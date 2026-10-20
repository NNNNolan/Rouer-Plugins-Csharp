using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.ForwardAPI;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Tests;

/// <summary>通过公开 Contracts Mock 验证跨 API 响应规范化，不引入宿主协议写出实现。</summary>
[TestClass]
public sealed class ForwardApiProtocolResponseTests
{
    private const string Alias = "requested-alias";
    private const string ChatEndpoint = "/v1/chat/completions";
    private const string ResponsesEndpoint = "/v1/responses";
    private const string Arguments = "{\"city\":\"北京\"}";
    private const string ResponsesCompletion = """
        {"id":"resp_test","model":"provider-model","status":"completed","output":[
          {"type":"reasoning","summary":[{"type":"summary_text","text":"think"}]},
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello"},{"type":"refusal","refusal":" refused"}]},
          {"type":"function_call","id":"fc_test","call_id":"call_test","name":"weather","arguments":"{\"city\":\"北京\"}"}
        ],"usage":{"input_tokens":11,"output_tokens":7,"total_tokens":18}}
        """;
    private const string ChatCompletion = """
        {"id":"chat_test","model":"provider-model","choices":[{"index":0,"message":{"role":"assistant",
          "content":[{"type":"text","text":"hello"}],"refusal":" refused","reasoning_content":"think",
          "tool_calls":[{"id":"call_test","type":"function","function":{"name":"weather","arguments":"{\"city\":\"北京\"}"}}]},
          "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}
        """;

    [TestMethod]
    [DataRow("responses", ChatEndpoint)]
    [DataRow("chat", ResponsesEndpoint)]
    public async Task CrossApiInvokeParsesTextRefusalReasoningToolsUsageAndKeepsRequestedAlias(string api, string endpoint)
    {
        using var harness = new Harness(api, endpoint, false, api == "chat" ? ChatCompletion : ResponsesCompletion);

        var result = await harness.InvokeAsync();

        Assert.AreEqual(api == "chat" ? ChatEndpoint : ResponsesEndpoint, harness.RequestUri!.AbsolutePath);
        AssertCanonical(result.Response, streaming: false);
        Assert.AreEqual(200, result.Attempt.StatusCode);
        Assert.AreEqual(PluginAccountAction.None, result.Attempt.Decision!.AccountAction);
        var completion = result.Response.Completion!;
        Assert.AreEqual(Alias, completion.Model, "Provider model names must not escape the requested alias.");
        Assert.AreEqual("hello refused", completion.Content);
        Assert.AreEqual("think", completion.ReasoningContent);
        Assert.AreEqual("tool_calls", completion.FinishReason);
        Assert.AreEqual(new Usage(11, 7, 18), completion.Usage);
        Assert.AreEqual(completion.Usage, result.Response.Usage);
        var tool = completion.ToolCalls!.Single();
        Assert.AreEqual(0, tool.Index);
        Assert.AreEqual("call_test", tool.Id);
        Assert.AreEqual("weather", tool.Name);
        Assert.AreEqual(Arguments, tool.Arguments);
        using var sent = JsonDocument.Parse(harness.RequestBody!);
        Assert.AreEqual("provider-model", sent.RootElement.GetProperty("model").GetString());
    }

    [TestMethod]
    [DataRow("max_output_tokens", "length")]
    [DataRow("content_filter", "content_filter")]
    public async Task IncompleteResponsesAreSuccessfulCompletionsWithExplicitFinishReason(string reason, string finish)
    {
        var json = JsonSerializer.Serialize(new
        {
            status = "incomplete", output = Array.Empty<object>(), incomplete_details = new { reason },
            usage = new { input_tokens = 5, output_tokens = 2 }
        });
        using var harness = new Harness("responses", ChatEndpoint, false, json);

        var result = await harness.InvokeAsync();

        AssertCanonical(result.Response, false);
        Assert.AreEqual(finish, result.Response.Completion!.FinishReason);
        Assert.AreEqual(new Usage(5, 2, 7), result.Response.Usage);
    }

