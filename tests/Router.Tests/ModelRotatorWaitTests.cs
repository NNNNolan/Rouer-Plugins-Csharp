using Plugins.ModelRotator;

namespace Router.Tests;

[TestClass]
public sealed class ModelRotatorWaitTests
{
    private const string RouteKey = "auto_switch\u001fmodel-a";
    private static readonly string[] ExpectedBca = ["b", "c", "a"];

    private static RotatorDocument Document(RotatorState state, int firstWaitSeconds = 50) => new()
    {
        Settings = new RotatorSettings { FirstWaitSeconds = firstWaitSeconds, FirstWaitMaxSeconds = 80 },
        Virtuals = [new RotatorRoute { VirtualModel = "auto_switch", Targets = ["model-a"] }],
        State = state
    };

    private static TimeSpan Wait(RotatorDocument doc, TimeSpan remaining) =>
        ModelRotatorTerminal.ComputeFirstWait(doc, doc.Virtuals[0], "model-a", remaining);

    [TestMethod]
    public void UnknownTargetUsesConfiguredWait()
    {
        var doc = Document(new RotatorState());

        Assert.AreEqual(TimeSpan.FromSeconds(50), Wait(doc, TimeSpan.FromMinutes(10)));
    }

    [TestMethod]
    public void SlowHealthyTargetExtendsWaitUpToCap()
    {
        var doc = Document(new RotatorState
        {
            Latency = new(StringComparer.Ordinal) { [RouteKey] = 72_000 }
        });

        // max(50s 配置值, 72s×1.5=108s) 收敛到 firstWaitMaxSeconds=80s 上限
        Assert.AreEqual(TimeSpan.FromSeconds(80), Wait(doc, TimeSpan.FromMinutes(10)));
    }

    [TestMethod]
    public void RecentlyTimedOutTargetOnlyGetsShortRetryWindow()
    {
        var doc = Document(new RotatorState
        {
            Latency = new(StringComparer.Ordinal) { [RouteKey] = 72_000 },
            Timeouts = new(StringComparer.Ordinal) { [RouteKey] = 1 }
        });

        // 上一次已经在这个目标上等满超时，再等 80 秒没有意义：只给 20 秒复检窗口
        Assert.AreEqual(TimeSpan.FromSeconds(20), Wait(doc, TimeSpan.FromMinutes(10)));
    }

    [TestMethod]
    public void ShortRetryWindowNeverExceedsRemainingBudget()
    {
        var doc = Document(new RotatorState
        {
            Timeouts = new(StringComparer.Ordinal) { [RouteKey] = 1 }
        });

        Assert.AreEqual(TimeSpan.FromSeconds(7), Wait(doc, TimeSpan.FromSeconds(12)));
    }

    [TestMethod]
    public void UnsettledTargetsMoveToTheEnd()
    {
        var doc = new RotatorDocument
        {
            Settings = new RotatorSettings(),
            Virtuals = [new RotatorRoute { VirtualModel = "auto_switch", Targets = ["a", "b", "c"] }],
            State = new RotatorState
            {
                Active = new(StringComparer.Ordinal) { ["auto_switch"] = "a" },
                Timeouts = new(StringComparer.Ordinal) { [KeyFor("a")] = 1 }
            }
        };

        CollectionAssert.AreEqual(ExpectedBca, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]));
    }

    [TestMethod]
    public void HealthyPinnedTargetStaysFirst()
    {
        var doc = new RotatorDocument
        {
            Settings = new RotatorSettings(),
            Virtuals = [new RotatorRoute { VirtualModel = "auto_switch", Targets = ["a", "b", "c"] }],
            State = new RotatorState
            {
                Active = new(StringComparer.Ordinal) { ["auto_switch"] = "b" },
                Failures = new(StringComparer.Ordinal) { [KeyFor("a")] = 2 }
            }
        };

        CollectionAssert.AreEqual(ExpectedBca, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]));
    }

    private static string KeyFor(string model) => "auto_switch\u001f" + model;

    [TestMethod]
    public void ConfiguredWaitBelowRetryWindowIsKept()
    {
        var doc = Document(new RotatorState
        {
            Timeouts = new(StringComparer.Ordinal) { [RouteKey] = 2 }
        }, firstWaitSeconds: 15);

        Assert.AreEqual(TimeSpan.FromSeconds(15), Wait(doc, TimeSpan.FromMinutes(10)));
    }
}
