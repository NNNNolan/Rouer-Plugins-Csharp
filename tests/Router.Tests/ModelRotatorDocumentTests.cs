using System.Text.Json;
using Plugins.ModelRotator;

namespace Router.Tests;

[TestClass]
public sealed class ModelRotatorDocumentTests
{
    [TestMethod]
    public void NormalizeDocumentClampsSettingsAndRoutes()
    {
        var parsed = JsonSerializer.Deserialize<RotatorDocument>(
            """
            {
              "version": 2,
              "settings": {
                "baseUrl": "http://127.0.0.1:8080/",
                "firstWaitSeconds": 9999,
                "firstByteBudgetSeconds": -5,
                "streamIdleSeconds": 1,
                "probeIntervalMinutes": 0,
                "probeTimeoutSeconds": 9999,
                "activationPolicy": "unknown"
              },
              "virtuals": [
                { "virtualModel": "route-a", "targets": ["model-a", "model-a", "", null, "model-b"], "failureThreshold": 999, "probeEndpoint": "/v1/embeddings" },
                { "virtualModel": "route-a", "targets": ["model-c"] },
                { "virtualModel": "", "targets": ["model-x"] }
              ],
              "state": { "failures": { "route-a\u001fmodel-a": 5 } }
            }
            """,
            ModelRotatorTerminal.JsonOptions);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(3, parsed.Virtuals.Count);
        var normalized = ModelRotatorTerminal.NormalizeDocument(parsed);

        Assert.IsNotNull(normalized);
        Assert.AreEqual(3, normalized.Version);
        Assert.AreEqual("http://127.0.0.1:8080", normalized.Settings.BaseUrl);
        Assert.AreEqual(300, normalized.Settings.FirstWaitSeconds, "正值应收敛到区间内最大值（JS clampSeconds 语义）");
        Assert.AreEqual(180, normalized.Settings.FirstByteBudgetSeconds, "非法负值才回落默认");
        Assert.AreEqual(5, normalized.Settings.StreamIdleSeconds, "低于下限的正值应收敛到下限");
        Assert.AreEqual(5, normalized.Settings.ProbeIntervalMinutes);
        Assert.AreEqual(180, normalized.Settings.ProbeTimeoutSeconds, "探测超时也应收敛进 5–180 秒区间");
        Assert.AreEqual("sticky", normalized.Settings.ActivationPolicy);
        Assert.AreEqual(1, normalized.Virtuals.Count, "重复的虚拟模型应被去重，空名应被丢掉");
        Assert.AreEqual("route-a", normalized.Virtuals[0].VirtualModel);
        Assert.AreEqual(2, normalized.Virtuals[0].Targets.Count, "重复与空目标应被丢掉");
        Assert.AreEqual(100, normalized.Virtuals[0].FailureThreshold);
        Assert.AreEqual("/v1/embeddings", normalized.Virtuals[0].ProbeEndpoint);
        Assert.AreEqual(5, normalized.State.Failures["route-a\u001fmodel-a"]);
    }

    [TestMethod]
    public void ProbeTimeoutUsesDefaultWhenMissingOrNonPositive()
    {
        var missing = ModelRotatorTerminal.NormalizeDocument(new RotatorDocument());
        Assert.IsNotNull(missing);
        Assert.AreEqual(45, missing.Settings.ProbeTimeoutSeconds);

        var nonPositive = ModelRotatorTerminal.NormalizeDocument(new RotatorDocument
        {
            Settings = new RotatorSettings { ProbeTimeoutSeconds = 0 }
        });
        Assert.IsNotNull(nonPositive);
        Assert.AreEqual(45, nonPositive.Settings.ProbeTimeoutSeconds, "非正值回落默认 45 秒");

        var tooSmall = ModelRotatorTerminal.NormalizeDocument(new RotatorDocument
        {
            Settings = new RotatorSettings { ProbeTimeoutSeconds = 1 }
        });
        Assert.IsNotNull(tooSmall);
        Assert.AreEqual(5, tooSmall.Settings.ProbeTimeoutSeconds, "低于下限的正值收敛到 5 秒");
    }

    [TestMethod]
    public void UsageConverterAcceptsLegacyArrayRows()
    {
        var document = JsonSerializer.Deserialize<RotatorDocument>(
            """
            {
              "version": 3,
              "usage": [["2026-10-15","model-a",3,120,80],["bad"],["2026-10-16","model-b",1,10,2]]
            }
            """,
            ModelRotatorTerminal.JsonOptions);

        Assert.IsNotNull(document);
        Assert.AreEqual(2, document.Usage.Count);
        Assert.AreEqual("2026-10-15", document.Usage[0].Day);
        Assert.AreEqual("model-b", document.Usage[1].Model);
        Assert.AreEqual(10, document.Usage[1].PromptTokens);

        var written = JsonSerializer.Serialize(document, ModelRotatorTerminal.JsonOptions);
        StringAssert.Contains(written, "\"usage\":[[\"2026-10-15\",\"model-a\",3,120,80],[\"2026-10-16\",\"model-b\",1,10,2]]");
    }
}
