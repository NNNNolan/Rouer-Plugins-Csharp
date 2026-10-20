using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Router.Contracts.Domain;

namespace Plugins.ForwardAPI;

public sealed partial class ForwardApiTerminal
{
    private static string? ProtocolString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static Usage? ProtocolUsage(JsonElement value)
    {
        if (!value.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        static int Number(JsonElement source, string name, string fallback)
            => source.TryGetProperty(name, out var n) && n.TryGetInt32(out var count) ? count
                : source.TryGetProperty(fallback, out n) && n.TryGetInt32(out count) ? count : 0;
        var input = Number(usage, "input_tokens", "prompt_tokens");
        var output = Number(usage, "output_tokens", "completion_tokens");
        var total = Number(usage, "total_tokens", "total_tokens");
        return new Usage(input, output, total > 0 ? total : input + output);
    }

    private static AdapterCompletion ParseProtocolCompletion(JsonElement root, string api, string model)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("上游响应必须为 JSON 对象");
        if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new JsonException(ProtocolString(error, "message") ?? "上游返回错误");
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new List<AdapterToolCall>();
        string finish;
        if (api == "chat")
        {
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                throw new JsonException("转换仅支持单个 Chat Completions choice");
            var choice = choices[0];
            var message = choice.GetProperty("message");
            AppendProtocolText(message, text);
            reasoning.Append(ProtocolString(message, "reasoning_content") ?? ProtocolString(message, "reasoning"));
            if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array)
                foreach (var tool in tools.EnumerateArray())
                {
                    if (ProtocolString(tool, "type") is { } type && type != "function")
                        throw new JsonException("不能转换非 function 工具调用");
                    var function = tool.GetProperty("function");
                    calls.Add(new AdapterToolCall(calls.Count, ProtocolString(tool, "id"),
                        ProtocolString(function, "name"), ProtocolString(function, "arguments")));
                }
            if (message.TryGetProperty("function_call", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                calls.Add(new AdapterToolCall(calls.Count, ProtocolString(legacy, "id") ?? "call_legacy",
                    ProtocolString(legacy, "name"), ProtocolString(legacy, "arguments")));
            finish = ProtocolString(choice, "finish_reason") ?? throw new JsonException("上游未返回 finish_reason");
        }
        else
        {
            var status = ProtocolString(root, "status");
            if (status is not ("completed" or "incomplete"))
                throw new JsonException("上游 Responses 未完成：" + (status ?? "缺少 status"));
            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                throw new JsonException("上游 Responses 缺少 output");
            foreach (var item in output.EnumerateArray())
            {
                switch (ProtocolString(item, "type"))
                {
                    case "message":
                        AppendProtocolText(item, text);
                        break;
                    case "function_call":
                        calls.Add(new AdapterToolCall(calls.Count, ProtocolString(item, "call_id") ?? ProtocolString(item, "id"),
                            ProtocolString(item, "name"), ProtocolString(item, "arguments")));
                        break;
                    case "reasoning":
                        if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
                            foreach (var part in summary.EnumerateArray()) reasoning.Append(ProtocolString(part, "text"));
                        break;
                    default:
                        throw new JsonException("上游包含无法转换的 Responses 输出类型");
                }
            }
            var incompleteReason = root.TryGetProperty("incomplete_details", out var details)
                ? ProtocolString(details, "reason") : null;
            finish = status == "incomplete" ? incompleteReason == "content_filter" ? "content_filter" : "length" : "stop";
        }
        if (calls.Count > 0 && finish == "stop") finish = "tool_calls";
        return new AdapterCompletion(model, text.ToString(), finish, ProtocolUsage(root), calls,
            reasoning.Length > 0 ? reasoning.ToString() : null);
    }

    private static void AppendProtocolText(JsonElement message, StringBuilder text)
    {
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                {
                    switch (ProtocolString(part, "type"))
                    {
                        case "text" or "output_text": text.Append(ProtocolString(part, "text")); break;
                        case "refusal": text.Append(ProtocolString(part, "refusal")); break;
                        default: throw new JsonException("上游包含无法转换的内容块");
                    }
                }
        }
        text.Append(ProtocolString(message, "refusal"));
        if (message.TryGetProperty("audio", out var audio) && audio.ValueKind != JsonValueKind.Null)
            throw new JsonException("跨 API 转换不支持音频输出，请选择原协议透传");
    }

