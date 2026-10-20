using System.Net;
using System.Reflection;
using System.Text.Json;
using Moq;
using Plugins.ForwardAPI;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Router.Tests;

/// <summary>通过 Contracts Mock 验证独立账号开关、实时模型目录和协议路径，不连接真实上游。</summary>
[TestClass]
public sealed class ForwardApiAccountStateTests
{
    private static readonly string[] AccountEndpoints = ["/v1/chat/completions", "/v1/responses"];
    [TestMethod]
    public async Task DisableRemovesUniqueModelsRetainsSharedModelsAndReenableRestoresCatalog()
    {
        var first = CreateAccount("first", ["first-only", "shared"]);
        var second = CreateAccount("second", ["second-only", "shared"]);
        var accounts = new Dictionary<string, Account> { [first.Id] = first, [second.Id] = second };
        var host = CreateHost(accounts);
        using var terminal = new ForwardApiTerminal(host);
        var originalFields = new Dictionary<string, string?>(((CustomCredential)first.Credential).Fields);
        var originalSettings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalFields["settings"]!)!;
        var initialVersion = first.Status.Version;
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None), "first-only", "second-only", "shared");

        var disabled = await terminal.SetAccountEnabledAsync(Management(new { id = first.Id, enabled = false }));

        Assert.AreEqual(200, disabled.StatusCode);
        Assert.AreEqual(ResourceState.Disabled, accounts[first.Id].Status.State);
        Assert.AreEqual(initialVersion + 1, accounts[first.Id].Status.Version);
        Assert.AreEqual(2, accounts[first.Id].Status.InFlight);
        AssertSettingsEnabled(accounts[first.Id], false);
        AssertPreservedCredentialFields(originalFields, accounts[first.Id]);
        using (var updated = JsonDocument.Parse(((CustomCredential)accounts[first.Id].Credential).Fields["settings"]!))
        {
            foreach (var pair in originalSettings.Where(pair => pair.Key != "enabled"))
                Assert.IsTrue(JsonElement.DeepEquals(pair.Value, updated.RootElement.GetProperty(pair.Key)), pair.Key);
        }
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None), "second-only", "shared");

        Assert.AreEqual(200, (await terminal.SetAccountEnabledAsync(Management(new { id = second.Id, enabled = false }))).StatusCode);
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None));

        var reenabled = await terminal.SetAccountEnabledAsync(Management(new { id = first.Id, enabled = true }));
        Assert.AreEqual(200, reenabled.StatusCode);
        Assert.AreEqual(ResourceState.Active, accounts[first.Id].Status.State);
        Assert.AreEqual(initialVersion + 2, accounts[first.Id].Status.Version);
        Assert.AreEqual(2, accounts[first.Id].Status.InFlight);
        AssertSettingsEnabled(accounts[first.Id], true);
        AssertPreservedCredentialFields(originalFields, accounts[first.Id]);
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None), "first-only", "shared");
        Mock.Get(host.Services.Accounts).Verify(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        Mock.Get(host.Services.Models).Verify(value => value.Invalidate("forwardapi"), Times.Exactly(3));
        AssertNoNetwork(host);
        Assert.IsFalse(JsonSerializer.Serialize(disabled.Body).Contains("state-test-proxy-secret", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(reenabled.Body).Contains("state-test-upstream-key", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(ResourceState.Disabled)]
    [DataRow(ResourceState.Invalid)]
    public async Task ReenableRestoresActiveStatusAndModelsFromDisabledOrInvalid(ResourceState state)
    {
        var account = CreateAccount("recover", ["recover-model"], state, enabled: false);
        var accounts = new Dictionary<string, Account> { [account.Id] = account };
        var host = CreateHost(accounts);
        using var terminal = new ForwardApiTerminal(host);
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None));

        var result = await terminal.SetAccountEnabledAsync(Management(new { id = account.Id, enabled = true }));

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(ResourceState.Active, accounts[account.Id].Status.State);
        AssertSettingsEnabled(accounts[account.Id], true);
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None), "recover-model");
        AssertNoNetwork(host);
    }

    [TestMethod]
    public async Task EnablingAlreadyActiveAccountPreservesExistingStatus()
    {
        var account = CreateAccount("already-active", ["model-a"]);
        var original = account.Status;
        original.Reason = "preserve existing status";
        original.LastStatusCode = 200;
        var host = CreateHost(new Dictionary<string, Account> { [account.Id] = account });
        using var terminal = new ForwardApiTerminal(host);

        var result = await terminal.SetAccountEnabledAsync(Management(new { id = account.Id, enabled = true }));

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreSame(original, account.Status);
        Assert.AreEqual(ResourceState.Active, account.Status.State);
        Assert.AreEqual("preserve existing status", account.Status.Reason);
        Assert.AreEqual(200, account.Status.LastStatusCode);
        Assert.AreEqual(7L, account.Status.Version);
        Assert.AreEqual(2, account.Status.InFlight);
        AssertNoNetwork(host);
    }

    [TestMethod]
    [DataRow(ResourceState.Disabled)]
    [DataRow(ResourceState.Invalid)]
    public async Task ModelQueriesObserveExternalStatusChangeWithoutForceRefreshOrSharedCacheRead(ResourceState state)
    {
        var account = CreateAccount("external", ["external-only"]);
        var accounts = new Dictionary<string, Account> { [account.Id] = account };
        var host = CreateHost(accounts);
        var shared = Mock.Get(host.Services.State.Shared);
        shared.SetupGet(value => value.IsAvailable).Returns(true);
        shared.Setup(value => value.GetStringAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[\"stale-cache-only\",\"external-only\"]");
        shared.Setup(value => value.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using var terminal = new ForwardApiTerminal(host);
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None), "external-only");
        // Host-side disable: settings remain enabled, only the current stored ResourceStatus changes.
        account.Status = new ResourceStatus { State = state, Version = account.Status.Version + 1 };

        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None));

        account.Status = new ResourceStatus { State = ResourceState.Active };
        AssertModels(await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None), "external-only");
        Mock.Get(host.Services.Accounts).Verify(value => value.ListAsync("forwardapi", It.IsAny<CancellationToken>()), Times.Exactly(3));
        shared.Verify(value => value.GetStringAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Mock.Get(host.Services.Accounts).Verify(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()), Times.Never);
        AssertNoNetwork(host);
    }

    [TestMethod]
    [DataRow(ResourceState.Disabled)]
    [DataRow(ResourceState.Invalid)]
    public async Task CurrentDisabledAccountCannotInvokeThroughOlderEnabledSnapshot(ResourceState state)
    {
        var account = CreateAccount("snapshot", ["model-a"]);
        var host = CreateHost(new Dictionary<string, Account> { [account.Id] = account });
        using var terminal = new ForwardApiTerminal(host);
        await terminal.StartAsync(new PluginStartContext("forwardapi", host), CancellationToken.None);
        // The snapshot is deliberately not rebuilt after receiving a newer account instance from the host.
        var current = CreateAccount(account.Id, ["model-a"], state);
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        var attempt = Attempt(current, client.Object, "/v1/chat/completions");
        var eligible = typeof(ForwardApiTerminal).GetMethod("IsAccountEligible", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Account, AdapterRequest, bool>>(terminal);
        Assert.IsTrue(eligible(account, attempt.Request), "The deliberately stale snapshot initially allows this account.");
        Assert.IsFalse(eligible(current, attempt.Request), "Current host status must override the enabled cached policy.");

        var result = await terminal.InvokeAsync(attempt);

        Assert.AreEqual(400, result.Response.StatusCode);
        client.VerifyNoOtherCalls();
        AssertNoNetwork(host);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"enabled\":true}")]
    [DataRow("{\"id\":\"\",\"enabled\":false}")]
    [DataRow("{\"id\":\"   \",\"enabled\":false}")]
    [DataRow("{\"id\":\"account\"}")]
    [DataRow("{\"id\":\"account\",\"enabled\":null}")]
    [DataRow("{\"id\":\"account\",\"enabled\":\"false\"}")]
    [DataRow("{\"id\":\"account\",\"enabled\":0}")]
    [DataRow("[]")]
    public async Task InvalidToggleInputReturns400WithoutStorageOrNetwork(string json)
    {
        var host = CreateHost([]);
        using var terminal = new ForwardApiTerminal(host);
        using var document = JsonDocument.Parse(json);
        var result = await terminal.SetAccountEnabledAsync(new PluginHttpContext
        {
            PluginKey = "forwardapi", Platform = "forwardapi", Body = document.RootElement
        });

        Assert.AreEqual(400, result.StatusCode);
        Mock.Get(host.Services.Accounts).VerifyNoOtherCalls();
        Mock.Get(host.Services.Models).Verify(value => value.Invalidate(It.IsAny<string>()), Times.Never);
        AssertNoNetwork(host);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("other-plugin")]
    [DataRow("other-platform")]
    public async Task UnknownOrForeignAccountReturns404WithoutChangingCredentials(string scenario)
    {
        var account = CreateAccount("foreign", ["model-a"], plugin: scenario == "other-plugin" ? "another-plugin" : "forwardapi",
            platform: scenario == "other-platform" ? "another-platform" : "forwardapi");
        var originalCredential = account.Credential;
        var host = CreateHost(scenario == "missing" ? [] : new Dictionary<string, Account> { [account.Id] = account });
        using var terminal = new ForwardApiTerminal(host);

        var result = await terminal.SetAccountEnabledAsync(Management(new { id = account.Id, enabled = false }));

        Assert.AreEqual(404, result.StatusCode);
        Assert.AreSame(originalCredential, account.Credential);
        Assert.AreEqual(ResourceState.Active, account.Status.State);
        Mock.Get(host.Services.Accounts).Verify(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()), Times.Never);
        Mock.Get(host.Services.Accounts).Verify(value => value.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Mock.Get(host.Services.Models).Verify(value => value.Invalidate(It.IsAny<string>()), Times.Never);
        AssertNoNetwork(host);
    }

    [TestMethod]
    [DataRow("/v1/responses")]
    [DataRow("/v1/chat/completions")]
    public async Task Sub2ApiForwardsRequestedProtocolEndpointWithoutRewriting(string endpoint)
    {
        var account = CreateAccount("sub2api", ["model-a"], siteType: "Sub2API");
        var host = CreateHost(new Dictionary<string, Account> { [account.Id] = account });
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        var requests = 0;
        client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) =>
            {
                requests++;
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("https://upstream.invalid" + endpoint, request.RequestUri!.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            });
        using var terminal = new ForwardApiTerminal(host);

        var result = await terminal.InvokeAsync(Attempt(account, client.Object, endpoint));

        Assert.IsTrue(result.Response.IsSuccess);
        Assert.AreEqual(1, requests);
        client.Verify(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();
        AssertNoNetwork(host);
    }

    [TestMethod]
    [DataRow(ResourceState.Disabled, true)]
    [DataRow(ResourceState.Invalid, true)]
    [DataRow(ResourceState.Active, false)]
    public async Task DailyCheckInSkipsAccountsDisabledInSettingsOrHost(ResourceState state, bool enabled)
    {
        var account = CreateAccount("checkin-disabled", ["model-a"], state, enabled, siteType: "NewAPI");
        var host = CreateHost(new Dictionary<string, Account> { [account.Id] = account });
        using var terminal = new ForwardApiTerminal(host);

        await terminal.ScheduledTasks.Single().ExecuteAsync(new PluginScheduledTaskContext(
            "forwardapi", "forwardapi", CancellationToken.None));

        AssertNoNetwork(host);
        Mock.Get(host.Services.Accounts).Verify(value => value.SaveAsync(It.IsAny<Account>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Account CreateAccount(string id, string[] models, ResourceState state = ResourceState.Active,
        bool enabled = true, string plugin = "forwardapi", string platform = "forwardapi", string siteType = "Custom") => new()
    {
        Id = id, PluginKey = plugin, Platform = platform, Label = "state test " + id,
        Status = new ResourceStatus { State = state, InFlight = 2, Version = 7 },
        Credential = new CustomCredential(new Dictionary<string, string?>
        {
            ["settings"] = JsonSerializer.Serialize(new
            {
                siteType, baseUrl = "https://upstream.invalid/v1", apiKey = "state-test-upstream-key",
                username = "site-user", password = "state-test-site-password", weight = 17, enabled,
                endpoints = AccountEndpoints, autoCheckIn = true,
                extraParams = "{\"ModelMap\":{\"model-a\":\"upstream-a\"}}", proxyMode = "direct",
                proxyUrl = "", proxyUsername = "", proxyPassword = "state-test-proxy-secret",
                futureSetting = new { nested = "preserve-unknown-setting" }
            }),
            ["modelsConfigured"] = "true", ["models"] = JsonSerializer.Serialize(models),
            ["availableModels"] = JsonSerializer.Serialize(models), ["modelsUpdatedAt"] = "2026-10-20T00:00:00Z",
            ["quotaSnapshot"] = "{\"remaining\":123}", ["quotaUpdatedAt"] = "2026-10-20T01:00:00Z",
            ["extension-field"] = "must-survive-toggle", ["nullable-extension"] = null
        })
    };

    private static IPluginHost CreateHost(Dictionary<string, Account> accounts)
    {
        var host = PluginTestHost.Create("forwardapi");
        Mock.Get(host.Services.Accounts).Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => accounts.GetValueOrDefault(id));
        Mock.Get(host.Services.Accounts).Setup(value => value.ListAsync("forwardapi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => accounts.Values.ToArray());
        Mock.Get(host.Services.Accounts).Setup(value => value.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account account, CancellationToken _) => { accounts[account.Id] = account; return account; });
        return host;
    }

    private static void AssertModels(IReadOnlyList<ModelDescriptor> actual, params string[] expected) =>
        CollectionAssert.AreEquivalent(expected, actual.Select(model => model.Id).ToArray());

    private static void AssertSettingsEnabled(Account account, bool enabled)
    {
        using var settings = JsonDocument.Parse(((CustomCredential)account.Credential).Fields["settings"]!);
        Assert.AreEqual(enabled, settings.RootElement.GetProperty("enabled").GetBoolean());
    }

    private static void AssertPreservedCredentialFields(Dictionary<string, string?> original, Account account)
    {
        var actual = ((CustomCredential)account.Credential).Fields;
        Assert.AreEqual(original.Count, actual.Count);
        foreach (var pair in original.Where(pair => pair.Key != "settings"))
            Assert.AreEqual(pair.Value, actual[pair.Key], pair.Key);
    }

    private static void AssertNoNetwork(IPluginHost host)
    {
        Mock.Get(host.Services.Http).Verify(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        Mock.Get(host.Services.Http.Pool).Verify(value => value.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PluginHttpContext Management(object body) => new()
    {
        PluginKey = "forwardapi", Platform = "forwardapi", Body = JsonSerializer.SerializeToElement(body)
    };

    private static PluginAttemptContext Attempt(Account account, IPluginHttpClient client, string endpoint) => new()
    {
        PluginKey = "forwardapi", PlatformName = "forwardapi", Account = account, HttpClient = client,
        CancellationToken = CancellationToken.None,
        Request = new AdapterRequest
        {
            Model = "model-a", Endpoint = endpoint,
            OriginalBody = JsonSerializer.SerializeToElement(new { model = "model-a", input = "protocol-path-test" })
        }
    };
}
