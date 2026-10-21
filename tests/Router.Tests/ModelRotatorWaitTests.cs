using Plugins.ModelRotator;

namespace Router.Tests;

[TestClass]
public sealed class ModelRotatorWaitTests
{
    private const string RouteKey = "auto_switch\u001fmodel-a";
    private static readonly string[] ExpectedBca = ["b", "c", "a"];
    private static readonly string[] ExpectedAbc = ["a", "b", "c"];
    private static readonly string[] ExpectedBdfAce = ["b", "d", "f", "a", "c", "e"];
    private static readonly string[] ExpectedAc = ["a", "c"];

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

    [TestMethod]
    [DataRow("sticky", false)]
    [DataRow("sticky", true)]
    [DataRow("prefer-highest", false)]
    [DataRow("prefer-highest", true)]
    public void DefaultDemoteThresholdPreservesSingleFailureOrTimeoutDemotion(string policy, bool timeoutBucket)
    {
        var doc = CandidateDocument(policy);
        SetCount(doc, timeoutBucket, "a", 1);

        Assert.AreEqual(1, doc.Settings.DemoteThreshold);
        CollectionAssert.AreEqual(ExpectedBca, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]),
            "默认阈值 1 应保留首次失败或超时后沉底的行为");
    }

    [TestMethod]
    [DataRow("sticky", false, 2)]
    [DataRow("sticky", true, 2)]
    [DataRow("prefer-highest", false, 2)]
    [DataRow("prefer-highest", true, 2)]
    [DataRow("sticky", false, 3)]
    [DataRow("sticky", true, 3)]
    [DataRow("prefer-highest", false, 3)]
    [DataRow("prefer-highest", true, 3)]
    [DataRow("sticky", false, 4)]
    [DataRow("sticky", true, 4)]
    [DataRow("prefer-highest", false, 4)]
    [DataRow("prefer-highest", true, 4)]
    public void EachPolicyDemotesOnlyWhenAnIndividualBucketReachesThreshold(string policy, bool timeoutBucket, int count)
    {
        var doc = CandidateDocument(policy, demoteThreshold: 3);
        SetCount(doc, timeoutBucket, "a", count);
        string[] expected = count < 3 ? ["a", "b", "c"] : ExpectedBca;

        CollectionAssert.AreEqual(expected, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]),
            "失败桶和超时桶都应分别使用 >= demoteThreshold 判断");
    }

    [TestMethod]
    [DataRow(false, 2)]
    [DataRow(true, 2)]
    [DataRow(false, 3)]
    [DataRow(true, 3)]
    public void StickyActiveTargetKeepsItsPlaceUntilItsBucketReachesThreshold(bool timeoutBucket, int count)
    {
        var doc = CandidateDocument("sticky", demoteThreshold: 3);
        doc.State.Active["auto_switch"] = "c";
        SetCount(doc, timeoutBucket, "c", count);
        string[] expected = count < 3 ? ["c", "a", "b"] : ["a", "b", "c"];

        CollectionAssert.AreEqual(expected, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]),
            "未达到降级阈值的 sticky 活跃目标应继续优先，达到阈值才让位");
    }

    [TestMethod]
    [DataRow(false, 2)]
    [DataRow(true, 2)]
    [DataRow(false, 3)]
    [DataRow(true, 3)]
    public void PreferHighestUsesConfiguredPriorityInsteadOfActivePin(bool timeoutBucket, int count)
    {
        var doc = CandidateDocument("prefer-highest", demoteThreshold: 3);
        doc.State.Active["auto_switch"] = "b";
        SetCount(doc, timeoutBucket, "b", count);
        string[] expected = count < 3 ? ["a", "b", "c"] : ["a", "c", "b"];

        CollectionAssert.AreEqual(expected, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]),
            "prefer-highest 应从配置优先级开始，只在达到阈值后把目标排到队尾");
    }

    [TestMethod]
    [DataRow("sticky")]
    [DataRow("prefer-highest")]
    public void FailureAndTimeoutCountsAreNotAddedForDemotion(string policy)
    {
        var doc = CandidateDocument(policy, demoteThreshold: 3);
        SetCount(doc, timeoutBucket: false, "a", 2);
        SetCount(doc, timeoutBucket: true, "a", 2);

        CollectionAssert.AreEqual(ExpectedAbc, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]),
            "两个桶都低于 3 时，2 次失败加 2 次超时不能累计成降级");
    }

    [TestMethod]
    [DataRow("sticky")]
    [DataRow("prefer-highest")]
    public void DemotionKeepsConfiguredOrderWithinBothGroups(string policy)
    {
        var doc = CandidateDocument(policy, demoteThreshold: 3);
        doc.Virtuals[0].Targets = ["a", "b", "c", "d", "e", "f"];
        SetCount(doc, timeoutBucket: false, "a", 3);
        SetCount(doc, timeoutBucket: true, "c", 4);
        SetCount(doc, timeoutBucket: false, "e", 3);
        SetCount(doc, timeoutBucket: true, "d", 2);

        CollectionAssert.AreEqual(ExpectedBdfAce,
            ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]),
            "未降级与已降级两组各自应保持原始目标顺序");
    }

    [TestMethod]
    [DataRow("sticky")]
    [DataRow("prefer-highest")]
    public void DisabledTargetsAreFilteredEvenBelowDemoteThreshold(string policy)
    {
        var doc = CandidateDocument(policy, demoteThreshold: 100);
        doc.State.Active["auto_switch"] = "b";
        SetCount(doc, timeoutBucket: false, "b", 1);
        doc.State.Disabled[KeyFor("b")] = "2026-10-19T00:00:00.000Z";

        CollectionAssert.AreEqual(ExpectedAc, ModelRotatorTerminal.SelectCandidates(doc, doc.Virtuals[0]),
            "降级阈值不能让已禁用的目标重新进入候选");
    }

    private static RotatorDocument CandidateDocument(string policy, int? demoteThreshold = null)
    {
        var settings = new RotatorSettings { ActivationPolicy = policy };
        if (demoteThreshold.HasValue) settings.DemoteThreshold = demoteThreshold.Value;
        return new RotatorDocument
        {
            Settings = settings,
            Virtuals = [new RotatorRoute { VirtualModel = "auto_switch", Targets = ["a", "b", "c"] }]
        };
    }

    private static void SetCount(RotatorDocument doc, bool timeoutBucket, string model, int count)
        => (timeoutBucket ? doc.State.Timeouts : doc.State.Failures)[KeyFor(model)] = count;

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
