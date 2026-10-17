using System.Reflection;
using Moq;
using Plugins.ForwardAPI;
using Router.Contracts.Domain;
using Router.Contracts.Plugins;

namespace Router.Tests;

[TestClass]
public sealed class ForwardApiAllocationTests
{
    [TestMethod]
    public async Task WarmAccountSelectionPreservesAllowListsWithoutAllocating()
    {
        var host = PluginTestHost.Create("forwardapi");
        var account = new Account
        {
            Id = "selection-check",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = """{"baseUrl":"https://upstream.example","apiKey":"test-key","endpoints":["/v1/chat/completions"]}""",
                ["modelsConfigured"] = "true",
                ["models"] = """["model-a"]"""
            })
        };
        Mock.Get(host.Services.Accounts).Setup(value => value.ListAsync("forwardapi", It.IsAny<CancellationToken>()))
            .ReturnsAsync([account]);
        using var terminal = new ForwardApiTerminal(host);
        await terminal.StartAsync(new PluginStartContext("forwardapi", host), CancellationToken.None);
        var eligible = typeof(ForwardApiTerminal).GetMethod("IsAccountEligible", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Account, AdapterRequest, bool>>(terminal);
        var request = new AdapterRequest { Model = "MODEL-A", Endpoint = " //V1/CHAT/COMPLETIONS// " };
        Assert.IsTrue(eligible(account, request));
        Assert.IsFalse(eligible(account, new AdapterRequest { Model = "model-b", Endpoint = request.Endpoint }));
        Assert.IsFalse(eligible(account, new AdapterRequest { Model = request.Model, Endpoint = "/v1/responses" }));

        for (var i = 0; i < 1000; i++) _ = eligible(account, request);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = 0;
        for (var i = 0; i < 10000; i++)
            if (eligible(account, request)) accepted++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(10000, accepted);
        Assert.AreEqual(0L, allocated, "预热后的账号筛选不应产生托管分配。");
    }

    [TestMethod]
    [DataRow(" /v1/chat/completions/ ", "/v1/chat/completions")]
    [DataRow(" //V1/MESSAGES// ", "/V1/MESSAGES")]
    [DataRow(" /custom/ ", "/custom")]
    [DataRow(" /// ", "/")]
    public void EndpointNormalizationPreservesPaths(string input, string expected)
    {
        var normalize = typeof(ForwardApiTerminal).GetMethod("NormalizeEndpoint", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, string>>();
        Assert.AreEqual(expected, normalize(input));
    }
}