    [TestMethod]
    [DataRow("chat", "{\"choices\":[]}")]
    [DataRow("chat", "{\"choices\":[{\"message\":{\"content\":\"unfinished\"}}]}")]
    [DataRow("responses", "{\"status\":\"in_progress\",\"output\":[]}")]
    [DataRow("responses", "{\"status\":\"failed\",\"error\":{\"message\":\"provider failure\"}}")]
    [DataRow("responses", "{\"status\":\"completed\",\"output\":[{\"type\":\"web_search_call\"}]}")]
    [DataRow("responses", "not json")]
    public async Task InvalidCrossApiCompletionBecomesNormalizedError(string api, string body)
    {
        using var harness = new Harness(api, Incoming(api), false, body);

        var result = await harness.InvokeAsync();

        Assert.IsFalse(result.Response.IsSuccess);
        Assert.IsFalse(result.Response.IsRawPassthrough);
        Assert.IsNull(result.Response.RawContent);
        Assert.IsNull(result.Response.Completion);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Response.Error));
        Assert.AreEqual(PluginAccountAction.None, result.Attempt.Decision!.AccountAction);
    }

    [TestMethod]
    [DataRow("original", ChatEndpoint, false)]
    [DataRow("original", ResponsesEndpoint, false)]
    [DataRow("chat", ChatEndpoint, false)]
    [DataRow("responses", ResponsesEndpoint, false)]
    [DataRow("original", ChatEndpoint, true)]
    [DataRow("original", ResponsesEndpoint, true)]
    [DataRow("chat", ChatEndpoint, true)]
    [DataRow("responses", ResponsesEndpoint, true)]
    public async Task OriginalAndSameProtocolRemainByteExactRawPassthrough(string api, string endpoint, bool streaming)
    {
        const string body = " {\"unknown\":9007199254740993,\"provider\":\"原始\"} \n";
        var wire = streaming ? "data: " + body + "\ndata: [DONE]\n\n" : body;
        using var harness = new Harness(api, endpoint, streaming, wire);

        var result = await harness.InvokeAsync();

        Assert.IsTrue(result.Response.IsRawPassthrough);
        Assert.IsNull(result.Response.Completion);
        Assert.IsNull(result.Response.Stream);
        Assert.AreEqual(endpoint, harness.RequestUri!.AbsolutePath);
        if (streaming)
        {
            Assert.IsNotNull(result.Response.RawStream);
            using var bytes = new MemoryStream();
            await foreach (var chunk in result.Response.RawStream) await bytes.WriteAsync(chunk);
            Assert.AreEqual(wire, Encoding.UTF8.GetString(bytes.ToArray()));
            if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
        }
        else Assert.AreEqual(wire, Encoding.UTF8.GetString(result.Response.RawContent!));
    }

    [TestMethod]
    public async Task ResponsesStreamingDoesNotRepeatTextReasoningOrToolArgumentsFromFinalSnapshots()
    {
        var wire = Events(
            """{"type":"response.created","response":{"status":"in_progress"}}""",
            """{"type":"response.output_text.delta","output_index":1,"delta":"hello"}""",
            """{"type":"response.refusal.delta","output_index":1,"delta":" refused"}""",
            """{"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"think"}""",
            """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_test","call_id":"call_test","name":"weather","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","output_index":2,"item_id":"fc_test","delta":"{\"city\":"}""",
            """{"type":"response.function_call_arguments.delta","output_index":2,"item_id":"fc_test","delta":"\"北京\"}"}""",
            """{"type":"response.function_call_arguments.done","output_index":2,"item_id":"fc_test","arguments":"{\"city\":\"北京\"}"}""",
            """{"type":"response.output_item.done","output_index":2,"item":{"type":"function_call","id":"fc_test","call_id":"call_test","name":"weather","arguments":"{\"city\":\"北京\"}"}}""",
            CompletedEvent(ResponsesCompletion));
        using var harness = new Harness("responses", ChatEndpoint, true, wire);

        var result = await harness.InvokeAsync();
        var chunks = await CollectAsync(result.Response);

        AssertCanonical(result.Response, true);
        AssertStreamContent(chunks);
        Assert.AreEqual("assistant", chunks.First(chunk => chunk.Role is not null).Role);
        Assert.AreEqual("tool_calls", chunks.Last().FinishReason);
        Assert.AreEqual(1, chunks.Count(chunk => chunk.FinishReason is not null), "Only the terminal chunk may carry the completion marker.");
        Assert.AreEqual(new Usage(11, 7, 18), chunks.Last().Usage);
        Assert.IsTrue(chunks.Take(chunks.Count - 1).All(chunk => chunk.Usage is null), "Usage must be reported once, on the terminal chunk.");
        Assert.IsTrue(harness.BodyStream.Disposed);
    }

    [TestMethod]
    public async Task ChatStreamingPreservesRefusalReasoningToolFragmentsAndUsageBeforeDone()
    {
        var wire = Events(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"hello","refusal":" refused","reasoning_content":"think","tool_calls":[{"index":0,"id":"call_test","type":"function","function":{"name":"weather","arguments":"{\"city\":"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"北京\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}""",
            "[DONE]");
        using var harness = new Harness("chat", ResponsesEndpoint, true, wire);

        var result = await harness.InvokeAsync();
        var chunks = await CollectAsync(result.Response);

        AssertCanonical(result.Response, true);
        AssertStreamContent(chunks);
        Assert.AreEqual("tool_calls", chunks.Last().FinishReason);
        Assert.AreEqual(1, chunks.Count(chunk => chunk.FinishReason is not null), "Only the terminal chunk may carry the completion marker.");
        Assert.AreEqual(new Usage(11, 7, 18), chunks.Last().Usage);
        Assert.IsTrue(chunks.Take(chunks.Count - 1).All(chunk => chunk.Usage is null), "Usage must be reported once, on the terminal chunk.");
        Assert.AreEqual(1, chunks.Count(chunk => chunk.FinishReason is not null));
    }

    [TestMethod]
    [DataRow("arguments.done")]
    [DataRow("item.done")]
    [DataRow("completed")]
    public async Task ResponsesToolsWithoutDeltasUseExactlyOneFinalSnapshot(string snapshot)
    {
        const string tool = """{"type":"function_call","id":"fc_test","call_id":"call_test","name":"weather","arguments":"{\"city\":\"北京\"}"}""";
        var completion = "{\"status\":\"completed\",\"output\":[" + tool + "]}";
        var frames = new List<string>();
        if (snapshot == "arguments.done")
        {
            frames.Add("{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":" + tool + "}");
            frames.Add("""{"type":"response.function_call_arguments.done","output_index":0,"arguments":"{\"city\":\"北京\"}"}""");
        }
        if (snapshot == "item.done") frames.Add("{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":" + tool + "}");
        frames.Add(CompletedEvent(completion));
        using var harness = new Harness("responses", ChatEndpoint, true, Events(frames.ToArray()));

        var chunks = await CollectAsync((await harness.InvokeAsync()).Response);

        var calls = chunks.SelectMany(chunk => chunk.ToolCalls ?? []).ToArray();
        Assert.AreEqual(Arguments, string.Concat(calls.Select(call => call.Arguments ?? string.Empty)));
        Assert.AreEqual(1, calls.Count(call => call.Id == "call_test"));
        Assert.AreEqual(1, calls.Count(call => call.Name == "weather"));
        Assert.AreEqual("tool_calls", chunks.Last().FinishReason);
        Assert.AreEqual(1, chunks.Count(chunk => chunk.FinishReason is not null), "Only the terminal chunk may carry the completion marker.");
        Assert.IsFalse(chunks.Any(chunk => chunk.Error is not null));
    }

    [TestMethod]
    public async Task ResponsesCompletedSnapshotSuppliesTextAndReasoningWhenNoDeltasArrive()
    {
        using var harness = new Harness("responses", ChatEndpoint, true, Events(CompletedEvent(ResponsesCompletion)));

        var chunks = await CollectAsync((await harness.InvokeAsync()).Response);

        AssertStreamContent(chunks);
        Assert.AreEqual(new Usage(11, 7, 18), chunks.Last().Usage);
        Assert.IsTrue(chunks.Take(chunks.Count - 1).All(chunk => chunk.Usage is null), "Usage must be reported once, on the terminal chunk.");
    }

    [TestMethod]
    public async Task ResponsesIncompleteStreamEmitsLengthAndUsage()
    {
        const string completion = """{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[],"usage":{"input_tokens":3,"output_tokens":2}}""";
        using var harness = new Harness("responses", ChatEndpoint, true,
            Events("{\"type\":\"response.incomplete\",\"response\":" + completion + "}"));

        var chunks = await CollectAsync((await harness.InvokeAsync()).Response);

        Assert.AreEqual("length", chunks.Single().FinishReason);
        Assert.AreEqual(new Usage(3, 2, 5), chunks.Single().Usage);
        Assert.IsNull(chunks.Single().Error);
    }

    [TestMethod]
    [DataRow("chat", "data: [DONE]\n\n")]
    [DataRow("chat", "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [DataRow("chat", "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")]
    [DataRow("chat", "data: {malformed}\n\n")]
    [DataRow("responses", "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")]
    [DataRow("responses", "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"failed\"}}}\n\n")]
    [DataRow("responses", "data: {\"type\":\"error\",\"message\":\"failed\"}\n\n")]
    [DataRow("responses", "data: {\"error\":{\"message\":\"failed\"}}\n\n")]
    [DataRow("responses", "data: {\"type\":")]
    [DataRow("responses", "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n")]
    [DataRow("responses", "data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\"}}\n\n")]
    public async Task MalformedFailedOrTruncatedStreamsEmitErrorWithoutSuccessfulFinish(string api, string wire)
    {
        using var harness = new Harness(api, Incoming(api), true, wire);

        var chunks = await CollectAsync((await harness.InvokeAsync()).Response);

        var error = chunks.Single(chunk => chunk.Error is not null);
        Assert.AreEqual("upstream_error", error.ErrorType);
        Assert.IsFalse(chunks.Any(chunk => chunk.FinishReason is not null));
        Assert.IsTrue(harness.BodyStream.Disposed);
    }

    [TestMethod]
    public async Task MultilineSseDataCommentsAndUtf8NetworkFragmentsAreParsed()
    {
        const string wire = ": keepalive\r\nevent: message\r\ndata: {\"choices\":[{\"index\":0,\r\ndata: \"delta\":{\"content\":\"你好🌍\"},\"finish_reason\":null}]}\r\n\r\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\r\n\r\ndata: [DONE]\r\n\r\n";
        using var harness = new Harness("chat", ResponsesEndpoint, true, wire, fragmentBytes: 1);

        var chunks = await CollectAsync((await harness.InvokeAsync()).Response);

        Assert.AreEqual("你好🌍", Joined(chunks.Select(chunk => chunk.Delta)));
        Assert.AreEqual("stop", chunks.Last().FinishReason);
        Assert.IsFalse(chunks.Any(chunk => chunk.Error is not null));
    }

    [TestMethod]
    [DataRow("responses")]
    [DataRow("chat")]
    public async Task UnenumeratedConvertedStreamCanBeDisposedAndDisposalIsIdempotent(string api)
    {
        using var harness = new Harness(api, Incoming(api), true, "");

        var response = (await harness.InvokeAsync()).Response;

        AssertCanonical(response, true);
        Assert.IsFalse(harness.BodyStream.Disposed);
        Assert.AreEqual(0, harness.BodyStream.ReadCount);
        Assert.IsNotNull(response.Lifetime, "A lazy stream must have an owner even before enumeration.");
        await response.Lifetime.DisposeAsync();
        await response.Lifetime.DisposeAsync();
        Assert.IsTrue(harness.BodyStream.Disposed);
        Assert.AreEqual(0, harness.BodyStream.ReadCount);
    }

    [TestMethod]
    [DataRow("responses")]
    [DataRow("chat")]
    public async Task CancellationDuringConvertedStreamReadPropagatesAndDisposesResponse(string api)
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new Harness(api, Incoming(api), true, "", waitForCancellation: true);
        var response = (await harness.InvokeAsync(cancellation.Token)).Response;
        await using var enumerator = response.Stream!.GetAsyncEnumerator(cancellation.Token);
        var pendingRead = enumerator.MoveNextAsync().AsTask();
        await harness.BodyStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        try
        {
            await pendingRead;
            Assert.Fail("Cancellation must propagate rather than becoming a successful finish or an upstream error chunk.");
        }
        catch (OperationCanceledException) { }
        Assert.IsTrue(harness.BodyStream.Disposed);
        if (response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
    }

    [TestMethod]
    [DataRow("responses", 401, true, false, false)]
    [DataRow("chat", 403, true, false, false)]
    [DataRow("responses", 429, false, false, true)]
    [DataRow("chat", 503, false, false, true)]
    [DataRow("responses", 407, false, true, false)]
    public async Task ConvertedErrorsKeepStatusCredentialAndProxyDecisions(string api, int status,
        bool invalidCredential, bool cooldownProxy, bool retryUpstream)
    {
        using var harness = new Harness(api, Incoming(api), false,
            """{"error":{"message":"invalid credential","type":"provider_error"}}""", status: status, usePool: true);

        var result = await harness.InvokeAsync();

        Assert.AreEqual(status, result.Response.StatusCode);
        Assert.AreEqual(status, result.Attempt.StatusCode);
        Assert.IsFalse(result.Response.IsRawPassthrough);
        Assert.IsNull(result.Response.RawContent);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Response.Error));
        Assert.AreEqual(invalidCredential, result.Attempt.IndicatesInvalidCredential);
        Assert.AreEqual(invalidCredential ? PluginAccountAction.Disable : PluginAccountAction.None, result.Attempt.Decision!.AccountAction);
        Assert.AreEqual(cooldownProxy ? PluginProxyAction.Cooldown : PluginProxyAction.None, result.Attempt.Decision.ProxyAction);
        Assert.AreEqual(invalidCredential || retryUpstream ? PluginRetryAction.NextAttempt : PluginRetryAction.None, result.Attempt.Decision.Retry);
    }

    [TestMethod]
    public void PrivateParserSupportsLegacyFunctionCallWithoutHostImplementation()
    {
        using var document = JsonDocument.Parse("""{"choices":[{"message":{"content":null,"function_call":{"name":"legacy","arguments":"{}"},"reasoning":"legacy-think"},"finish_reason":"function_call"}]}""");
        var method = typeof(ForwardApiTerminal).GetMethod("ParseProtocolCompletion", BindingFlags.NonPublic | BindingFlags.Static)!;

        var completion = (AdapterCompletion)method.Invoke(null, [document.RootElement, "chat", Alias])!;

        Assert.AreEqual(Alias, completion.Model);
        Assert.AreEqual("legacy-think", completion.ReasoningContent);
        var calls = completion.ToolCalls ?? [];
        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("legacy", calls[0].Name);
        Assert.AreEqual("{}", calls[0].Arguments);
    }

    [TestMethod]
    public async Task MessagesToResponsesConvertsNativeInputArrayAndMapsModel()
    {
        using var harness = new Harness("responses", "/v1/messages", false, ResponsesCompletion, messages:
        [
            new AdapterMessage("user", "hi")
            {
                ContentParts =
                [
                    new AdapterContentPart(AdapterContentPartKind.Text, Text: "hi"),
                    new AdapterContentPart(AdapterContentPartKind.Image, ImageUrl: "https://img.example/a.png")
                ]
            }
        ]);

        var result = await harness.InvokeAsync();

        AssertCanonical(result.Response, false);
        using var sent = JsonDocument.Parse(harness.RequestBody!);
        var root = sent.RootElement;
        Assert.IsFalse(root.TryGetProperty("messages", out _), "Anthropic messages must be converted to native Responses input.");
        var input = root.GetProperty("input");
        Assert.AreEqual(JsonValueKind.Array, input.ValueKind);
        var item = input[0];
        Assert.AreEqual("message", item.GetProperty("type").GetString());
        Assert.AreEqual("user", item.GetProperty("role").GetString());
        var content = item.GetProperty("content");
        Assert.AreEqual("input_text", content[0].GetProperty("type").GetString());
        Assert.AreEqual("hi", content[0].GetProperty("text").GetString());
        Assert.AreEqual("input_image", content[1].GetProperty("type").GetString());
        Assert.AreEqual("provider-model", root.GetProperty("model").GetString());
    }

    [TestMethod]
    public async Task MessagesToResponsesRejectsNativeFileIdAsCanonicalBadRequestWithoutUpstreamCall()
    {
        using var harness = new Harness("responses", "/v1/messages", false, ResponsesCompletion, messages:
        [
            new AdapterMessage("user", "see file")
            {
                ContentParts =
                [
                    new AdapterContentPart(AdapterContentPartKind.File, FileId: "file-123", FileName: "a.pdf")
                ]
            }
        ]);

        var result = await harness.InvokeAsync();

        Assert.AreEqual(400, result.Response.StatusCode);
        Assert.IsFalse(result.Response.IsSuccess);
        Assert.IsFalse(result.Response.IsRawPassthrough);
        Assert.IsNull(result.Response.RawContent);
        Assert.IsNull(result.Response.Completion);
        StringAssert.Contains(result.Response.Error, "file_id");
        harness.VerifyNoUpstreamCalls();
    }

    private static void AssertCanonical(AdapterResponse response, bool streaming)
    {
        Assert.AreEqual(200, response.StatusCode);
        Assert.IsTrue(response.IsSuccess);
        Assert.IsFalse(response.IsRawPassthrough);
        Assert.IsNull(response.RawContent);
        Assert.IsNull(response.RawStream);
        Assert.AreEqual(streaming, response.IsStreaming);
        if (streaming) Assert.IsNotNull(response.Stream);
        else Assert.IsNotNull(response.Completion);
    }

    private static void AssertStreamContent(List<StreamChunk> chunks)
    {
        Assert.IsFalse(chunks.Any(chunk => chunk.Error is not null));
        Assert.AreEqual("hello refused", Joined(chunks.Select(chunk => chunk.Delta)));
        Assert.AreEqual("think", Joined(chunks.Select(chunk => chunk.ReasoningDelta)));
        var calls = chunks.SelectMany(chunk => chunk.ToolCalls ?? []).ToArray();
        Assert.AreEqual(Arguments, string.Concat(calls.Select(call => call.Arguments ?? string.Empty)), "Final tool snapshots must not duplicate streamed arguments.");
        Assert.AreEqual(1, calls.Count(call => call.Id == "call_test"));
        Assert.AreEqual(1, calls.Count(call => call.Name == "weather"));
        Assert.IsTrue(calls.All(call => call.Index == 0));
    }

    private static string Joined(IEnumerable<string?> values)
        => string.Concat(values.Select(value => value ?? string.Empty));

    private static string Incoming(string api) => api == "chat" ? ResponsesEndpoint : ChatEndpoint;
    private static string Events(params string[] frames) => string.Concat(frames.Select(frame => "data: " + frame.Replace("\n", "\ndata: ", StringComparison.Ordinal) + "\n\n"));
    private static string CompletedEvent(string completion) => "{\"type\":\"response.completed\",\"response\":" + completion + "}";

    private static async Task<List<StreamChunk>> CollectAsync(AdapterResponse response)
    {
        Assert.IsNotNull(response.Stream, "Cross API SSE must expose canonical StreamChunk values.");
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in response.Stream) chunks.Add(chunk);
        if (response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
        return chunks;
    }

    private sealed class Harness : IDisposable
    {
        private readonly ForwardApiTerminal _terminal = new(PluginTestHost.Create("forwardapi"));
        private readonly Mock<IPluginHttpClient> _client = new(MockBehavior.Strict);
        private readonly Account _account;
        private readonly string _endpoint;
        private readonly bool _streaming;
        private readonly IReadOnlyList<AdapterMessage>? _messages;
        public readonly ObservableStream BodyStream;
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        public Harness(string api, string endpoint, bool streaming, string body, int status = 200,
            bool usePool = false, int fragmentBytes = int.MaxValue, bool waitForCancellation = false,
            IReadOnlyList<AdapterMessage>? messages = null)
        {
            _endpoint = endpoint;
            _streaming = streaming;
            _messages = messages;
            BodyStream = new ObservableStream(Encoding.UTF8.GetBytes(body), fragmentBytes, waitForCancellation);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(BodyStream) };
            response.Content.Headers.ContentType = new(streaming ? "text/event-stream" : "application/json");
            _client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), usePool,
                    HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
                .Returns(async (HttpRequestMessage request, bool poolFlag, HttpCompletionOption option, CancellationToken token) =>
                {
                    _ = (poolFlag, option);
                    RequestUri = request.RequestUri;
                    RequestBody = await request.Content!.ReadAsStringAsync(token);
                    return response;
                });
            _account = new Account
            {
                Id = "protocol-account", PluginKey = "forwardapi", Platform = "forwardapi",
                Credential = new CustomCredential(new Dictionary<string, string?>
                {
                    ["settings"] = JsonSerializer.Serialize(new
                    {
                        siteType = "Custom", baseUrl = "http://upstream.invalid/v1", apiKey = "test-protocol-key",
                        upstreamApi = api, proxyMode = usePool ? "pool" : "direct",
                        extraParams = "{\"ModelMap\":{\"requested-alias\":\"provider-model\"}}"
                    }),
                    ["modelsConfigured"] = "true", ["models"] = "[\"requested-alias\"]", ["availableModels"] = "[\"requested-alias\"]"
                })
            };
        }

        public Task<PluginInvocationResult> InvokeAsync(CancellationToken token = default) => _terminal.InvokeAsync(new PluginAttemptContext
        {
            PluginKey = "forwardapi", PlatformName = "forwardapi", Account = _account,
            HttpClient = _client.Object, CancellationToken = token,
            Request = new AdapterRequest
            {
                Model = Alias, Endpoint = _endpoint, Stream = _streaming,
                Messages = _messages ?? [new AdapterMessage("user", "hi")],
                OriginalBody = _endpoint == ResponsesEndpoint
                    ? JsonSerializer.SerializeToElement(new { model = Alias, stream = _streaming, input = "hi" })
                    : JsonSerializer.SerializeToElement(new { model = Alias, stream = _streaming, messages = new[] { new { role = "user", content = "hi" } } })
            }
        });

        public void VerifyNoUpstreamCalls() => _client.Verify(
            value => value.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<bool>(),
                It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()), Times.Never);

        public void Dispose()
        {
            _terminal.Dispose();
            BodyStream.Dispose();
        }
    }

    private sealed class ObservableStream(byte[] bytes, int fragmentBytes, bool waitForCancellation) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public int ReadCount { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            ReadStarted.TrySetResult();
            if (waitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentBytes)], cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
