using System.Text.Json;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Plugins.ForwardAPI;

/// <summary>
/// 将 NewAPI、Sub2API 和兼容 OpenAI API 的自定义站点接入 Router2API，
/// 并在已配置账号之间按模型、端点和账号权重进行路由。
/// </summary>
/// <param name="host">用于账号、模型目录、缓存、HTTP 和插件日志等宿主能力的访问入口。</param>
[PipelinePlugin(PipelineStage.Terminal, 100, "ForwardApiTerminal")]
[PlatformAdapter("forwardapi", PluginKey = "forwardapi", DisplayName = "ForwardAPI")]
[ModelCache(Disabled = true)]
[CredentialSchema(CredentialKind.Custom)]
public sealed partial class ForwardApiTerminal(IPluginHost host)
    : IPlatformTerminal, IPluginModule, IPluginMainPageProvider, IPluginScheduledTaskProvider, IDisposable
{
    private const string ForwardApiPlatform = "forwardapi";
    private const string ModelCacheKeyPrefix = "models:v1";
    private static readonly TimeSpan ModelCacheTtl = TimeSpan.FromDays(30);
    internal static readonly string[] SupportedEndpoints =
    [
        "/v1/chat/completions",
        "/v1/completions",
        "/v1/responses",
        "/v1/messages"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPluginServices _host = host.Services;
    private readonly SemaphoreSlim _snapshotRefreshGate = new(1, 1);
    private AccountModelSnapshot _accountModelSnapshot = AccountModelSnapshot.Empty;

    /// <summary>
    /// 获取插件注册的后台任务。当前任务会按计划为启用且开启自动签到的账号执行签到。
    /// </summary>
    public IReadOnlyList<ScheduledTaskRegistration> ScheduledTasks =>
    [
        new("forwardapi-daily-checkin", "0 10 10 * * *", DailyCheckInAsync, "每日早上十点十分执行已启用账号的自动签到")
    ];

    /// <summary>创建 ForwardAPI 账号池管理页面。</summary>
    /// <returns>由宿主隔离呈现的插件管理页面。</returns>
    public PluginMainPage GetMainPage() => CreateMainPage();

    /// <summary>
    /// 返回当前平台可用模型目录。需要强制刷新或内存快照尚未加载时，会从宿主账号存储重建；
    /// 对外只公布已启用账号明确允许使用的模型。
    /// </summary>
    /// <param name="context">宿主提供的平台、缓存和模型查询上下文。</param>
    /// <param name="cancellationToken">取消本次账号查询和缓存访问的令牌。</param>
    /// <returns>去重后的模型 ID 和显示名称列表。</returns>
    public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(
        ModelQueryContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref _accountModelSnapshot);
        var useFreshSnapshot = context.ForceRefresh || !snapshot.IsLoaded;
        if (context.ForceRefresh)
            snapshot = await RebuildAccountModelSnapshotAsync(_host.PluginKey, cancellationToken, forceRefresh: true);
        else if (!snapshot.IsLoaded)
            snapshot = await RebuildAccountModelSnapshotAsync(_host.PluginKey, cancellationToken, forceRefresh: false);

        var models = useFreshSnapshot
            ? snapshot.Models
            : await ReadCachedModelsAsync(ForwardApiPlatform, cancellationToken) ?? snapshot.Models;
        return models
            .Select(model => new ModelDescriptor(model, model))
            .ToArray();
    }

    /// <summary>
    /// 验证自定义凭据中是否包含可用的站点 URL 和 API Key；此检查不向上游发起网络请求。
    /// </summary>
    /// <param name="credential">包含 ForwardAPI 站点设置的宿主凭据。</param>
    /// <param name="cancellationToken">在开始校验前检查取消状态。</param>
    /// <returns>凭据格式有效与否以及对应的错误说明。</returns>
    public Task<CredentialValidationResult> ValidateCredentialAsync(
        Credential credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSettings(credential, out var settings))
            return Task.FromResult(new CredentialValidationResult(false, "ForwardAPI account settings are missing or invalid"));
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return Task.FromResult(new CredentialValidationResult(false, "API key is empty"));
        if (!TryValidateBaseUrl(settings.BaseUrl, out var error))
            return Task.FromResult(new CredentialValidationResult(false, error));
        if (!TryReadReplaceHeaders(ReadExtraParams(settings), out _, out var headerError))
            return Task.FromResult(new CredentialValidationResult(false, headerError));
        return Task.FromResult(new CredentialValidationResult(true));
    }

    /// <summary>
    /// 注册账号可选条件、模型权重、冷却和代理重试策略，让不支持请求模型或端点的账号不参与尝试。
    /// </summary>
    /// <param name="builder">宿主提供的插件策略注册器。</param>
    public void Configure(IPluginBuilder builder)
    {
        builder.AccountPolicy(policy => policy
            .SelectForRequest((account, request) => IsAccountEligible(account, request))
            .WeightBy((account, _) => GetAccountModelPolicy(account)?.Weight ?? int.MinValue));

        builder.ProxyPolicy(policy => policy
            .OnTransportFailure(() => FailureDecision(new PluginAttemptResult(PluginAttemptOutcome.Retry, IsTransportFailure: true)))
            .MaxAttempts(3)
            .AttemptTimeoutSeconds(60)
            .TotalTimeoutSeconds(180));
    }

    /// <summary>
    /// 启动插件并预热所管理账号的模型快照；缓存服务不可用时仍保留内存快照。
    /// </summary>
    /// <param name="context">包含本插件实例标识的启动上下文。</param>
    /// <param name="cancellationToken">取消启动和初始账号读取的令牌。</param>
    public async ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
    {
        await RebuildAccountModelSnapshotAsync(context.PluginKey, cancellationToken);
    }

    /// <summary>停止插件并释放用于模型快照刷新的同步资源。</summary>
    /// <param name="cancellationToken">宿主停止流程的取消令牌。</param>
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>释放账号模型快照刷新门。</summary>
    public void Dispose() => _snapshotRefreshGate.Dispose();

    /// <summary>判断账号是否启用，并且同时声明支持当前端点和请求模型。</summary>
    private bool IsAccountEligible(Account account, AdapterRequest request)
    {
        var policy = GetAccountModelPolicy(account);
        if (policy is null || !policy.Enabled)
            return false;
        if (!policy.Endpoints.Contains(NormalizeEndpoint(request.Endpoint), StringComparer.OrdinalIgnoreCase))
            return false;
        return policy.Models.Contains(request.Model, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>优先从当前不可变快照读取账号策略；快照未收录账号时从凭据即时构造策略。</summary>
    private AccountModelPolicy? GetAccountModelPolicy(Account account)
    {
        var snapshot = Volatile.Read(ref _accountModelSnapshot);
        if (snapshot.Accounts.TryGetValue(account.Id, out var cached))
            return cached;
        if (!TryReadSettings(account, out var settings))
            return null;
        return new AccountModelPolicy(settings.Enabled, settings.Weight, settings.Endpoints, ReadModels(account));
    }

    /// <summary>
    /// 从宿主重新读取平台账号，排除无效设置和已禁用账号，构造路由策略及去重模型目录，
    /// 然后一次性发布内存快照并更新可选共享缓存。
    /// </summary>
    private async Task<AccountModelSnapshot> RebuildAccountModelSnapshotAsync(
        string pluginKey,
        CancellationToken cancellationToken,
        bool forceRefresh = true)
    {
        await _snapshotRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref _accountModelSnapshot);
            if (!forceRefresh && current.IsLoaded) return current;

            var accounts = await _host.Accounts.ListAsync(ForwardApiPlatform, cancellationToken);
            var policies = new Dictionary<string, AccountModelPolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in accounts)
            {
                if (!TryReadSettings(account, out var settings)) continue;
                policies[account.Id] = new AccountModelPolicy(
                    settings.Enabled && account.Status.State is not (ResourceState.Disabled or ResourceState.Invalid),
                    settings.Weight,
                    settings.Endpoints,
                    ReadModels(account));
            }

            var models = policies.Values
                .Where(policy => policy.Enabled)
                .SelectMany(policy => policy.Models)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var snapshot = new AccountModelSnapshot(true, policies, models);
            Volatile.Write(ref _accountModelSnapshot, snapshot);
            await StoreCachedModelsAsync(ForwardApiPlatform, models, cancellationToken);
            return snapshot;
        }
        finally
        {
            _snapshotRefreshGate.Release();
        }
    }

    /// <summary>尝试读取共享缓存中的模型列表；缓存未配置或读取失败时返回空值并回退到账号快照。</summary>
    private async Task<string[]?> ReadCachedModelsAsync(string platform, CancellationToken cancellationToken)
    {
        try
        {
            var cache = _host.State.Shared;
            if (!cache.IsAvailable) return null;
            var value = await cache.GetStringAsync(GetModelCacheKey(platform), cancellationToken);
            return string.IsNullOrWhiteSpace(value)
                ? null
                : JsonSerializer.Deserialize<string[]>(value, JsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>尽力将模型目录写入可选共享缓存；缓存故障不影响插件内存模型快照。</summary>
    private async Task StoreCachedModelsAsync(
        string platform,
        string[] models,
        CancellationToken cancellationToken)
    {
        try
        {
            var cache = _host.State.Shared;
            if (cache.IsAvailable)
                await cache.SetStringAsync(
                    GetModelCacheKey(platform),
                    JsonSerializer.Serialize(models, JsonOptions),
                    ModelCacheTtl,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Redis is an optional cache; keep the in-memory snapshot available.
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Redis is an optional cache; keep the in-memory snapshot available.
        }
    }

    /// <summary>生成带平台和版本命名空间的共享模型缓存键。</summary>
    private static string GetModelCacheKey(string platform)
        => $"{ModelCacheKeyPrefix}:{platform}";

    /// <summary>为端点路径添加单个前导斜杠并移除首尾多余斜杠。</summary>
    private static string NormalizeEndpoint(string endpoint)
        => "/" + endpoint.Trim().Trim('/');

    /// <summary>从账号凭据委托读取站点设置。</summary>
    private static bool TryReadSettings(Account account, out ForwardApiSettings settings)
        => TryReadSettings(account.Credential, out settings);

    /// <summary>解析账号 JSON 设置，规范化端点并过滤本插件不支持的路径。</summary>
    private static bool TryReadSettings(Credential credential, out ForwardApiSettings settings)
    {
        settings = new ForwardApiSettings();
        if (credential is not CustomCredential custom
            || !custom.Fields.TryGetValue("settings", out var json)
            || string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            settings = JsonSerializer.Deserialize<ForwardApiSettings>(json, JsonOptions) ?? new ForwardApiSettings();
            settings.Endpoints = settings.Endpoints
                .Select(NormalizeEndpoint)
                .Where(endpoint => SupportedEndpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>读取账号被管理员允许转发的模型列表；未配置时返回空列表。</summary>
    private static string[] ReadModels(Account account)
        => HasConfiguredModels(account) ? ReadModelList(account, "models") : [];

    /// <summary>读取最近发现的候选模型目录，并兼容首次保存前旧版模型字段。</summary>
    private static string[] ReadAvailableModels(Account account)
    {
        var available = ReadModelList(account, "availableModels");
        return available.Length > 0 || HasConfiguredModels(account)
            ? available
            : ReadModelList(account, "models");
    }

    /// <summary>判断模型列表是否由当前账号配置流程明确设置。</summary>
    private static bool HasConfiguredModels(Account account)
        => account.Credential is CustomCredential custom
            && custom.Fields.TryGetValue("modelsConfigured", out var configured)
            && string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>反序列化指定账号凭据字段中的模型字符串数组并清理空项、空白和重复值。</summary>
    private static string[] ReadModelList(Account account, string fieldName)
    {
        if (account.Credential is not CustomCredential custom
            || !custom.Fields.TryGetValue(fieldName, out var json)
            || string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions)?
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>验证 Base URL 为无凭据、查询串或片段的绝对 HTTP(S) 地址。</summary>
    private static bool TryValidateBaseUrl(string? value, out string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Base URL must be an absolute http(s) URL without credentials, query, or fragment";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>从账号凭据反序列化的 ForwardAPI 站点和路由设置。</summary>
    private sealed class ForwardApiSettings
    {
        /// <summary>创建带有站点默认值的设置对象，供 JSON 反序列化和凭据校验使用。</summary>
        public ForwardApiSettings() { }

        /// <summary>站点适配类型，决定额度与默认签到接口策略。</summary>
        public string SiteType { get; set; } = "NewAPI";
        /// <summary>上游站点的绝对 HTTP(S) Base URL。</summary>
        public string BaseUrl { get; set; } = string.Empty;
        /// <summary>用于上游 API 请求的密钥；仅在服务端凭据中保存。</summary>
        public string ApiKey { get; set; } = string.Empty;
        /// <summary>可选的站点登录用户名，仅供需要网页登录的签到流程使用。</summary>
        public string Username { get; set; } = string.Empty;
        /// <summary>可选的站点登录密码，仅供需要网页登录的签到流程使用。</summary>
        public string Password { get; set; } = string.Empty;
        /// <summary>账号在同模型候选账号中的相对优先级。</summary>
        public int Weight { get; set; }
        /// <summary>该账号允许响应的受支持 API 路径集合。</summary>
        public string[] Endpoints { get; set; } = SupportedEndpoints.ToArray();
        /// <summary>是否将账号纳入每日自动签到任务。</summary>
        public bool AutoCheckIn { get; set; }
        /// <summary>账号是否参与模型目录和请求路由。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>站点认证、额度和签到接口的 JSON 扩展配置。</summary>
        public string ExtraParams { get; set; } = "{}";
    }

    /// <summary>单个账号在内存模型快照中的资格、权重和模型/端点范围。</summary>
    /// <param name="Enabled">账号是否满足启用及宿主状态要求。</param>
    /// <param name="Weight">参与同模型候选账号排序的权重。</param>
    /// <param name="Endpoints">账号允许响应的 API 路径。</param>
    /// <param name="Models">账号允许转发的模型 ID。</param>
    private sealed record AccountModelPolicy(bool Enabled, int Weight, string[] Endpoints, string[] Models);

    /// <summary>一次原子发布的账号策略及平台模型目录快照。</summary>
    /// <param name="IsLoaded">是否已经从宿主账号存储完成至少一次加载。</param>
    /// <param name="Accounts">按账号 ID 索引的路由策略。</param>
    /// <param name="Models">已启用账号允许使用的去重模型目录。</param>
    private sealed record AccountModelSnapshot(
        bool IsLoaded,
        IReadOnlyDictionary<string, AccountModelPolicy> Accounts,
        string[] Models)
    {
        /// <summary>插件尚未从账号存储加载数据时使用的空快照。</summary>
        public static AccountModelSnapshot Empty { get; } = new(
            false,
            new Dictionary<string, AccountModelPolicy>(StringComparer.OrdinalIgnoreCase),
            []);
    }
}
