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
    public void DemoteThresholdDefaultsToOneWhenMissingFromLegacySettings()
    {
        var legacy = JsonSerializer.Deserialize<RotatorDocument>(
            """{"version":3,"settings":{"activationPolicy":"sticky"}}""",
            ModelRotatorTerminal.JsonOptions);
        Assert.IsNotNull(legacy);

        var normalized = ModelRotatorTerminal.NormalizeDocument(legacy);

        Assert.AreEqual(1, new RotatorSettings().DemoteThreshold);
        Assert.AreEqual(1, ModelRotatorTerminal.EmptyDocument().Settings.DemoteThreshold);
        Assert.AreEqual(1, normalized.Settings.DemoteThreshold,
            "旧配置缺少全局降级阈值时，应保持一次失败或超时即降级的默认行为");
    }

    [TestMethod]
    [DataRow(int.MinValue, 1)]
    [DataRow(-1, 1)]
    [DataRow(0, 1)]
    [DataRow(1, 1)]
    [DataRow(37, 37)]
    [DataRow(100, 100)]
    [DataRow(101, 100)]
    [DataRow(int.MaxValue, 100)]
    public void NormalizeDemoteThresholdClampsToAllowedRange(int input, int expected)
    {
        var normalized = ModelRotatorTerminal.NormalizeDocument(new RotatorDocument
        {
            Settings = new RotatorSettings { DemoteThreshold = input }
        });

        Assert.AreEqual(expected, normalized.Settings.DemoteThreshold,
            "全局降级阈值必须收敛到 1..100，且区间内取值不变");
    }

    [TestMethod]
    public void DemoteThresholdSerializesAsGlobalSettingAndSurvivesRoundTrip()
    {
        var document = new RotatorDocument
        {
            Settings = new RotatorSettings { DemoteThreshold = 7 },
            Virtuals = [new RotatorRoute { VirtualModel = "route-a", Targets = ["model-a"], FailureThreshold = 11 }]
        };

        var json = JsonSerializer.Serialize(document, ModelRotatorTerminal.JsonOptions);
        using var serialized = JsonDocument.Parse(json);
        Assert.AreEqual(7, serialized.RootElement.GetProperty("settings").GetProperty("demoteThreshold").GetInt32());
        Assert.IsFalse(serialized.RootElement.GetProperty("virtuals")[0].TryGetProperty("demoteThreshold", out _),
            "降级阈值属于全局设置，不是路由的禁用阈值");
        var restored = JsonSerializer.Deserialize<RotatorDocument>(json, ModelRotatorTerminal.JsonOptions);
        Assert.IsNotNull(restored);
        Assert.AreEqual(7, restored.Settings.DemoteThreshold);
        Assert.AreEqual(11, restored.Virtuals[0].FailureThreshold, "全局降级阈值不能覆盖路由禁用阈值");
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
