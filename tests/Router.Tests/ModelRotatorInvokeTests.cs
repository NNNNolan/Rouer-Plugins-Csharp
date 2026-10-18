using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.ModelRotator;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Tests;

/// <summary>
/// ModelRotator 的请求路径测试：模型重写、故障轮转、客户端错误不轮转，
/// 以及手动激活只改 pin、不动已有连接（下一次请求才生效）。
/// </summary>
[TestClass]
public sealed class ModelRotatorInvokeTests
{
    [TestMethod]
    public async Task InvokeRewritesModelAndPreservesOriginalBody()
    {
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.OK, """{"id":"x","choices":[]}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(200, result.Attempt.StatusCode, "成功尝试必须带状态码，否则详情页显示未收到响应");
        CollectionAssert.AreEqual(OnlyModelA, harness.Http.RequestedModels.ToArray());
        var sent = JsonNode.Parse(harness.Http.SentBodies.Single())!.AsObject();
        Assert.AreEqual("model-a", sent["model"]!.GetValue<string>());
        Assert.AreEqual(0.4, sent["temperature"]!.GetValue<double>());
        Assert.IsFalse(sent.ContainsKey("endpoint"), "宿主内部路由字段不应转发给上游");
        Assert.IsFalse(sent.ContainsKey("overrides"));
        Assert.IsFalse(sent.ContainsKey("models"));
    }

    [TestMethod]
    public async Task StreamedSuccessReportsStatusCodeToHostAttemptDetail()
    {
        // 实测过：只有 AdapterResponse.StatusCode 而没有 attempt 状态码时，宿主请求详情显示
        // “未收到 HTTP 响应”，成功的流式请求看起来像失败。
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n"));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(200, result.Attempt.StatusCode, "宿主靠 attempt 状态码渲染请求详情");
        Assert.AreEqual(PluginAttemptOutcome.Healthy, result.Attempt.Outcome);
        var detail = result.AttemptDetails!.Single();
        Assert.AreEqual(200, detail.StatusCode);
        Assert.AreEqual("success", detail.Outcome);
    }

    [TestMethod]
    public async Task FailedTargetCountsFailureAndRotatesToNextTarget()
    {
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.InternalServerError, """{"error":"boom"}"""));
        harness.Http.Respond(_ => Json(HttpStatusCode.OK, """{"id":"x","choices":[]}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        CollectionAssert.AreEqual(ModelAThenB, harness.Http.RequestedModels.ToArray());
        var failures = harness.Accounts.Failures();
        Assert.AreEqual(1, failures["route-a\u001fmodel-a"]!.GetValue<int>(), "失败应计入该实际模型的故障计数");
        Assert.IsNull(failures["route-a\u001fmodel-b"], "成功的目标不应留下故障计数");
    }

    [TestMethod]
    public async Task BareTargetModelIsConfigErrorSoNoCountAndNoRotate()
    {
        // 宿主对裸模型名回 500 "model must use the platform/model format"：换哪个目标都一样，
        // 必须当成配置错误停下，否则会把整条路由的目标逐个禁掉。
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.InternalServerError,
            """{"error":{"message":"model must use the platform/model format","type":"server_error"}}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(500, result.Response.StatusCode);
        CollectionAssert.AreEqual(OnlyModelA, harness.Http.RequestedModels.ToArray(), "不应轮转到下一个目标");
        Assert.AreEqual("target_model_unroutable", result.Attempt.Decision?.ReasonCode);
        Assert.AreEqual(0, harness.Accounts.Failures().Count, "配置错误不应计入故障计数");
    }

    [TestMethod]
    public async Task ClientInputErrorDoesNotCountOrRotate()
    {
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.BadRequest, """{"error":{"message":"bad max_tokens"}}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(400, result.Response.StatusCode);
        Assert.AreEqual(1, harness.Http.RequestedModels.Count, "客户端输入错误不应继续轮转");
        Assert.IsEmpty(harness.Accounts.Failures(), "客户端输入错误不应计入模型故障");
    }

