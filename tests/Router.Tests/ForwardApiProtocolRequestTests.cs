using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plugins.ForwardAPI;
using Router.Contracts.Domain;

namespace Router.Tests;

/// <summary>通过反射调用协议转换器，验证支持的保留语义并确保不访问网络。</summary>
[TestClass]
public sealed class ForwardApiProtocolRequestTests
{
    private static readonly string[] ExpectedAssistantText = ["before", "between", "after"];
    private static readonly string[] ExpectedParallelCallIds = ["a", "b"];

    [TestMethod]
    public void ConvertChatToResponsesPreservesMessagesToolsAndOptions()
    {
        var request = CreateRequest("/v1/chat/completions", """
            {
              "model":"model-a",
              "stream":true,
              "temperature":0.25,
              "top_p":0.8,
              "max_completion_tokens":256,
              "messages":[
                {"role":"system","content":"be exact"},
                {"role":"user","content":[
                  {"type":"text","text":"look"},
                  {"type":"image_url","image_url":{"url":"https://example.invalid/a.png","detail":"low"}}
                ]},
                {"role":"assistant","content":"","tool_calls":[{"id":"call-1","type":"function","function":{"name":"get","arguments":"{  \"q\":1}"}}]},
                {"role":"tool","content":"answer","tool_call_id":"call-1"}
              ],
              "tools":[{"type":"function","function":{"name":"get","description":"d","parameters":{"type":"object"},"strict":false}}],
              "tool_choice":{"type":"function","function":{"name":"get"}},
              "reasoning_effort":"high",
              "response_format":{"type":"json_schema","json_schema":{"name":"out","schema":{"type":"object"}}}
            }
            """);
        var converted = Convert(request, "responses");

        Assert.AreEqual("model-a", (string?)converted["model"]);
        Assert.AreEqual(true, (bool?)converted["stream"]);
        Assert.AreEqual(false, (bool?)converted["store"]);
        Assert.AreEqual(256, (int?)converted["max_output_tokens"]);
        Assert.AreEqual(0.25m, (decimal?)converted["temperature"]);
        Assert.AreEqual(4, converted["input"]!.AsArray().Count, converted["input"]!.ToJsonString());
        Assert.AreEqual("system", converted["input"]![0]!["role"]!.GetValue<string>());
        var input = converted["input"]![1]!["content"]!.AsArray();
        Assert.AreEqual("input_text", input[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("input_image", input[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("https://example.invalid/a.png", input[1]!["image_url"]!.GetValue<string>());
        Assert.AreEqual("low", input[1]!["detail"]!.GetValue<string>());
        Assert.AreEqual("function_call", converted["input"]![2]!["type"]!.GetValue<string>());
        Assert.AreEqual("call-1", converted["input"]![2]!["call_id"]!.GetValue<string>());
        Assert.AreEqual("{  \"q\":1}", converted["input"]![2]!["arguments"]!.GetValue<string>());
        Assert.AreEqual("function_call_output", converted["input"]![3]!["type"]!.GetValue<string>());
        var tools = converted["tools"]!.AsArray();
        var function = tools[0]!.AsObject();
        Assert.AreEqual("function", function["type"]!.GetValue<string>());
        Assert.AreEqual("get", function["name"]!.GetValue<string>());
        Assert.AreEqual(false, (bool)function["strict"]!.AsValue());
        var choice = converted["tool_choice"]!.AsObject();
        Assert.AreEqual("function", choice["type"]!.GetValue<string>());
        Assert.AreEqual("get", choice["name"]!.GetValue<string>());
        var format = converted["text"]!["format"]!.AsObject();
        Assert.AreEqual("json_schema", format["type"]!.GetValue<string>());
        Assert.AreEqual("out", format["name"]!.GetValue<string>());
        Assert.AreEqual(false, format["strict"]!.GetValue<bool>());
        Assert.AreEqual("high", converted["reasoning"]!["effort"]!.GetValue<string>());
    }

    [TestMethod]
    public void ConvertResponsesToChatMapsInstructionsToolHistoryAndOptions()
    {
        var request = CreateRequest("/v1/responses", """
            {
              "model":"model-b",
              "stream":false,
              "instructions":"be exact",
              "input":[
                {"type":"message","role":"user","content":[{"type":"input_text","text":"run"}]},
                {"type":"function_call","call_id":"call-2","name":"get","arguments":"{}"},
                {"type":"function_call_output","call_id":"call-2","output":"done"}
              ],
              "tools":[{"type":"function","name":"get","parameters":{"type":"object"},"strict":true}],
              "tool_choice":{"type":"function","name":"get"},
              "reasoning":{"effort":"high"},
              "text":{"format":{"type":"json_schema","name":"resp","schema":{"type":"object"}}},
              "max_output_tokens":128,
              "user":"tenant"
            }
            """);
        var converted = Convert(request, "chat");

        Assert.IsFalse(converted.ContainsKey("store"));
        Assert.AreEqual(128, (int?)converted["max_completion_tokens"]);
        Assert.IsFalse(converted.ContainsKey("max_tokens"));
        Assert.IsFalse(converted.ContainsKey("stream_options"));
        var messages = converted["messages"]!.AsArray();
        Assert.AreEqual(4, messages.Count);
        Assert.AreEqual("be exact", messages[0]!["content"]!.GetValue<string>());
        Assert.AreEqual("developer", messages[0]!["role"]!.GetValue<string>());
        var toolCall = messages[2]!["tool_calls"]![0]!.AsObject();
        Assert.AreEqual("call-2", toolCall["id"]!.GetValue<string>());
        Assert.AreEqual("get", toolCall["function"]!["name"]!.GetValue<string>());
        Assert.AreEqual("call-2", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.AreEqual("done", messages[3]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("tenant", (string?)converted["user"]);
        var tool = converted["tools"]![0]!.AsObject();
        Assert.AreEqual("function", tool["type"]!.GetValue<string>());
        Assert.AreEqual("get", tool["function"]!["name"]!.GetValue<string>());
        Assert.AreEqual("function", converted["tool_choice"]!["type"]!.GetValue<string>());
        Assert.AreEqual("high", (string?)converted["reasoning_effort"]);
        var format = converted["response_format"]!.AsObject();
        Assert.AreEqual("json_schema", format["type"]!.GetValue<string>());
        Assert.AreEqual("resp", format["json_schema"]!["name"]!.GetValue<string>());
        Assert.AreEqual(false, format["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [TestMethod]
    public void ConvertResponsesToChatGroupsParallelCallsAndAssistantContentWithinEachTurn()
    {
        var request = CreateRequest("/v1/responses", """
            {"model":"m","input":[
              {"role":"user","content":"run"},
              {"role":"assistant","content":[{"type":"output_text","text":"before"}]},
              {"type":"function_call","call_id":"a","name":"first","arguments":"{  \"x\":1}"},
              {"role":"assistant","content":[{"type":"output_text","text":"between"}]},
              {"type":"function_call","call_id":"b","name":"second","arguments":"{}"},
              {"role":"assistant","content":"after"},
              {"type":"function_call_output","call_id":"a","output":"A"},
              {"type":"function_call_output","call_id":"b","output":"B"},
              {"role":"assistant","content":"next turn"},
              {"type":"function_call","call_id":"c","name":"third","arguments":"{}"},
              {"type":"function_call_output","call_id":"c","output":"C"},
              {"role":"user","content":"again"},
              {"type":"function_call","call_id":"d","name":"fourth","arguments":"{}"},
              {"type":"function_call_output","call_id":"d","output":"D"}
            ]}
            """);

        var messages = Convert(request, "chat")["messages"]!.AsArray();

        Assert.AreEqual(9, messages.Count, messages.ToJsonString());
        var assistant = messages[1]!.AsObject();
        Assert.AreEqual("assistant", (string?)assistant["role"]);
        var content = assistant["content"]!.AsArray();
        CollectionAssert.AreEqual(ExpectedAssistantText,
            content.Select(part => part!["text"]!.GetValue<string>()).ToArray());
        var calls = assistant["tool_calls"]!.AsArray();
        CollectionAssert.AreEqual(ExpectedParallelCallIds, calls.Select(call => call!["id"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("first", (string?)calls[0]!["function"]!["name"]);
        Assert.AreEqual("{  \"x\":1}", (string?)calls[0]!["function"]!["arguments"]);
        Assert.AreEqual("a", (string?)messages[2]!["tool_call_id"]);
        Assert.AreEqual("b", (string?)messages[3]!["tool_call_id"]);
        Assert.AreEqual("next turn", (string?)messages[4]!["content"]![0]!["text"]);
        Assert.AreEqual(1, messages[4]!["tool_calls"]!.AsArray().Count);
        Assert.AreEqual("c", (string?)messages[4]!["tool_calls"]![0]!["id"]);
        Assert.AreEqual("user", (string?)messages[6]!["role"]);
        Assert.AreEqual("d", (string?)messages[7]!["tool_calls"]![0]!["id"]);
    }

    [TestMethod]
    public void ConvertResponsesToChatGroupsCallsWithoutAssistantText()
    {
        var request = CreateRequest("/v1/responses", """
            {"model":"m","input":[
              {"type":"function_call","call_id":"a","name":"first","arguments":"{}"},
              {"type":"function_call","call_id":"b","name":"second","arguments":"{}"},
              {"type":"function_call_output","call_id":"b","output":"B"},
              {"type":"function_call_output","call_id":"a","output":"A"}
            ]}
            """);

        var messages = Convert(request, "chat")["messages"]!.AsArray();

        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual("assistant", (string?)messages[0]!["role"]);
        Assert.AreEqual(string.Empty, (string?)messages[0]!["content"]);
        CollectionAssert.AreEqual(ExpectedParallelCallIds, messages[0]!["tool_calls"]!.AsArray()
            .Select(call => call!["id"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("b", (string?)messages[1]!["tool_call_id"]);
        Assert.AreEqual("a", (string?)messages[2]!["tool_call_id"]);
    }

    [TestMethod]
    [DataRow("/v1/responses", "max_output_tokens")]
    [DataRow("/v1/responses", "max_completion_tokens")]
    [DataRow("/v1/messages", "max_tokens")]
    [DataRow("/v1/completions", "max_tokens")]
    public void ConvertToChatUsesReasoningCompatibleOutputTokenLimit(string endpoint, string sourceLimit)
    {
        var request = CreateRequest(endpoint, $$"""{"model":"o3","{{sourceLimit}}":321}""");
        request.Messages = [new("user", "run")];

        var converted = Convert(request, "chat");

        Assert.AreEqual(321, (int?)converted["max_completion_tokens"]);
        Assert.IsFalse(converted.ContainsKey("max_tokens"));
        Assert.IsFalse(converted.ContainsKey("max_output_tokens"));
    }

    [TestMethod]
    public void ConvertToChatUsesNormalizedOutputTokenLimit()
    {
        var request = CreateRequest("/v1/responses", """{"model":"o3","input":"run"}""");
        request.MaxTokens = 456;

        var converted = Convert(request, "chat");

        Assert.AreEqual(456, (int?)converted["max_completion_tokens"]);
        Assert.IsFalse(converted.ContainsKey("max_tokens"));
    }

    [TestMethod]
    [DataRow("/v1/responses", true)]
    [DataRow("/v1/messages", true)]
    [DataRow("/v1/completions", true)]
    [DataRow("/v1/responses", false)]
    public void ConvertToChatRequestsUsageOnlyForStreams(string endpoint, bool stream)
    {
        var request = CreateRequest(endpoint, $$"""{"model":"m","stream":{{(stream ? "true" : "false")}}}""");
        request.Messages = [new("user", "run")];

        var converted = Convert(request, "chat");

        if (stream) Assert.AreEqual(true, (bool?)converted["stream_options"]!["include_usage"]);
        else Assert.IsFalse(converted.ContainsKey("stream_options"));
    }

    [TestMethod]
    public void ConvertToChatEnablesUsageWhenSourceDisablesIt()
    {
        var request = CreateRequest("/v1/responses", """
            {"model":"m","stream":true,"input":"run","stream_options":{"include_usage":false}}
            """);

        var converted = Convert(request, "chat");

        Assert.AreEqual(true, (bool?)converted["stream_options"]!["include_usage"]);
    }

    [TestMethod]
    [DataRow("chat", true)]
    [DataRow("chat", false)]
    [DataRow("responses", true)]
    [DataRow("responses", false)]
    public void ConvertPreservesExplicitToolAndTextFormatStrict(string target, bool strict)
    {
        var strictJson = strict ? "true" : "false";
        var request = target == "responses"
            ? CreateRequest("/v1/chat/completions", $$"""
                {"model":"m","messages":[{"role":"user","content":"run"}],
                 "tools":[{"type":"function","function":{"name":"get","parameters":{"type":"object"},"strict":{{strictJson}} } }],
                 "response_format":{"type":"json_schema","json_schema":{"name":"out","schema":{"type":"object"},"strict":{{strictJson}} } } }
                """)
            : CreateRequest("/v1/responses", $$"""
                {"model":"m","input":"run",
                 "tools":[{"type":"function","name":"get","parameters":{"type":"object"},"strict":{{strictJson}} }],
                 "text":{"format":{"type":"json_schema","name":"out","schema":{"type":"object"},"strict":{{strictJson}} } } }
                """);

        var converted = Convert(request, target);

        var tool = converted["tools"]![0]!;
        var schema = target == "responses" ? converted["text"]!["format"]! : converted["response_format"]!["json_schema"]!;
        Assert.AreEqual(strict, (target == "responses" ? tool["strict"]! : tool["function"]!["strict"]!).GetValue<bool>());
        Assert.AreEqual(strict, schema["strict"]!.GetValue<bool>());
    }

    [TestMethod]
    public void ConvertResponsesToChatKeepsDistinctOmittedToolAndTextStrictDefaults()
    {
        var request = CreateRequest("/v1/responses", """
            {"model":"m","input":"run",
             "tools":[{"type":"function","name":"get","parameters":{"type":"object"}}],
             "text":{"format":{"type":"json_schema","name":"out","schema":{"type":"object"}}}}
            """);

        var converted = Convert(request, "chat");

        Assert.AreEqual(true, (bool?)converted["tools"]![0]!["function"]!["strict"]);
        Assert.AreEqual(false, (bool?)converted["response_format"]!["json_schema"]!["strict"]);
    }

    [TestMethod]
    public void ConvertAnthropicMessagesToChatPreservesAttachments()
    {
        var request = CreateRequest("/v1/messages", """
            {
              "model":"model-c",
              "system":"site rules",
              "temperature":1,
              "messages":[
                {"role":"user","content":[
                  {"type":"text","text":"hi"},
                  {"type":"image","source":{"type":"url","url":"https://example.invalid/x.png"}}
                ]}
              ],
              "tools":[{"type":"custom","name":"echo","description":"d","input_schema":{"type":"object"} }]
            }
            """);
        request.Messages = [
            new("system", "site rules"),
            new("user", "hi") { ContentParts = [new(AdapterContentPartKind.Text, Text: "hi"), new(AdapterContentPartKind.Image, ImageUrl: "https://example.invalid/x.png")] }
        ];
        var converted = Convert(request, "chat");

        var messages = converted["messages"]!.AsArray();
        Assert.AreEqual("system", messages[0]!["role"]!.GetValue<string>());
        Assert.AreEqual("site rules", messages[0]!["content"]!.GetValue<string>());
        Assert.AreEqual("user", messages[1]!["role"]!.GetValue<string>());
        var blocks = messages[1]!["content"]!.AsArray();
        Assert.AreEqual("text", blocks[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("image_url", blocks[1]!["type"]!.GetValue<string>());
        Assert.AreEqual("https://example.invalid/x.png", blocks[1]!["image_url"]!.AsObject()["url"]!.GetValue<string>());
        var tool = converted["tools"]![0]!.AsObject();
        Assert.AreEqual("echo", tool["function"]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("/v1/responses", "chat", "store", "true")]
    [DataRow("/v1/responses", "chat", "previous_response_id", "\"old\"")]
    [DataRow("/v1/responses", "chat", "conversation", "\"old\"")]
    [DataRow("/v1/responses", "chat", "background", "true")]
    [DataRow("/v1/responses", "chat", "n", "2")]
    [DataRow("/v1/responses", "chat", "logprobs", "true")]
    [DataRow("/v1/responses", "chat", "unknown_field", "{}")]
    [DataRow("/v1/chat/completions", "responses", "logit_bias", "{}")]
    [DataRow("/v1/chat/completions", "responses", "stop", "[\"x\"]")]
    public void ConvertRejectsLossyOptions(string endpoint, string target, string field, string value)
    {
        var request = CreateRequest(endpoint, $$"""{"model":"m","{{field}}":{{value}}}""");
        NotSupported(() => Convert(request, target));
    }

    [TestMethod]
    [DataRow("reasoning", "chat")]
    [DataRow("computer_call", "chat")]
    [DataRow("web_search_call", "chat")]
    [DataRow("file_search_call", "chat")]
    public void ConvertRejectsUnsupportedResponsesHistoryItems(string type, string target)
    {
        var request = CreateRequest("/v1/responses",
            $$"""{"model":"m","input":[{"type":"{{type}}"}]}""");
        NotSupported(() => Convert(request, target));
    }

    [TestMethod]
    public void ConvertRejectsNativeFileIdsAndMultimodalAssistantContent()
    {
        var chat = CreateRequest("/v1/chat/completions",
            """{"model":"m","messages":[{"role":"user","content":[{"type":"text","text":"see"},{"type":"file","file":{"file_id":"abc"}}]}]}""");
        NotSupported(() => Convert(chat, "responses"));
        var responses = CreateRequest("/v1/responses",
            """{"model":"m","input":[{"type":"message","role":"assistant","content":[{"type":"input_image","image_url":"https://example.invalid/x.png"}]}]}""");
        NotSupported(() => Convert(responses, "chat"));
    }

    [TestMethod]
    [DataRow("previous_response_id", "\"r1\"")]
    [DataRow("metadata", "{}")]
    public void ConvertRejectsResponsesStatefulFieldsWithNormalizedFallback(string field, string value)
    {
        var request = CreateRequest("/v1/responses", $$"""{"model":"m","{{field}}":{{value}}}""");
        NotSupported(() => Convert(request, "chat"));
    }

    private static JsonObject Convert(AdapterRequest request, string target) =>
        (JsonObject)ConvertMethod().Invoke(null, [request, target])!;

    private static void NotSupported(Action action)
    {
        try { action(); }
        catch (TargetInvocationException invocation) { Assert.IsInstanceOfType<NotSupportedException>(invocation.InnerException); return; }
        Assert.Fail("Expected NotSupportedException.");
    }

    private static MethodInfo ConvertMethod()
    {
        var method = typeof(ForwardApiTerminal).GetMethod("ConvertProtocolRequest",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.IsNotNull(method, ConvertTimeoutDescription);
        return method;
    }

    private static AdapterRequest CreateRequest(string endpoint, string json)
    {
        using var document = JsonDocument.Parse(json);
        var body = document.RootElement.Clone();
        var adapter = new AdapterRequest
        {
            Model = body.TryGetProperty("model", out var model) ? model.GetString() ?? string.Empty : string.Empty,
            Endpoint = endpoint,
            OriginalBody = body,
            Stream = body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True,
            MaxTokens = body.TryGetProperty("max_completion_tokens", out var limit) && limit.TryGetInt32(out var value)
                ? value
                : body.TryGetProperty("max_output_tokens", out var output) && output.TryGetInt32(out var outputValue)
                    ? outputValue
                    : body.TryGetProperty("max_tokens", out var tokens) && tokens.TryGetInt32(out var tokensValue) ? tokensValue : null,
            Temperature = body.TryGetProperty("temperature", out var temperature) && temperature.TryGetDouble(out var temp) ? temp : null,
            Tools = body.TryGetProperty("tools", out var tools)
                ? tools.EnumerateArray().Select(item => (object)item.Clone()).ToArray() : []
        };
        if (body.TryGetProperty("reasoning_effort", out var effort) && effort.ValueKind == JsonValueKind.String)
            adapter.Extensions["reasoning_effort"] = effort.Clone();
        return adapter;
    }

    private const string ConvertTimeoutDescription = "Protocol request converter reflection access is required.";
}
