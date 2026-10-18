using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Plugins.ModelRotator;

/// <summary>将单个虚拟模型映射到有序真实模型，并在失败后由插件内部执行有限轮转。</summary>
[PipelinePlugin(PipelineStage.Terminal, 100, "ModelRotatorTerminal")]
[PlatformAdapter("modelrotator", PluginKey = "modelrotator", DisplayName = "模型轮转")]
[ModelCache(Disabled = true)]
[CredentialSchema(CredentialKind.Custom)]
public sealed partial class ModelRotatorTerminal(IPluginHost host)
    : IPlatformTerminal, IPluginModule, IPluginMainPageProvider, IPluginScheduledTaskProvider, IDisposable
{
    /// <summary>本插件的平台名，与 <see cref="PlatformAdapterAttribute"/> 声明一致；仅供宿主按平台过滤时使用。</summary>
    internal const string Platform = "modelrotator";
    // 账号在宿主按插件 key 隔离；配置账号必须挂在本插件命名空间下，沿用 JS 版前缀会被宿主以
    // "account belongs to another plugin" 拒绝并让激活失败回滚。
    internal const string ConfigId = "modelrotator:config";
    internal const string DocField = "config";
    internal const string RoleField = "role";
    internal const string ConfigRole = "config";
    internal const int DocVersion = 3;
    internal const int MaxVirtuals = 200;
    internal const int MaxTargets = 50;
    internal const int MaxModelLength = 200;
    internal const int MaxAttempts = 5;
    internal const int MaxDocBytes = 40 * 1024;
    internal const int MaxBufferedResponseBytes = 600 * 1024;
    internal static readonly string[] SupportedEndpoints =
    [
        "/v1/chat/completions", "/v1/completions", "/v1/embeddings", "/v1/responses", "/v1/messages"
    ];

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IPluginServices _services = host.Services;
    private readonly SemaphoreSlim _docMutationGate = new(1, 1);

    public PluginMainPage GetMainPage() => CreateMainPage();

    public IReadOnlyList<ScheduledTaskRegistration> ScheduledTasks =>
    [
        new("modelrotator-health-probe", "0 * * * * *", RunScheduledProbeAsync,
            "按配置间隔探测已禁用的实际模型")
    ];

    public void Configure(IPluginBuilder builder)
    {
        // 轮转完全由本插件完成；避免宿主在内部模型失败后再次重放整次请求。
        builder.AccountPolicy(policy => policy.SelectAccount(IsConfigAccount));
        builder.ProxyPolicy(policy => policy
            .MaxAttempts(1)
            .AttemptTimeoutSeconds(180)
            .TotalTimeoutSeconds(180));
    }

    public async ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
    {
        _ = await ReadDocumentAsync(cancellationToken);
        await EnsureConfigAccountAsync(cancellationToken);
        _services.Models.Invalidate(Platform);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _docMutationGate.Dispose();

    public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(
        ModelQueryContext context,
        CancellationToken cancellationToken)
    {
        var doc = await ReadDocumentAsync(cancellationToken);
        return doc.Virtuals
            .Where(route => route.Enabled && route.Targets.Count > 0)
            .Select(route => new ModelDescriptor(route.VirtualModel, route.VirtualModel))
            .ToArray();
    }

    public Task<CredentialValidationResult> ValidateCredentialAsync(
        Credential credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var valid = credential is CustomCredential custom
            && string.Equals(custom.Fields.GetValueOrDefault(RoleField), ConfigRole, StringComparison.Ordinal)
            && custom.Fields.ContainsKey(DocField);
        return Task.FromResult(valid
            ? new CredentialValidationResult(true)
            : new CredentialValidationResult(false, "Model rotator config credential is missing"));
    }

    private static bool IsConfigAccount(Account account) =>
        string.Equals(account.Platform, Platform, StringComparison.OrdinalIgnoreCase)
        && (account.Id == ConfigId
            || account.Credential is CustomCredential custom
                && string.Equals(custom.Fields.GetValueOrDefault(RoleField), ConfigRole, StringComparison.Ordinal));

    private static PluginAttemptResult Decision(
        PluginFailureKind kind,
        PluginRetryAction retry,
        string code,
        PluginAccountAction accountAction = PluginAccountAction.None,
        DateTimeOffset? cooldownUntil = null,
        PluginProxyAction proxyAction = PluginProxyAction.None,
        string? reason = null,
        int? statusCode = null)
    {
        var decision = new PluginAttemptDecision
        {
            FailureKind = kind,
            Retry = retry,
            AccountAction = accountAction,
            AccountCooldownUntil = cooldownUntil,
            ProxyAction = proxyAction,
            ReasonCode = code
        };
        decision.Validate();
        // StatusCode 必须带上：宿主用 attempt 的 statusCode 渲染请求详情，
        // 空值会显示成「未收到 HTTP 响应」，让成功的请求看起来像失败。
        return decision.ToResult(statusCode, reason);
    }

    private static PluginInvocationResult Fail(AdapterResponse response, string code, string reason,
        PluginFailureKind kind = PluginFailureKind.Plugin,
        PluginRetryAction retry = PluginRetryAction.None) =>
        new(response, Decision(kind, retry, code, reason: reason,
            statusCode: response.StatusCode > 0 ? response.StatusCode : null), []);

    private static string NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "/v1/chat/completions";
        var path = endpoint.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        return path.StartsWith('/') ? path : "/" + path;
    }

    internal const string DefaultBaseUrl = "http://127.0.0.1:8080";

    /// <summary>
    /// 读取路径的 baseUrl 清理：不抛异常。任何无效或非本机回环地址一律回退到默认回环端口——
    /// 防止畸形配置让插件无法启动（所有端点都会读文档），也保证「只向本机回环转发」的 SSRF 不变式
    /// 在读取时（而不仅是保存时）成立。
    /// </summary>
    private static string SanitizeBaseUrl(string? baseUrl) =>
        TryNormalizeBaseUrl(baseUrl, out var normalized, out _) ? normalized : DefaultBaseUrl;

    /// <summary>保存路径的严格校验：错误原因写进 error 回显给用户。</summary>
    private static bool TryNormalizeBaseUrl(string? baseUrl, out string normalized, out string error)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            normalized = DefaultBaseUrl;
            error = "平台回环地址必须是有效的 HTTP(S) URL，且不能包含凭据、查询或片段";
            return false;
        }
        if (!IsLocalLoopback(uri))
        {
            normalized = DefaultBaseUrl;
            error = "平台回环地址必须指向本机 loopback，不能配置外部 origin";
            return false;
        }
        normalized = value.TrimEnd('/');
        error = "";
        return true;
    }

    private async Task EnsureConfigAccountAsync(CancellationToken cancellationToken)
    {
        var existing = await _services.Accounts.GetAsync(ConfigId, cancellationToken);
        if (existing is not null) return;
        var doc = EmptyDocument();
        await _services.Accounts.SaveAsync(new Account
        {
            Id = ConfigId,
            PluginKey = _services.PluginKey,
            Platform = Platform,
            Label = "模型轮转配置",
            Credential = ToCredential(doc)
        }, cancellationToken);
    }

    private static CustomCredential ToCredential(RotatorDocument doc)
    {
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocBytes)
            throw new InvalidOperationException("模型轮转配置超过 40 KiB 安全上限");
        return new CustomCredential(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RoleField] = ConfigRole,
            [DocField] = json
        });
    }

    private static bool TryReadDocument(Account? account, out RotatorDocument doc)
    {
        if (account?.Credential is CustomCredential custom
            && custom.Fields.TryGetValue(DocField, out var json)
            && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                doc = NormalizeDocument(JsonSerializer.Deserialize<RotatorDocument>(json, JsonOptions));
                return true;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException) { }
        }
        doc = EmptyDocument();
        return account is null;
    }
}
