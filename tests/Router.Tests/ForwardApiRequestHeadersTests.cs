using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Plugins.ForwardAPI;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Tests;

/// <summary>通过 Contracts HTTP Mock 验证插件实际转发路径，不连接宿主或真实上游。</summary>
[TestClass]
public sealed class ForwardApiRequestHeadersTests
{
    private static readonly string[] ModelIds = ["model-a"];

    /// <summary>所有转发端点共用最后覆盖逻辑，兼容流式和非流式响应。</summary>
    /// <param name="endpoint">下游模型请求端点。</param>
    /// <param name="stream">是否请求流式响应。</param>
    /// <returns>异步测试任务。</returns>
    [TestMethod]
    [DataRow("/v1/chat/completions", false)]
    [DataRow("/v1/chat/completions", true)]
    [DataRow("/v1/completions", false)]
    [DataRow("/v1/completions", true)]
    [DataRow("/v1/responses", false)]
    [DataRow("/v1/responses", true)]
    [DataRow("/v1/messages", false)]
    [DataRow("/v1/messages", true)]
    public async Task ReplaceHeadersOverrideAndAddHeaders(string endpoint, bool stream)
    {
        const string extraParams = """
            {
              "apiKeyHeader": "X-Upstream-Key",
              "ReplaceHeaders": {
                "User-Agent": "first-value",
                "user-agent": "claude-cli/2.1.161 (external, cli)",
                "X-Added": "configured",
                "X-Upstream-Key": "replacement-key",
                "Content-Type": "application/json; profile=forwardapi",
                "anthropic-version": "2023-06-01"
              }
            }
            """;
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        var context = CreateContext(extraParams, endpoint, stream, request =>
        {
            // User-Agent 的类型化解析会拆成产品和注释，原始值才对应实际替换的头。
            Assert.AreEqual("claude-cli/2.1.161 (external, cli)", request.Headers.NonValidated["User-Agent"].Single());
            Assert.AreEqual("configured", request.Headers.GetValues("X-Added").Single());
            Assert.AreEqual("replacement-key", request.Headers.GetValues("X-Upstream-Key").Single());
            Assert.AreEqual("trace-123", request.Headers.GetValues("X-Trace").Single());
            Assert.AreEqual("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.AreEqual("application/json; profile=forwardapi", request.Content!.Headers.GetValues("Content-Type").Single());
        });

        var result = await terminal.InvokeAsync(context);

        Assert.IsTrue(result.Response.IsSuccess);
        Assert.AreEqual(1, Mock.Get(context.HttpClient).Invocations.Count);
        Assert.AreEqual("downstream-client", context.Request.RequestHeaders["User-Agent"]);
        Assert.AreEqual("downstream-key", context.Request.RequestHeaders["X-Upstream-Key"]);
        if (result.Response.RawStream is { } rawStream)
            await foreach (var _ in rawStream) { }
    }

    /// <summary>管理员显式配置的认证替换值优先于账号默认生成值。</summary>
    /// <param name="endpoint">下游模型请求端点。</param>
    /// <param name="header">当前协议默认使用的认证头。</param>
    /// <returns>异步测试任务。</returns>
    [TestMethod]
    [DataRow("/v1/chat/completions", "Authorization")]
    [DataRow("/v1/messages", "X-Api-Key")]
    public async Task ReplaceHeadersOverrideDefaultAuthentication(string endpoint, string header)
    {
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        var extraParams = JsonSerializer.Serialize(new { ReplaceHeaders = new Dictionary<string, string> { [header] = "configured-key" } });
        var context = CreateContext(extraParams, endpoint, inspect: request =>
            Assert.AreEqual("configured-key", request.Headers.GetValues(header).Single()));

        Assert.IsTrue((await terminal.InvokeAsync(context)).Response.IsSuccess);
        Assert.AreEqual(1, Mock.Get(context.HttpClient).Invocations.Count);
    }

    /// <summary>不同账号不共享覆盖配置；未设置和空对象均沿用下游及默认认证头。</summary>
    /// <returns>异步测试任务。</returns>
    [TestMethod]
    public async Task ReplaceHeadersAreAccountScopedAndOptional()
    {
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        foreach (var (extraParams, expected) in new[]
        {
            ("""{"ReplaceHeaders":{"User-Agent":"configured-client"}}""", "configured-client"),
            ("{}", "downstream-client"),
            ("""{"ReplaceHeaders":{}}""", "downstream-client")
        })
        {
            var context = CreateContext(extraParams, inspect: request =>
            {
                Assert.AreEqual(expected, request.Headers.GetValues("User-Agent").Single());
                Assert.AreEqual("Bearer secret-upstream-key", request.Headers.GetValues("Authorization").Single());
            });

            Assert.IsTrue((await terminal.InvokeAsync(context)).Response.IsSuccess);
            Assert.AreEqual(1, Mock.Get(context.HttpClient).Invocations.Count);
        }
    }

    /// <summary>首次获取、已有账号获取和刷新模型共用请求头覆盖规则。</summary>
    /// <param name="keyHeader">站点使用的默认或自定义 API Key 请求头。</param>
    /// <returns>异步测试任务。</returns>
    [TestMethod]
    [DataRow("Authorization")]
    [DataRow("X-Upstream-Key")]
    public async Task ModelDiscoveryAndRefreshApplyReplaceHeaders(string keyHeader)
    {
        const string userAgent = "claude-cli/2.1.161 (external, cli)";
        var extraParams = JsonSerializer.Serialize(new
        {
            apiKeyHeader = keyHeader,
            ReplaceHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "first-value", ["user-agent"] = userAgent,
                [keyHeader.ToLowerInvariant()] = "replacement-key", ["X-Added"] = "configured"
            }
        });
        var host = PluginTestHost.Create("forwardapi");
        using var terminal = new ForwardApiTerminal(host);
        var account = CreateContext(extraParams).Account;
        Mock.Get(host.Services.Accounts).Setup(value => value.GetAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        Mock.Get(host.Services.Accounts).Setup(value => value.SaveAsync(account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var requests = new List<Dictionary<string, string[]>>();
        var handler = new Mock<HttpMessageHandler>();
        using var handlerLifetime = handler.Object;
        handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage request, CancellationToken _) =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                Assert.AreEqual("https://upstream.example/v1/models", request.RequestUri!.AbsoluteUri);
                requests.Add(request.Headers.NonValidated.ToDictionary(
                    header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"model-a"},{"id":"model-b"}]}""")
                });
            });
        Mock.Get(host.Services.Http).Setup(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() =>
            {
                var client = new HttpClient(handler.Object, disposeHandler: false);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "default-client");
                return client;
            });
        var fresh = new PluginHttpContext
        {
            PluginKey = "forwardapi", Platform = "forwardapi",
            Body = JsonSerializer.SerializeToElement(new
            {
                siteType = "Custom", baseUrl = "https://upstream.example/v1", apiKey = "secret-upstream-key",
                extraParams = JsonSerializer.Deserialize<JsonElement>(extraParams)
            })
        };
        var existing = new PluginHttpContext
        {
            PluginKey = "forwardapi", Platform = "forwardapi",
            Body = JsonSerializer.SerializeToElement(new { id = account.Id })
        };

        var results = new[]
        {
            await terminal.DiscoverModelsAsync(fresh),
            await terminal.DiscoverModelsAsync(existing),
            await terminal.RefreshModelsAsync(existing)
        };

        Assert.IsTrue(results.All(result => result.StatusCode == 200));
        Assert.AreEqual(3, requests.Count);
        foreach (var headers in requests)
        {
            Assert.AreEqual(userAgent, headers.GetValueOrDefault("User-Agent")?.Single());
            Assert.AreEqual("replacement-key", headers.GetValueOrDefault(keyHeader)?.Single());
            Assert.AreEqual("configured", headers.GetValueOrDefault("X-Added")?.Single());
        }
        Assert.AreEqual("""["model-a"]""", ((CustomCredential)account.Credential).Fields["models"]);
    }

    /// <summary>无效替换配置在凭据、管理端点和转发入口均被拒绝，不发送请求。</summary>
    /// <param name="extraParams">包含无效 ReplaceHeaders 的额外参数 JSON。</param>
    /// <returns>异步测试任务。</returns>
    [TestMethod]
    [DataRow("""{"ReplaceHeaders":null}""")]
    [DataRow("""{"ReplaceHeaders":[]}""")]
    [DataRow("""{"ReplaceHeaders":"{}"}""")]
    [DataRow("""{"ReplaceHeaders":{"X-Test":1}}""")]
    [DataRow("""{"ReplaceHeaders":{"X-Test":null}}""")]
    [DataRow("""{"ReplaceHeaders":{"Bad Header":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"User-Agent":"client\r\nX-Injected: value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"User-Agent":"client\u0085value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Host":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Connection":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Content-Length":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Transfer-Encoding":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Upgrade":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Proxy-Authorization":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Cookie":"value"}}""")]
    [DataRow("""{"ReplaceHeaders":{"Set-Cookie":"value"}}""")]
    public async Task InvalidReplaceHeadersAreRejected(string extraParams)
    {
        var host = PluginTestHost.Create("forwardapi");
        using var terminal = new ForwardApiTerminal(host);
        var context = CreateContext(extraParams);
        Mock.Get(host.Services.Accounts).Setup(value => value.GetAsync(context.Account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context.Account);

        var result = await terminal.InvokeAsync(context);

        Assert.AreEqual(400, result.Response.StatusCode);
        Assert.AreEqual(0, Mock.Get(context.HttpClient).Invocations.Count);
        Assert.IsFalse((await terminal.ValidateCredentialAsync(context.Account.Credential, CancellationToken.None)).Success);
        var refresh = await terminal.RefreshModelsAsync(new PluginHttpContext
        {
            PluginKey = "forwardapi", Platform = "forwardapi",
            Body = JsonSerializer.SerializeToElement(new { id = context.Account.Id })
        });
        Assert.AreEqual(400, refresh.StatusCode);
        Mock.Get(host.Services.Http).Verify(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);

        var management = new PluginHttpContext
        {
            PluginKey = "forwardapi", Platform = "forwardapi",
            Body = JsonSerializer.SerializeToElement(new
            {
                label = "invalid", siteType = "Custom", baseUrl = "https://upstream.example/v1", apiKey = "secret-upstream-key",
                extraParams = JsonSerializer.Deserialize<JsonElement>(extraParams),
                models = ModelIds, availableModels = ModelIds
            })
        };
        foreach (var response in new[] { await terminal.DiscoverModelsAsync(management), await terminal.SaveAccountAsync(management) })
        {
            Assert.AreEqual(400, response.StatusCode);
            StringAssert.Contains(JsonSerializer.Serialize(response.Body), "ReplaceHeaders");
        }
    }

    private static PluginAttemptContext CreateContext(
        string extraParams,
        string endpoint = "/v1/chat/completions",
        bool stream = false,
        Action<HttpRequestMessage>? inspect = null)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) =>
            {
                inspect?.Invoke(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            });
        return new PluginAttemptContext
        {
            PluginKey = "forwardapi", PlatformName = "forwardapi", HttpClient = client.Object,
            CancellationToken = CancellationToken.None,
            Account = new Account
            {
                PluginKey = "forwardapi", Platform = "forwardapi",
                Credential = new CustomCredential(new Dictionary<string, string?>
                {
                    ["settings"] = JsonSerializer.Serialize(new
                    {
                        siteType = "Custom", baseUrl = "https://upstream.example/v1", apiKey = "secret-upstream-key", extraParams
                    }),
                    ["modelsConfigured"] = "true", ["models"] = """["model-a"]"""
                })
            },
            Request = new AdapterRequest
            {
                Model = "model-a", Endpoint = endpoint, Stream = stream,
                OriginalBody = JsonSerializer.SerializeToElement(new { model = "forwardapi/model-a" }),
                RequestHeaders =
                {
                    ["uSeR-aGeNt"] = "downstream-client", ["X-Trace"] = "trace-123", ["x-upstream-key"] = "downstream-key",
                    ["anthropic-version"] = "old-version", ["Content-Type"] = "application/downstream"
                }
            }
        };
    }
}