    private sealed class ProtocolResponseLifetime(HttpResponseMessage response, ProxyResponseLifetime? proxy) : IAsyncDisposable
    {
        private HttpResponseMessage? _response = response;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _response, null)?.Dispose();
            proxy?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // StreamReader handles partial UTF-8 codepoints and arbitrary network boundaries; SSE frames may contain multiple data lines.
    private static async IAsyncEnumerable<string> ReadProtocolEventsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                yield return data.ToString();
                data.Clear();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (data.Length > 0) data.Append('\n');
            data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
        }
        if (data.Length > 0) yield return data.ToString();
    }

    private sealed class ProtocolStreamState(string api, string model)
    {
        // Map Responses output items to stable downstream tool indexes. Tool calls are indexed by
        // arrival order only, so reasoning/message output items never consume a tool slot.
        private readonly Dictionary<string, int> _callIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _toolIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _outputIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _itemIndexes = new(StringComparer.Ordinal);
        private readonly HashSet<int> _argumentsSeen = [];
        private readonly HashSet<int> _headersSeen = [];
        private readonly HashSet<int> _toolDone = [];
        private bool _textSeen;
        private bool _reasoningSeen;
        public bool Completed { get; private set; }
        private bool _finishSeen;
        private string _finish = "stop";
        private Usage? _usage;
        private int Index(JsonElement value)
        {
            if (value.TryGetProperty("item_id", out var itemId) && ProtocolString(itemId, "item_id") is { } itemKey
                && _itemIndexes.TryGetValue(itemKey, out var mapped)) return mapped;
            if (value.TryGetProperty("output_index", out var outputIndex)
                && _outputIndexes.TryGetValue(outputIndex.GetRawText(), out var outputMapped)) return outputMapped;
            int result;
            if (value.TryGetProperty("item_id", out itemId) && itemId.ValueKind == JsonValueKind.String)
            {
                _itemIndexes.TryAdd(itemId.GetString()!, result = _toolIndexes.Count);
                _toolIndexes[itemId.GetString()!] = result;
                return result;
            }
            if (value.TryGetProperty("output_index", out outputIndex))
            {
                var key = outputIndex.GetRawText();
                _outputIndexes.TryAdd(key, result = _toolIndexes.Count);
                _toolIndexes.TryAdd(key, result);
                return result;
            }
            _toolIndexes.TryAdd(string.Empty, result = _toolIndexes.Count);
            return result;
        }
        private int FinalIndex(AdapterToolCall call)
        {
            if (call.Id is { } id && _callIndexes.TryGetValue(id, out var index)) return index;
            return call.Index;
        }
        public List<StreamChunk> Feed(string data)
        {
            if (data == "[DONE]")
            {
                if (api != "chat" || !_finishSeen) throw new JsonException("上游流在完成事件之前终止");
                Completed = true;
                return [new StreamChunk(null, _finish, _usage)];
            }
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("上游流事件不是 JSON 对象");
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new JsonException(ProtocolString(error, "message") ?? "上游流返回错误");
            return api == "chat" ? FeedChat(root) : FeedResponses(root);
        }
        private List<StreamChunk> FeedChat(JsonElement root)
        {
            _usage = ProtocolUsage(root) ?? _usage;
            var chunks = new List<StreamChunk>();
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return chunks;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("index", out var choiceIndex) && choiceIndex.GetInt32() != 0)
                    throw new JsonException("跨 API 流仅支持单个 choice");
                if (ProtocolString(choice, "finish_reason") is { } finish)
                {
                    _finishSeen = true;
                    _finish = finish;
                }
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                if (ProtocolString(delta, "content") is { } text) chunks.Add(new StreamChunk(text));
                if (ProtocolString(delta, "refusal") is { } refusal) chunks.Add(new StreamChunk(refusal));
                if ((ProtocolString(delta, "reasoning_content") ?? ProtocolString(delta, "reasoning")) is { } thinking)
                    chunks.Add(new StreamChunk(null, ReasoningDelta: thinking));
                if (ProtocolString(delta, "role") is { } role) chunks.Add(new StreamChunk(null, Role: role));
                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = call.GetProperty("index").GetInt32();
                        var function = call.TryGetProperty("function", out var fn) ? fn : default;
                        chunks.Add(new StreamChunk(null, ToolCalls: [new ToolCallDelta(index,
                            ProtocolString(call, "id"), ProtocolString(call, "type") ?? "function",
                            ProtocolString(function, "name"), ProtocolString(function, "arguments"))]));
                    }
                if (delta.TryGetProperty("function_call", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                    chunks.Add(new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, "call_legacy", "function",
                        ProtocolString(legacy, "name"), ProtocolString(legacy, "arguments"))]));
            }
            return chunks;
        }
        private List<StreamChunk> FeedResponses(JsonElement root)
        {
            var type = ProtocolString(root, "type");
            switch (type)
            {
                case "response.created": return [new StreamChunk(null, Role: "assistant")];
                case "response.output_text.delta":
                    _textSeen = true;
                    return [new StreamChunk(ProtocolString(root, "delta"))];
                case "response.refusal.delta":
                    _textSeen = true;
                    return [new StreamChunk(ProtocolString(root, "delta"))];
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    _reasoningSeen = true;
                    return [new StreamChunk(null, ReasoningDelta: ProtocolString(root, "delta"))];
                case "response.output_item.added":
                case "response.output_item.done":
                    var item = root.GetProperty("item");
                    if (ProtocolString(item, "type") != "function_call") return [];
                    var index = Index(root);
                    if ((ProtocolString(item, "call_id") ?? ProtocolString(item, "id")) is { } callId)
                        _callIndexes[callId] = index;
                    if (root.TryGetProperty("item_id", out var rootItemId) && ProtocolString(rootItemId, "item_id") is { } rootItemKey)
                        _itemIndexes[rootItemKey] = index;
                    if (root.TryGetProperty("output_index", out var rootOutputIndex))
                        _outputIndexes[rootOutputIndex.GetRawText()] = index;
                    var result = new List<StreamChunk>();
                    if (_headersSeen.Add(index))
                        result.Add(new StreamChunk(null, ToolCalls: [new ToolCallDelta(index,
                            ProtocolString(item, "call_id") ?? ProtocolString(item, "id"), "function", ProtocolString(item, "name"))]));
                    if (type == "response.output_item.done" && _toolDone.Add(index) && !_argumentsSeen.Contains(index))
                        result.Add(new StreamChunk(null, ToolCalls: [new ToolCallDelta(index, Arguments: ProtocolString(item, "arguments"))]));
                    return result;
                case "response.function_call_arguments.delta":
                    var deltaIndex = Index(root);
                    _argumentsSeen.Add(deltaIndex);
                    return [new StreamChunk(null, ToolCalls: [new ToolCallDelta(deltaIndex, Arguments: ProtocolString(root, "delta"))])];
                case "response.function_call_arguments.done":
                    var doneIndex = Index(root);
                    if (_argumentsSeen.Contains(doneIndex) || !_toolDone.Add(doneIndex)) return [];
                    return [new StreamChunk(null, ToolCalls: [new ToolCallDelta(doneIndex, Arguments: ProtocolString(root, "arguments"))])];
                case "response.failed":
                case "error":
                    throw new JsonException("上游 Responses 流失败");
                case "response.completed":
                case "response.incomplete":
                    var completion = ParseProtocolCompletion(root.GetProperty("response"), "responses", model);
                    var final = new List<StreamChunk>();
                    if (!_textSeen && completion.Content is { Length: > 0 }) final.Add(new StreamChunk(completion.Content));
                    if (!_reasoningSeen && completion.ReasoningContent is { Length: > 0 })
                        final.Add(new StreamChunk(null, ReasoningDelta: completion.ReasoningContent));
                    foreach (var call in completion.ToolCalls ?? [])
                    {
                        // Final snapshots are a fallback, not a second copy of already streamed arguments.
                        var finalIndex = FinalIndex(call);
                        if (_headersSeen.Add(finalIndex))
                            final.Add(new StreamChunk(null, ToolCalls: [new ToolCallDelta(finalIndex, call.Id, "function", call.Name)]));
                        if (!_argumentsSeen.Contains(finalIndex) && _toolDone.Add(finalIndex))
                            final.Add(new StreamChunk(null, ToolCalls: [new ToolCallDelta(finalIndex, Arguments: call.Arguments)]));
                    }
                    final.Add(new StreamChunk(null, completion.FinishReason, completion.Usage));
                    Completed = true;
                    return final;
                default: return [];
            }
        }
    }

    private static async IAsyncEnumerable<StreamChunk> ReadConvertedStreamAsync(
        HttpResponseMessage response, string api, string model, ProtocolResponseLifetime lifetime,
        ForwardApiSettings settings, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var state = new ProtocolStreamState(api, model);
            await foreach (var data in ReadProtocolEventsAsync(stream, cancellationToken))
            {
                List<StreamChunk> chunks;
                try { chunks = state.Feed(data); }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    chunks = [new StreamChunk(null, Error: SanitizeMessage(exception.Message, settings), ErrorType: "upstream_error")];
                }
                foreach (var chunk in chunks) yield return chunk;
                if (state.Completed || chunks.Any(chunk => chunk.Error is not null)) yield break;
            }
            if (!state.Completed)
                yield return new StreamChunk(null, Error: "上游流在完成事件之前断开", ErrorType: "upstream_error");
        }
        finally
        {
            await lifetime.DisposeAsync();
        }
    }
}