    [TestMethod]
    public async Task ManualActivationWritesPinWithoutAnyHttpRequestAndAppliesToNextRequest()
    {
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        using var terminal = harness.Build();

        var activation = await terminal.ActivateVirtualAsync(Context(new { virtualModel = "route-a", model = "model-b" }));

        Assert.AreEqual(200, activation.StatusCode);
        Assert.AreEqual(0, harness.Http.SendCount, "手动激活不能发起任何上游请求，也就不会断开或重置已有连接");
        Assert.AreEqual("model-b", harness.Accounts.Active()["route-a"]!.GetValue<string>(), "pin 应立即写入配置");

        harness.Http.Respond(_ => Json(HttpStatusCode.OK, """{"id":"x","choices":[]}"""));
        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        CollectionAssert.AreEqual(OnlyModelB, harness.Http.RequestedModels.ToArray(), "下一次请求应从新 pin 开始");
        Assert.AreEqual("model-b", harness.Accounts.Active()["route-a"]!.GetValue<string>(), "成功后 sticky 保持当前 pin");
    }

    [TestMethod]
    public async Task ManualActivationRejectsUnknownModelAndKeepsExistingPin()
    {
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        using var terminal = harness.Build();
        await terminal.ActivateVirtualAsync(Context(new { virtualModel = "route-a", model = "model-b" }));

        var rejected = await terminal.ActivateVirtualAsync(Context(new { virtualModel = "route-a", model = "nope" }));

        Assert.AreEqual(400, rejected.StatusCode);
        Assert.AreEqual("model-b", harness.Accounts.Active()["route-a"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DisabledTargetIsSkipped()
    {
        var harness = new Harness();
        harness.Accounts.Seed(
            """{"version":3,"settings":{"baseUrl":"http://127.0.0.1:8080"},"virtuals":[{"virtualModel":"route-a","targets":["model-a","model-b"],"failureThreshold":1}],"state":{"disabled":{"route-a\u001fmodel-a":"2026-10-17T00:00:00.000Z"}}}""",
            version: 4);
        harness.Http.Respond(_ => Json(HttpStatusCode.OK, """{"id":"x","choices":[]}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        CollectionAssert.AreEqual(OnlyModelB, harness.Http.RequestedModels.ToArray(), "被禁用的目标应跳过");
    }

    [TestMethod]
    public async Task Upstream403FreeTierErrorCountsFailureAndRotatesToNextTarget()
    {
        // 实测：opencode 免费层按数据中心 IP 段回 403 FreeTierError，宿主对 /v1 的鉴权失败
        // 一律回 401，所以 403 只可能是「这个目标在当前出口不可用」。旧实现把它当成回环 Key
        // 配错直接收手，导致等满 21 秒首字后一个模型都不换；现在必须计数并继续轮转。
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.Forbidden,
            """{"type":"error","error":{"type":"FreeTierError","message":"OpenCode's free tier can only be used from within OpenCode"}}"""));
        harness.Http.Respond(_ => Json(HttpStatusCode.OK, """{"id":"x","choices":[]}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        CollectionAssert.AreEqual(ModelAThenB, harness.Http.RequestedModels.ToArray(), "403 应继续轮转到下一个目标");
        var failures = harness.Accounts.Failures();
        Assert.AreEqual(1, failures["route-a\u001fmodel-a"]!.GetValue<int>(), "被上游拒绝的目标应计入故障计数");
        Assert.IsNull(failures["route-a\u001fmodel-b"], "成功的目标不应留下故障计数");
    }

    [TestMethod]
    public async Task Loopback401InvalidApiKeyStopsWithoutRotation()
    {
        // 宿主 AuthMiddleware 的回环 Key 校验固定是 401 + {"error":"invalid api key"}：
        // 所有目标共用同一把 Key，轮转没有任何意义，必须立刻收手且不计数。
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.Unauthorized, """{"error":"invalid api key"}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(401, result.Response.StatusCode);
        CollectionAssert.AreEqual(OnlyModelA, harness.Http.RequestedModels.ToArray(), "回环鉴权失败不应轮转");
        Assert.AreEqual("loopback_auth_error", result.Attempt.Decision?.ReasonCode);
        Assert.IsEmpty(harness.Accounts.Failures(), "配置级鉴权失败不应计入模型故障");
    }

    [TestMethod]
    public async Task Upstream401WithProviderBodyCountsFailureAndRotatesToNextTarget()
    {
        // 上游自己回 401（凭据/账号级），不是宿主回环 Key 的错：换一个目标可能就好了。
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.Unauthorized,
            """{"error":{"message":"Incorrect API key provided: sk-***","type":"invalid_request_error"}}"""));
        harness.Http.Respond(_ => Json(HttpStatusCode.OK, """{"id":"x","choices":[]}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        CollectionAssert.AreEqual(ModelAThenB, harness.Http.RequestedModels.ToArray(), "上游 401 应继续轮转到下一个目标");
        Assert.AreEqual(1, harness.Accounts.Failures()["route-a\u001fmodel-a"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Upstream401WithInvalidApiKeyPhraseInWrappedBodyStillRotates()
    {
        // 对抗回归：上游把「Invalid API key provided」包在外层 message 里（最常见的
        // 凭据报错文案）。它不是宿主回环 Key 的错（宿主回环失败只回根级
        // {"error":"invalid api key"}），按子串模糊匹配会误判成一个模型都不换；
        // JSON 精确匹配后必须继续轮转。
        var harness = new Harness();
        harness.Accounts.Seed(ConfigJson, version: 7);
        harness.Http.Respond(_ => Json(HttpStatusCode.Unauthorized,
            """{"error":{"message":"Invalid API key provided","type":"invalid_request_error"}}"""));
        harness.Http.Respond(_ => Json(HttpStatusCode.OK, """{"id":"x","choices":[]}"""));
        using var terminal = harness.Build();

        var result = await terminal.InvokeAsync(harness.Attempt("route-a"));

        Assert.AreEqual(200, result.Response.StatusCode);
        CollectionAssert.AreEqual(ModelAThenB, harness.Http.RequestedModels.ToArray(), "外层包装的 invalid api key 文案不是回环鉴权失败，应继续轮转");
        Assert.AreNotEqual("loopback_auth_error", result.Attempt.Decision?.ReasonCode, "不应落入回环鉴权分支");
        Assert.AreEqual(1, harness.Accounts.Failures()["route-a\u001fmodel-a"]!.GetValue<int>(), "上游凭据拒绝应计入该目标故障计数");
    }

    private static readonly string[] OnlyModelA = ["model-a"];

    private static readonly string[] ModelAThenB = ["model-a", "model-b"];

    private static readonly string[] OnlyModelB = ["model-b"];

    private const string ConfigJson =
        """{"version":3,"settings":{"baseUrl":"http://127.0.0.1:8080","firstWaitSeconds":30},"virtuals":[{"virtualModel":"route-a","targets":["model-a","model-b"],"failureThreshold":3}]}""";

    private static PluginHttpContext Context(object body) => new()
    {
        PluginKey = "modelrotator",
        Platform = "modelrotator",
        Body = body,
        CancellationToken = CancellationToken.None
    };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    /// <summary>只使用 Router.Contracts 能力的测试宿主：账号、日志、模型缓存与 HTTP 均为本地替身。</summary>
    private sealed class Harness
    {
        private readonly Mock<IPluginServices> _services = new();
        private readonly Mock<IPluginHost> _host = new(MockBehavior.Strict);

        public Harness()
        {
            Accounts = new RecordingAccounts();
            Http = new RecordingHttp();
            _services.SetupGet(services => services.PluginKey).Returns("modelrotator");
            _services.SetupGet(services => services.Accounts).Returns(Accounts);
            _services.SetupGet(services => services.Log).Returns(Mock.Of<IPluginLogSink>());
            _services.SetupGet(services => services.Models).Returns(Mock.Of<IPluginModels>());
            _services.SetupGet(services => services.State)
                .Returns(new PluginStateServices(Mock.Of<IPluginStateStore>(), Mock.Of<IPluginStateStore>()));
            _services.SetupGet(services => services.Http).Returns(Mock.Of<IPluginHttpServices>());
            _host.SetupGet(host => host.PluginKey).Returns("modelrotator");
            _host.SetupGet(host => host.Services).Returns(_services.Object);
        }

        public RecordingAccounts Accounts { get; }

        public RecordingHttp Http { get; }

        public ModelRotatorTerminal Build() => new(_host.Object);

        public PluginAttemptContext Attempt(string model) => new()
        {
            Request = new AdapterRequest
            {
                Model = model,
                Endpoint = "/v1/chat/completions",
                OriginalBody = JsonSerializer.Deserialize<JsonElement>(
                    """{"model":"route-a","temperature":0.4,"endpoint":"ignored","overrides":{"x":1},"models":["y"]}"""),
                Stream = false
            },
            PlatformName = "modelrotator",
            PluginKey = "modelrotator",
            Account = Accounts.Account!,
            HttpClient = Http,
            CancellationToken = CancellationToken.None,
            TraceId = "test-trace"
        };
    }

    /// <summary>内存账号替身：实现真实 CAS 语义，便于断言插件写回的内容。</summary>
    private sealed class RecordingAccounts : IPluginAccounts
    {
        private Account? _account;

        public Account? Account => _account;

        public void Seed(string configJson, long version)
        {
            _account = new Account
            {
                Id = ModelRotatorTerminal.ConfigId,
                PluginKey = "modelrotator",
                Platform = "modelrotator",
                Label = "模型轮转配置",
                CredentialVersion = version,
                Credential = new CustomCredential(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["role"] = "config",
                    ["config"] = configJson
                })
            };
        }

        public JsonObject Failures() => Document()["state"]?["failures"] as JsonObject ?? new JsonObject();

        public JsonObject Active() => Document()["state"]?["active"] as JsonObject ?? new JsonObject();

        private JsonObject Document()
        {
            var json = ((CustomCredential)_account!.Credential).Fields["config"]!;
            return JsonNode.Parse(json)!.AsObject();
        }

        public Task<Account?> GetAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(_account is not null && _account.Id == id ? _account : null);

        public Task<IReadOnlyList<Account>> ListAsync(string? platform = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Account>>(_account is null ? [] : [_account]);

        public Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default)
        {
            _account = account;
            return Task.FromResult(_account);
        }

        public Task<Account> RefreshAsync(string id, Func<Account, CancellationToken, Task<Account>> refresh, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetCooldownAsync(string id, DateTimeOffset until, string reason, int? statusCode = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ClearCooldownAsync(string id, string expectedReason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DisableAsync(string id, string reason, int? statusCode = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Account?> CompareExchangeCredentialAsync(string id, long expectedVersion, Credential credential, CancellationToken cancellationToken = default)
        {
            if (_account is null || _account.Id != id || _account.CredentialVersion != expectedVersion)
                return Task.FromResult<Account?>(null);
            _account.Credential = credential;
            _account.CredentialVersion++;
            return Task.FromResult<Account?>(_account);
        }
    }

    /// <summary>记录请求体与模型顺序的 HttpClient 替身；响应按注册顺序出队。</summary>
    private sealed class RecordingHttp : IPluginHttpClient
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public List<string> RequestedModels { get; } = [];

        public List<string> SentBodies { get; } = [];

        public int SendCount { get; private set; }

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responses.Enqueue(responder);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default)
            => SendAsync(request, useProxyPool: false, completionOption, cancellationToken);

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool useProxyPool,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            SendCount++;
            RequestedModels.Add(JsonNode.Parse(body)?["model"]?.GetValue<string>() ?? "");
            SentBodies.Add(body);
            if (_responses.Count == 0) throw new HttpRequestException("测试未提供响应");
            return _responses.Dequeue()(request);
        }
    }
}