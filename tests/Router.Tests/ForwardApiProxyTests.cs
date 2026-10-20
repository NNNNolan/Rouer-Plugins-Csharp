using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Plugins.ForwardAPI;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Tests;

/// <summary>仅使用 Contracts Mock 和回环 TCP 代理验证出口选择、认证和响应生命周期。</summary>
[TestClass]
public sealed class ForwardApiProxyTests
{
    private const string ProxyPassword = "test-proxy-password";
    private static readonly string[] Models = ["model-a"];

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("direct", false)]
    [DataRow("pool", true)]
    public async Task ModelRequestSelectsExpectedHostTransport(string? mode, bool expectedPool)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), expectedPool,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        var context = Attempt(CreateAccount(mode), client.Object);

        var result = await terminal.InvokeAsync(context);

        Assert.IsTrue(result.Response.IsSuccess);
        client.Verify(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), expectedPool,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExplicitNullProxySettingsRemainDirect()
    {
        var account = CreateAccount(null);
        var fields = ((CustomCredential)account.Credential).Fields;
        var settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(fields["settings"]!)!;
        foreach (var name in new[] { "proxyMode", "proxyUrl", "proxyUsername", "proxyPassword" }) settings[name] = null;
        var updated = new Dictionary<string, string?>(fields) { ["settings"] = JsonSerializer.Serialize(settings) };
        account.Credential = new CustomCredential(updated);
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        Assert.IsTrue((await terminal.InvokeAsync(Attempt(account, client.Object))).Response.IsSuccess);
    }

    [TestMethod]
    [DataRow(429)]
    [DataRow(503)]
    [DataRow(0)]
    public async Task UpstreamFailuresNeverCooldownAccount(int status)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        var setup = client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), true,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()));
        if (status == 0) setup.ThrowsAsync(new HttpRequestException("proxy unavailable"));
        else setup.ReturnsAsync(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") });
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        var result = await terminal.InvokeAsync(Attempt(CreateAccount("pool"), client.Object));
        Assert.IsNotNull(result.Attempt.Decision);
        Assert.AreEqual(PluginAccountAction.None, result.Attempt.Decision.AccountAction);
        Assert.AreEqual(status == 0 ? PluginProxyAction.Cooldown : PluginProxyAction.None, result.Attempt.Decision.ProxyAction);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CustomHttpProxyReceivesAuthenticatedPostAndPreservesCompleteResponse(bool streaming)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        const string json = "{\"id\":\"local-proxy-response\"}";
        const string sse = "data: {\"delta\":\"first\"}\n\ndata: [DONE]\n\n";
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeAuthenticatedProxyAsync(listener, streaming ? sse : json,
            streaming, releaseBody.Task, timeout.Token);
        var hostClient = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        var account = CreateAccount("custom", address, "proxy-user", ProxyPassword);
        var context = Attempt(account, hostClient.Object, streaming, timeout.Token);
        try
        {
            var result = await terminal.InvokeAsync(context);
            Assert.AreEqual(200, result.Response.StatusCode);
            if (streaming)
            {
                Assert.IsNotNull(result.Response.RawStream);
                Assert.IsNotNull(result.Response.Lifetime);
                // The proxy sends only headers until InvokeAsync returns. Early client disposal breaks the stream.
                releaseBody.TrySetResult();
                using var body = new MemoryStream();
                await foreach (var chunk in result.Response.RawStream.WithCancellation(timeout.Token))
                    await body.WriteAsync(chunk, timeout.Token);
                Assert.AreEqual(sse, Encoding.UTF8.GetString(body.ToArray()));
                await result.Response.Lifetime.DisposeAsync();
            }
            else
            {
                Assert.IsNotNull(result.Response.RawContent);
                Assert.AreEqual(json, Encoding.UTF8.GetString(result.Response.RawContent));
            }
            var request = await server;
            StringAssert.StartsWith(request.StartLine, "POST http://upstream.invalid/v1/chat/completions HTTP/");
            Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("proxy-user:" + ProxyPassword)),
                request.Headers["Proxy-Authorization"]);
            Assert.AreEqual("Bearer upstream-test-key", request.Headers["Authorization"]);
            using var payload = JsonDocument.Parse(request.Body);
            Assert.AreEqual("model-a", payload.RootElement.GetProperty("model").GetString());
            Assert.IsFalse(request.Body.Contains(ProxyPassword, StringComparison.Ordinal));
            hostClient.VerifyNoOtherCalls();
        }
        finally
        {
            releaseBody.TrySetResult();
            await timeout.CancelAsync();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task UnconsumedCustomStreamLifetimeClosesProxyConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ObserveUnconsumedStreamCloseAsync(listener, timeout.Token);
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        var result = await terminal.InvokeAsync(Attempt(CreateAccount("custom", address), client.Object, true, timeout.Token));
        Assert.IsNotNull(result.Response.RawStream);
        Assert.IsNotNull(result.Response.Lifetime);
        await result.Response.Lifetime.DisposeAsync();
        Assert.AreEqual(0, await server, "Disposing an unconsumed response must close its proxy socket.");
        client.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ManagementDiscoveryAndRefreshUsePoolWithoutDirectFallback()
    {
        var requests = 0;
        var handler = new Mock<HttpMessageHandler>();
        using var handlerLifetime = handler.Object;
        handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage request, CancellationToken _) =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                Assert.AreEqual("http://upstream.invalid/v1/models", request.RequestUri!.AbsoluteUri);
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"model-a\"}]}")
                });
            });
        var pool = new Mock<IProxyPoolHttpClientFactory>(MockBehavior.Strict);
        pool.Setup(value => value.CreateClientAsync(It.Is<ProxyPoolHttpClientOptions>(options => !options.AllowDirectFallback),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpClient(handler.Object, disposeHandler: false));
        var host = PluginTestHost.Create("forwardapi", pool.Object);
        var account = CreateAccount("pool");
        ConfigureAccounts(host, account);
        using var terminal = new ForwardApiTerminal(host);

        var fresh = await terminal.DiscoverModelsAsync(Management(new
        {
            siteType = "Custom", baseUrl = "http://upstream.invalid/v1", apiKey = "upstream-test-key", proxyMode = "pool"
        }));
        var existing = await terminal.DiscoverModelsAsync(Management(new { id = account.Id }));
        var refreshed = await terminal.RefreshModelsAsync(Management(new { id = account.Id }));

        Assert.AreEqual(200, fresh.StatusCode);
        Assert.AreEqual(200, existing.StatusCode);
        Assert.AreEqual(200, refreshed.StatusCode);
        Assert.AreEqual(3, requests);
        pool.Verify(value => value.CreateClientAsync(It.Is<ProxyPoolHttpClientOptions>(options => !options.AllowDirectFallback),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        Mock.Get(host.Services.Http).Verify(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    public async Task DiscoveryUsesLocalSocks5ProxyAndResolvesUpstreamAtProxy()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = "socks5://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeSocks5ModelsAsync(listener, timeout.Token);
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));

        var result = await terminal.DiscoverModelsAsync(Management(new
        {
            siteType = "Custom", baseUrl = "http://upstream.invalid/v1", apiKey = "upstream-test-key",
            proxyMode = "custom", proxyUrl = address
        }, timeout.Token));

        Assert.AreEqual(200, result.StatusCode);
        var request = await server;
        Assert.AreEqual("upstream.invalid", request.Host);
        Assert.AreEqual(80, request.Port);
        StringAssert.StartsWith(request.Http.StartLine, "GET /v1/models HTTP/");
        StringAssert.Contains(JsonSerializer.Serialize(result.Body), "model-a");
    }

    [TestMethod]
    [DataRow("invalid", "", "", "")]
    [DataRow("custom", "ftp://127.0.0.1:8080", "", "")]
    [DataRow("custom", "http://user:password@127.0.0.1:8080", "", "")]
    [DataRow("custom", "http://127.0.0.1:8080/path", "", "")]
    [DataRow("custom", "http://127.0.0.1:8080?query=1", "", "")]
    [DataRow("custom", "http://127.0.0.1:8080#fragment", "", "")]
    [DataRow("custom", "http://127.0.0.1:8080", "", "password-without-user")]
    [DataRow("custom", "http://127.0.0.1:8080", "user\r\n", "password")]
    [DataRow("custom", "http://127.0.0.1:8080", "user", "password\n")]
    public async Task InvalidProxySettingsRejectSaveAndDiscoveryBeforeNetwork(string mode, string url, string user, string password)
    {
        var host = PluginTestHost.Create("forwardapi");
        using var terminal = new ForwardApiTerminal(host);
        var input = Management(new
        {
            label = "invalid", siteType = "Custom", baseUrl = "http://upstream.invalid", apiKey = "upstream-test-key",
            models = Models, availableModels = Models, proxyMode = mode, proxyUrl = url,
            proxyUsername = user, proxyPassword = password
        });
        Assert.AreEqual(400, (await terminal.SaveAccountAsync(input)).StatusCode);
        Assert.AreEqual(400, (await terminal.DiscoverModelsAsync(input)).StatusCode);
        Mock.Get(host.Services.Accounts).Verify(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()), Times.Never);
        Mock.Get(host.Services.Http).Verify(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        Mock.Get(host.Services.Http.Pool).Verify(value => value.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SavingUnchangedProxyPreservesPasswordWithoutReturningIt(bool omitPassword)
    {
        var host = PluginTestHost.Create("forwardapi");
        var account = CreateAccount("custom", "http://127.0.0.1:8080", "proxy-user", ProxyPassword);
        ConfigureAccounts(host, account);
        Account? saved = null;
        Mock.Get(host.Services.Accounts).Setup(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
            .Callback<Account, CancellationToken>((value, _) => saved = value).ReturnsAsync((Account value, CancellationToken _) => value);
        using var terminal = new ForwardApiTerminal(host);
        var fields = new Dictionary<string, object?> { ["id"] = account.Id, ["label"] = "renamed" };
        if (!omitPassword) fields["proxyPassword"] = "";

        var result = await terminal.SaveAccountAsync(Management(fields));

        Assert.AreEqual(200, result.StatusCode);
        Assert.IsNotNull(saved);
        using var settings = JsonDocument.Parse(((CustomCredential)saved.Credential).Fields["settings"]!);
        Assert.AreEqual(ProxyPassword, settings.RootElement.GetProperty("proxyPassword").GetString());
        var response = JsonSerializer.SerializeToElement(result.Body);
        Assert.IsTrue(response.GetProperty("account").GetProperty("hasProxyPassword").GetBoolean());
        Assert.IsFalse(response.GetProperty("account").TryGetProperty("proxyPassword", out _));
        Assert.IsFalse(response.GetRawText().Contains(ProxyPassword, StringComparison.Ordinal));
        var list = await terminal.ListAccountsAsync(Management(new { }));
        Assert.IsFalse(JsonSerializer.Serialize(list.Body).Contains(ProxyPassword, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("mode")]
    [DataRow("url")]
    [DataRow("username")]
    public async Task ChangingProxyConnectionWithBlankPasswordClearsOldSecret(string changed)
    {
        var host = PluginTestHost.Create("forwardapi");
        var account = CreateAccount("custom", "http://127.0.0.1:8080", "proxy-user", ProxyPassword);
        ConfigureAccounts(host, account);
        Account? saved = null;
        Mock.Get(host.Services.Accounts).Setup(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
            .Callback<Account, CancellationToken>((value, _) => saved = value).ReturnsAsync((Account value, CancellationToken _) => value);
        using var terminal = new ForwardApiTerminal(host);
        var result = await terminal.SaveAccountAsync(Management(new
        {
            id = account.Id, label = "changed", proxyMode = changed == "mode" ? "pool" : "custom",
            proxyUrl = changed == "url" ? "http://127.0.0.1:8081" : "http://127.0.0.1:8080",
            proxyUsername = changed == "username" ? "another-user" : "proxy-user", proxyPassword = ""
        }));

        Assert.AreEqual(200, result.StatusCode);
        Assert.IsNotNull(saved);
        using var settings = JsonDocument.Parse(((CustomCredential)saved.Credential).Fields["settings"]!);
        Assert.AreEqual("", settings.RootElement.GetProperty("proxyPassword").GetString());
        Assert.IsFalse(JsonSerializer.Serialize(result.Body).Contains(ProxyPassword, StringComparison.Ordinal));
    }

    private static Account CreateAccount(string? mode, string url = "", string username = "", string password = "")
    {
        var settings = new Dictionary<string, object?>
        {
            ["siteType"] = "Custom", ["baseUrl"] = "http://upstream.invalid/v1", ["apiKey"] = "upstream-test-key",
            ["proxyUrl"] = url, ["proxyUsername"] = username, ["proxyPassword"] = password
        };
        if (mode is not null) settings["proxyMode"] = mode;
        return new Account
        {
            Id = "proxy-account", PluginKey = "forwardapi", Platform = "forwardapi", Label = "proxy account",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = JsonSerializer.Serialize(settings), ["modelsConfigured"] = "true", ["models"] = "[\"model-a\"]",
                ["availableModels"] = "[\"model-a\"]", ["modelsUpdatedAt"] = "2026-10-20T00:00:00Z"
            })
        };
    }

    private static void ConfigureAccounts(IPluginHost host, Account account)
    {
        Mock.Get(host.Services.Accounts).Setup(value => value.GetAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        Mock.Get(host.Services.Accounts).Setup(value => value.ListAsync("forwardapi", It.IsAny<CancellationToken>())).ReturnsAsync([account]);
        Mock.Get(host.Services.Accounts).Setup(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account value, CancellationToken _) => value);
    }

    private static PluginAttemptContext Attempt(Account account, IPluginHttpClient client, bool stream = false, CancellationToken token = default) => new()
    {
        PluginKey = "forwardapi", PlatformName = "forwardapi", Account = account, HttpClient = client, CancellationToken = token,
        Request = new AdapterRequest
        {
            Model = "model-a", Endpoint = "/v1/chat/completions", Stream = stream,
            OriginalBody = JsonSerializer.SerializeToElement(new { model = "model-a", stream })
        }
    };

    private static PluginHttpContext Management(object body, CancellationToken token = default) => new()
    {
        PluginKey = "forwardapi", Platform = "forwardapi", Body = JsonSerializer.SerializeToElement(body), CancellationToken = token
    };

    private sealed record CapturedRequest(string StartLine, Dictionary<string, string> Headers, string Body);

    private static async Task<CapturedRequest> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            await stream.ReadExactlyAsync(one, token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture) : 0];
        await stream.ReadExactlyAsync(body, token);
        return new CapturedRequest(lines[0], headers, Encoding.UTF8.GetString(body));
    }

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken token) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token);

    private static async Task<CapturedRequest> ServeAuthenticatedProxyAsync(TcpListener listener, string body,
        bool streaming, Task releaseBody, CancellationToken token)
    {
        // Close the challenge connection so the second authenticated request is accepted deterministically.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            await using var stream = connection.GetStream();
            var request = await ReadRequestAsync(stream, token);
            if (!request.Headers.ContainsKey("Proxy-Authorization"))
            {
                await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"local-test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
                continue;
            }
            var contentType = streaming ? "text/event-stream" : "application/json";
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n", token);
            if (streaming) await releaseBody.WaitAsync(token);
            await WriteAsync(stream, body, token);
            return request;
        }
        throw new InvalidOperationException("Proxy authentication was not supplied after a Basic challenge.");
    }

    private static async Task<int> ObserveUnconsumedStreamCloseAsync(TcpListener listener, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        _ = await ReadRequestAsync(stream, token);
        await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n", token);
        return await stream.ReadAsync(new byte[1], token);
    }

    private static async Task<(string Host, int Port, CapturedRequest Http)> ServeSocks5ModelsAsync(TcpListener listener, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, token);
        Assert.AreEqual((byte)5, greeting[0]);
        var methods = new byte[greeting[1]];
        await stream.ReadExactlyAsync(methods, token);
        CollectionAssert.Contains(methods, (byte)0);
        await stream.WriteAsync(new byte[] { 5, 0 }, token);
        var command = new byte[4];
        await stream.ReadExactlyAsync(command, token);
        CollectionAssert.AreEqual(new byte[] { 5, 1, 0, 3 }, command, "SOCKS5 must send an unresolved domain to the proxy.");
        var size = new byte[1];
        await stream.ReadExactlyAsync(size, token);
        var name = new byte[size[0]];
        await stream.ReadExactlyAsync(name, token);
        var port = new byte[2];
        await stream.ReadExactlyAsync(port, token);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
        var request = await ReadRequestAsync(stream, token);
        const string models = "{\"data\":[{\"id\":\"model-a\"}]}";
        await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(models) + "\r\nConnection: close\r\n\r\n" + models, token);
        return (Encoding.ASCII.GetString(name), (port[0] << 8) | port[1], request);
    }
}
