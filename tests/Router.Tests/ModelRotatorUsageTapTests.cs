using System.Text;
using Plugins.ModelRotator;

namespace Router.Tests;

[TestClass]
public sealed class ModelRotatorUsageTapTests
{
    private static void Feed(ModelRotatorTerminal.StreamUsageTap tap, string text) =>
        tap.Feed(Encoding.UTF8.GetBytes(text));

    [TestMethod]
    public void ReadsUsageFromFinalSseChunk()
    {
        var tap = new ModelRotatorTerminal.StreamUsageTap();
        Feed(tap, "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}],\"usage\":null}\n\n");
        Feed(tap, "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}],\"usage\":null}\n\n");
        Feed(tap, "data: {\"choices\":[{\"delta\":{\"content\":null},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":39,\"completion_tokens\":51,\"total_tokens\":90}}\n\n");
        Feed(tap, "data: [DONE]\n\n");

        var usage = tap.Usage;
        Assert.IsNotNull(usage);
        Assert.AreEqual(39, usage.PromptTokens);
        Assert.AreEqual(51, usage.CompletionTokens);
    }

    [TestMethod]
    public void ReadsUsageSplitAcrossChunks()
    {
        var tap = new ModelRotatorTerminal.StreamUsageTap();
        Feed(tap, "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"comp");
        Feed(tap, "letion_tokens\":7,\"total_tokens\":19}}\n\n");

        var usage = tap.Usage;
        Assert.IsNotNull(usage);
        Assert.AreEqual(12, usage.PromptTokens);
        Assert.AreEqual(7, usage.CompletionTokens);
    }

    [TestMethod]
    public void MergesAnthropicStartAndDeltaUsage()
    {
        var tap = new ModelRotatorTerminal.StreamUsageTap();
        Feed(tap, "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":12,\"output_tokens\":1}}}\n\n");
        Feed(tap, "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":57}}\n\n");

        var usage = tap.Usage;
        Assert.IsNotNull(usage);
        Assert.AreEqual(12, usage.PromptTokens);
        Assert.AreEqual(57, usage.CompletionTokens);
    }

    [TestMethod]
    public void IgnoresNestedDetailsAndReportsNullWhenAbsent()
    {
        var tap = new ModelRotatorTerminal.StreamUsageTap();
        Feed(tap, "{\"id\":\"x\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"completion_tokens_details\":{\"reasoning_tokens\":8}}}\n");
        Assert.IsNotNull(tap.Usage);
        Assert.AreEqual(100, tap.Usage.PromptTokens);
        Assert.AreEqual(20, tap.Usage.CompletionTokens);

        var empty = new ModelRotatorTerminal.StreamUsageTap();
        Feed(empty, "data: {\"choices\":[{\"delta\":{}}],\"usage\":null}\n\n");
        Feed(empty, "data: [DONE]\n\n");
        Assert.IsNull(empty.Usage);
    }

    [TestMethod]
    public void IgnoresTextThatMerelyMentionsUsage()
    {
        var tap = new ModelRotatorTerminal.StreamUsageTap();
        Feed(tap, "data: {\"choices\":[{\"delta\":{\"content\":\"the usage field is null\"}}]}\n\n");
        Assert.IsNull(tap.Usage);
    }
}
