using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Plugins.ForwardAPI;

public sealed partial class ForwardApiTerminal
{
    /// <summary>构造无网络协议转换请求；不能等价表达的语义显式拒绝，避免静默丢失。</summary>
    private static JsonObject ConvertProtocolRequest(AdapterRequest request, string targetApi)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (targetApi is not ("responses" or "chat"))
            throw new NotSupportedException("The target API must be responses or chat.");
        var source = request.OriginalBody is { ValueKind: JsonValueKind.Object } body
            ? JsonNode.Parse(body.GetRawText())!.AsObject()
            : new JsonObject();
        var sourceApi = request.Endpoint.Trim().Trim('/').ToLowerInvariant() switch
        {
            "v1/chat/completions" => "chat",
            "v1/responses" => "responses",
            "v1/messages" => "messages",
            "v1/completions" => "completions",
            _ => throw new NotSupportedException("Unsupported source protocol for request conversion.")
        };
        if (sourceApi == targetApi) return (JsonObject)source.DeepClone();
        foreach (var option in request.Extensions)
            if (!source.ContainsKey(option.Key) && option.Value is not null)
                source[option.Key] = JsonSerializer.SerializeToNode(option.Value);
        ValidateProtocolOptions(source, sourceApi, targetApi);
        var messages = ReadProtocolMessages(source, sourceApi, request.Messages);
        var result = new JsonObject { ["model"] = request.Model, ["stream"] = request.Stream };
        result[targetApi == "responses" ? "input" : "messages"] = FormatProtocolMessages(messages, targetApi);
        // Responses otherwise defaults to server-side persistence, unlike a stateless Chat request.
        if (targetApi == "responses") result["store"] = false;
        // 转换后的 Chat 流必须请求末尾 usage，供宿主记录完整用量。
        if (targetApi == "chat" && request.Stream) result["stream_options"] = new JsonObject { ["include_usage"] = true };
        var maxTokens = ReadProtocolOption(source, "max_completion_tokens")
            ?? ReadProtocolOption(source, "max_output_tokens") ?? ReadProtocolOption(source, "max_tokens");
        var maxTokensKey = targetApi == "responses" ? "max_output_tokens" : "max_completion_tokens";
        if (maxTokens is not null) result[maxTokensKey] = maxTokens.DeepClone();
        else if (request.MaxTokens is { } limit) result[maxTokensKey] = limit;
        CopyProtocolOption(source, result, "temperature");
        if (!result.ContainsKey("temperature") && request.Temperature is { } temperature) result["temperature"] = temperature;
        foreach (var key in new[] { "top_p", "parallel_tool_calls", "user" }) CopyProtocolOption(source, result, key);
        if (!result.ContainsKey("parallel_tool_calls") && request.Extensions.TryGetValue("parallel_tool_calls", out var parallel))
            result["parallel_tool_calls"] = JsonSerializer.SerializeToNode(parallel);
        CopyProtocolStop(source, targetApi, result);
        CopyProtocolTools(source, request, sourceApi, targetApi, result);
        CopyProtocolFormat(source, targetApi, result);
        CopyProtocolReasoning(source, request, targetApi, result);
        return result;
    }

    private static void ValidateProtocolOptions(JsonObject source, string sourceApi, string target)
    {
        foreach (var key in new[] { "previous_response_id", "conversation", "prompt", "background", "store", "logprobs",
            "top_logprobs", "best_of", "n", "echo", "suffix", "stream_options", "truncation", "include" })
        {
            var value = ReadProtocolOption(source, key);
            if (value is null || (key == "prompt" && sourceApi == "completions")) continue;
            if (key == "n" && value is JsonValue count && count.TryGetValue<int>(out var n) && n == 1) continue;
            if (key is "background" or "store" or "logprobs" or "echo"
                && value is JsonValue boolean && boolean.TryGetValue<bool>(out var flag) && !flag) continue;
            if (key == "truncation" && ProtocolString(value) == "disabled") continue;
            if (key == "include" && value is JsonArray { Count: 0 }) continue;
            if (key == "stream_options" && value is JsonObject options
                && options.All(pair => pair.Key == "include_usage" && pair.Value is JsonValue usage
                    && usage.TryGetValue<bool>(out _))) continue;
            throw new NotSupportedException($"'{key}' cannot be preserved when converting {sourceApi} to {target}.");
        }
        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "model", "stream", "messages", "prompt", "input", "instructions", "system", "temperature", "top_p",
            "max_tokens", "max_completion_tokens", "max_output_tokens", "tools", "functions", "tool_choice", "function_call",
            "parallel_tool_calls", "stop", "stop_sequences", "response_format", "reasoning_effort", "reasoning", "text",
            "endpoint", "overrides", "models", "user", "store", "background", "logprobs", "n", "echo", "stream_options",
            "truncation", "include"
        };
        foreach (var pair in source)
            if (pair.Value is not null && !supported.Contains(pair.Key))
                throw new NotSupportedException($"Request field '{pair.Key}' cannot be preserved by this protocol conversion.");
        if (ReadProtocolOption(source, "instructions") is { } instructions && ProtocolString(instructions) is null)
            throw new JsonException("instructions must be a string.");
        if (ReadProtocolOption(source, "max_tokens") is not null && ReadProtocolOption(source, "max_completion_tokens") is not null)
            throw new NotSupportedException("Conflicting max_tokens and max_completion_tokens cannot be converted.");
        if (target == "responses" && (ReadProtocolOption(source, "stop") ?? ReadProtocolOption(source, "stop_sequences")) is { } stop
            && stop is not JsonArray { Count: 0 })
            throw new NotSupportedException("Responses has no equivalent for stop sequences.");
    }

    private static List<AdapterMessage> ReadProtocolMessages(JsonObject source, string api, IReadOnlyList<AdapterMessage> fallback)
    {
        var result = new List<AdapterMessage>();
        if (api == "responses")
        {
            if (ProtocolString(source["instructions"]) is { } instructions)
                result.Add(new AdapterMessage("developer", instructions));
            if (source["input"] is JsonValue value && value.TryGetValue<string>(out var text))
                result.Add(new AdapterMessage("user", text));
            else if (source["input"] is JsonArray input)
                foreach (var node in input) ReadResponsesItem(RequireProtocolObject(node, "Responses input item"), result);
            else if (source.ContainsKey("input") && source["input"] is not null)
                throw new JsonException("Responses input must be a string or array.");
            else result.AddRange(fallback.Where(message => message.Role != "system" || ProtocolString(source["instructions"]) is null));
        }
        else if (api == "chat" && source["messages"] is JsonArray chat)
        {
            foreach (var node in chat)
            {
                var item = RequireProtocolObject(node, "Chat message");
                RejectProtocolFields(item, "role", "content", "name", "tool_calls", "tool_call_id", "reasoning", "reasoning_content");
                var role = RequireProtocolString(item["role"], "message.role");
                var parts = ReadProtocolParts(item["content"], role);
                var calls = item["tool_calls"] is JsonArray toolCalls
                    ? toolCalls.Select(call => (object)RequireProtocolObject(call, "tool call").DeepClone()).ToArray() : null;
                if (item["tool_calls"] is not null && calls is null) throw new JsonException("tool_calls must be an array.");
                if (item["reasoning"] is not null || item["reasoning_content"] is not null)
                    throw new NotSupportedException("Per-message reasoning history cannot be converted between Chat and Responses.");
                result.Add(CreateProtocolMessage(role, parts, ProtocolString(item["name"]), calls, ProtocolString(item["tool_call_id"])));
            }
        }
        else if (api == "completions" && source["prompt"] is { } prompt)
        {
            if (prompt is JsonArray prompts)
            {
                if (prompts.Count != 1) throw new NotSupportedException("Batched legacy prompts cannot be represented as one conversation.");
                prompt = prompts[0] ?? throw new JsonException("prompt cannot be null.");
            }
            result.Add(new AdapterMessage("user", RequireProtocolString(prompt, "Legacy prompt (token IDs are unsupported)")));
        }
        else
        {
            // Anthropic parsing is already normalized by Contracts host; validate raw blocks before using it.
            if (api == "messages") ValidateAnthropicConversion(source);
            result.AddRange(fallback);
        }
        if (result.Count == 0) throw new JsonException("There are no messages to convert.");
        return result;
    }

    private static void ReadResponsesItem(JsonObject item, List<AdapterMessage> result)
    {
        var type = ProtocolString(item["type"]) ?? "message";
        switch (type)
        {
            case "message":
                RejectProtocolFields(item, "type", "role", "content", "status", "id");
                var role = RequireProtocolString(item["role"], "input.role");
                var message = CreateProtocolMessage(role, ReadProtocolParts(item["content"], role));
                if (role == "assistant") AddResponsesAssistantTurn(message, result);
                else result.Add(message);
                break;
            case "function_call":
                RejectProtocolFields(item, "type", "name", "arguments", "call_id", "id", "status");
                var id = RequireProtocolString(item["call_id"], "function_call.call_id");
                var call = new JsonObject
                {
                    ["id"] = id, ["type"] = "function", ["function"] = new JsonObject
                    {
                        ["name"] = RequireProtocolString(item["name"], "function_call.name"),
                        ["arguments"] = RequireProtocolString(item["arguments"], "function_call.arguments")
                    }
                };
                AddResponsesAssistantTurn(new AdapterMessage("assistant", string.Empty, ToolCalls: [call]), result);
                break;
            case "function_call_output":
                RejectProtocolFields(item, "type", "output", "call_id", "id", "status");
                result.Add(CreateProtocolMessage("tool", ReadProtocolParts(item["output"], "tool"),
                    callId: RequireProtocolString(item["call_id"], "function_call_output.call_id")));
                break;
            default:
                throw new NotSupportedException($"Responses input item '{type}' cannot be converted to Chat.");
        }
        if (ProtocolString(item["status"]) is { } status && status != "completed")
            throw new NotSupportedException("Incomplete Responses history cannot be converted.");
    }

    private static void AddResponsesAssistantTurn(AdapterMessage message, List<AdapterMessage> result)
    {
        // Responses 把同一轮助手文本和并行调用拆成多个 item；Chat 要求全部调用在一个助手消息中。
        // 工具结果或其他角色会结束这一轮，不能跨过它们合并后续调用。
        if (result.Count == 0 || result[^1].Role != "assistant")
        {
            result.Add(message);
            return;
        }
        var previous = result[^1];
        var parts = (previous.ContentParts ?? []).Concat(message.ContentParts ?? []).ToList();
        var calls = (previous.ToolCalls ?? []).Concat(message.ToolCalls ?? []).ToArray();
        result[^1] = CreateProtocolMessage("assistant", parts, calls: calls.Length > 0 ? calls : null);
    }

    private static AdapterMessage CreateProtocolMessage(string role, List<AdapterContentPart> parts,
        string? name = null, IReadOnlyList<object>? calls = null, string? callId = null)
        => new(role, string.Concat(parts.Where(part => part.Kind == AdapterContentPartKind.Text).Select(part => part.Text)),
            name, calls, callId) { ContentParts = parts.Count > 0 ? parts : null };

    private static List<AdapterContentPart> ReadProtocolParts(JsonNode? content, string role)
    {
        if (content is null) return [];
        if (ProtocolString(content) is { } text) return [new(AdapterContentPartKind.Text, Text: text)];
        if (content is not JsonArray array) throw new JsonException("Message content must be text or an array of content blocks.");
        var result = new List<AdapterContentPart>();
        foreach (var node in array)
        {
            var block = RequireProtocolObject(node, "content block");
            var type = RequireProtocolString(block["type"], "content.type");
            switch (type)
            {
                case "text":
                case "input_text":
                case "output_text":
                    RejectProtocolFields(block, "type", "text", "annotations");
                    if (block["annotations"] is JsonArray { Count: > 0 })
                        throw new NotSupportedException("Content annotations cannot be preserved across protocols.");
                    result.Add(new(AdapterContentPartKind.Text, Text: RequireProtocolString(block["text"], "content.text")));
                    break;
                case "image_url":
                case "input_image":
                    RejectProtocolFields(block, "type", "image_url", "detail", "file_id");
                    var image = block["image_url"] as JsonObject;
                    if (image is not null) RejectProtocolFields(image, "url", "detail");
                    RejectNativeFileId(block);
                    result.Add(new(AdapterContentPartKind.Image,
                        ImageUrl: RequireProtocolString(image?["url"] ?? block["image_url"], "image URL"),
                        Detail: ProtocolString(image?["detail"] ?? block["detail"])));
                    break;
                case "file":
                case "input_file":
                    RejectProtocolFields(block, "type", "file", "file_id", "file_data", "file_url", "filename", "detail");
                    var file = block["file"] as JsonObject ?? block;
                    if (file != block) RejectProtocolFields(file, "file_id", "file_data", "file_url", "filename", "detail");
                    RejectNativeFileId(file);
                    result.Add(new(AdapterContentPartKind.File,
                        FileName: ProtocolString(file["filename"]), FileData: ProtocolString(file["file_data"]),
                        FileUrl: ProtocolString(file["file_url"]), Detail: ProtocolString(file["detail"])));
                    break;
                default:
                    throw new NotSupportedException($"Content block '{type}' cannot be converted without losing semantics.");
            }
        }
        if (role is "assistant" or "system" or "developer" or "tool" && result.Any(part => part.Kind != AdapterContentPartKind.Text))
            throw new NotSupportedException($"Multimodal '{role}' content is not supported by both protocols.");
        return result;
    }

    private static JsonArray FormatProtocolMessages(IReadOnlyList<AdapterMessage> messages, string target)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            var role = message.Role.ToLowerInvariant();
            if (role is not ("user" or "assistant" or "system" or "developer" or "tool"))
                throw new NotSupportedException($"Message role '{message.Role}' cannot be converted.");
            if (message.Reasoning is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }
                || message.ReasoningContent is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                throw new NotSupportedException("Per-message reasoning history cannot be preserved across protocols.");
            foreach (var part in message.ContentParts ?? [])
            {
                if (!string.IsNullOrEmpty(part.FileId)) throw new NotSupportedException("Native file_id is scoped to its provider and cannot be converted.");
                if (role != "user" && part.Kind != AdapterContentPartKind.Text)
                    throw new NotSupportedException($"Multimodal '{role}' content cannot be converted.");
            }
            var calls = new JsonArray();
            foreach (var call in message.ToolCalls ?? []) calls.Add(ConvertProtocolCall(call));
            if (calls.Count > 0 && role != "assistant") throw new NotSupportedException("Only assistant messages may call tools.");
            if (target == "chat")
            {
                var item = new JsonObject { ["role"] = role, ["content"] = JsonSerializer.SerializeToNode(AdapterContentFormatter.ToChatContent(message)) };
                if (role == "tool") item["tool_call_id"] = RequireProtocolString(JsonValue.Create(message.ToolCallId), "tool result call ID");
                if (message.Name is not null) item["name"] = message.Name;
                if (calls.Count > 0) item["tool_calls"] = calls;
                result.Add(item);
                continue;
            }
            if (message.Name is not null) throw new NotSupportedException("Named Chat messages cannot be represented by Responses message items.");
            if (role == "tool")
            {
                result.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] =
                    RequireProtocolString(JsonValue.Create(message.ToolCallId), "tool result call ID"), ["output"] = message.Content });
                continue;
            }
            var hasContent = !string.IsNullOrEmpty(message.Content)
                || message.ContentParts?.Any(part => part.Kind != AdapterContentPartKind.Text
                    || !string.IsNullOrEmpty(part.Text)) == true;
            if (hasContent || calls.Count == 0)
                result.Add(new JsonObject { ["type"] = "message", ["role"] = role,
                    ["content"] = JsonSerializer.SerializeToNode(AdapterContentFormatter.ToResponsesContent(message, role == "assistant")) });
            foreach (var call in calls.Cast<JsonObject>())
            {
                var function = call["function"]!.AsObject();
                result.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call["id"]!.DeepClone(),
                    ["name"] = function["name"]!.DeepClone(), ["arguments"] = function["arguments"]!.DeepClone() });
            }
        }
        return result;
    }

    private static JsonObject ConvertProtocolCall(object value)
    {
        var raw = JsonSerializer.SerializeToNode(value)?.AsObject() ?? throw new JsonException("A tool call must be an object.");
        RejectProtocolFields(raw, "type", "id", "function");
        if (ProtocolString(raw["type"]) is { } type && type != "function")
            throw new NotSupportedException("Only function tool calls can be converted.");
        var function = RequireProtocolObject(raw["function"], "tool_call.function");
        RejectProtocolFields(function, "name", "arguments");
        return new JsonObject { ["id"] = RequireProtocolString(raw["id"], "tool call ID"), ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = RequireProtocolString(function["name"], "tool function name"),
                ["arguments"] = RequireProtocolString(function["arguments"], "tool function arguments") } };
    }

    private static void CopyProtocolTools(JsonObject source, AdapterRequest request, string sourceApi, string target, JsonObject result)
    {
        var tools = source["tools"] as JsonArray;
        if (source["tools"] is not null && tools is null) throw new JsonException("tools must be an array.");
        if (tools is null && source["functions"] is JsonArray functions)
            tools = new JsonArray(functions.Select(function => (JsonNode)new JsonObject { ["type"] = "function", ["function"] = function?.DeepClone() }).ToArray());
        tools ??= new JsonArray(request.Tools.Select(tool => JsonSerializer.SerializeToNode(tool)).ToArray());
        var converted = new JsonArray();
        foreach (var node in tools)
        {
            var raw = RequireProtocolObject(node, "tool");
            JsonObject function;
            if (sourceApi == "messages" && raw.ContainsKey("input_schema"))
            {
                RejectProtocolFields(raw, "name", "description", "input_schema", "type");
                if (raw["type"] is not null && ProtocolString(raw["type"]) != "custom")
                    throw new NotSupportedException("Anthropic built-in tools cannot be converted.");
                function = new JsonObject { ["name"] = raw["name"]?.DeepClone(), ["description"] = raw["description"]?.DeepClone(),
                    ["parameters"] = raw["input_schema"]?.DeepClone(), ["strict"] = false };
            }
            else
            {
                if (ProtocolString(raw["type"]) != "function") throw new NotSupportedException("Only function tool schemas can be converted.");
                if (raw["function"] is JsonObject chatFunction)
                {
                    RejectProtocolFields(raw, "type", "function");
                    function = (JsonObject)chatFunction.DeepClone();
                }
                else { function = (JsonObject)raw.DeepClone(); function.Remove("type"); }
            }
            RejectProtocolFields(function, "name", "description", "parameters", "strict");
            _ = RequireProtocolString(function["name"], "tool.name");
            if (!function.ContainsKey("strict")) function["strict"] = sourceApi == "responses";
            converted.Add(target == "responses" ? FlattenProtocolFunction(function)
                : new JsonObject { ["type"] = "function", ["function"] = function });
        }
        if (converted.Count > 0) result["tools"] = converted;
        var choice = source["tool_choice"] ?? source["function_call"];
        if (choice is null && request.Extensions.TryGetValue("tool_choice", out var value)) choice = JsonSerializer.SerializeToNode(value);
        if (choice is null) return;
        if (ProtocolString(choice) is { } mode)
        {
            if (mode is not ("auto" or "none" or "required")) throw new NotSupportedException("Unknown tool_choice mode.");
            result["tool_choice"] = mode;
            return;
        }
        var selected = RequireProtocolObject(choice, "tool_choice");
        var selectionType = ProtocolString(selected["type"]);
        if (sourceApi == "messages" && selectionType is "auto" or "any" or "none")
        {
            RejectProtocolFields(selected, "type", "disable_parallel_tool_use");
            result["tool_choice"] = selectionType == "any" ? "required" : selectionType;
        }
        else
        {
            RejectProtocolFields(selected, "type", "name", "function", "disable_parallel_tool_use");
            if (selectionType is not (null or "function" or "tool")) throw new NotSupportedException("Only named function tool_choice is supported.");
            var named = selected["function"] as JsonObject ?? selected;
            if (named != selected) RejectProtocolFields(named, "name");
            var name = RequireProtocolString(named["name"], "tool_choice.name");
            result["tool_choice"] = target == "responses" ? new JsonObject { ["type"] = "function", ["name"] = name }
                : new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name } };
        }
        if (selected["disable_parallel_tool_use"] is JsonValue disabled && disabled.TryGetValue<bool>(out var disable))
            result["parallel_tool_calls"] = !disable;
    }

    private static JsonObject FlattenProtocolFunction(JsonObject function)
    {
        var result = (JsonObject)function.DeepClone();
        result["type"] = "function";
        return result;
    }

    private static void CopyProtocolStop(JsonObject source, string target, JsonObject result)
    {
        var stop = source["stop"] ?? source["stop_sequences"];
        if (stop is null || stop is JsonArray { Count: 0 }) return;
        if (target == "responses") throw new NotSupportedException("Responses has no equivalent for stop sequences.");
        result["stop"] = stop.DeepClone();
    }

    private static void CopyProtocolFormat(JsonObject source, string target, JsonObject result)
    {
        var format = source["response_format"] as JsonObject;
        if (target == "chat" && source["text"] is JsonObject text)
        {
            RejectProtocolFields(text, "format");
            format = text["format"] as JsonObject;
        }
        if (format is null) return;
        var type = RequireProtocolString(format["type"], "response format type");
        if (type is "text" or "json_object")
        {
            RejectProtocolFields(format, "type");
            if (target == "responses") result["text"] = new JsonObject { ["format"] = format.DeepClone() };
            else result["response_format"] = format.DeepClone();
            return;
        }
        if (type != "json_schema") throw new NotSupportedException("Unknown structured output format.");
        var schema = target == "responses" ? RequireProtocolObject(format["json_schema"], "response_format.json_schema")
            : (JsonObject)format.DeepClone();
        if (target == "responses") RejectProtocolFields(format, "type", "json_schema");
        else schema.Remove("type");
        RejectProtocolFields(schema, "name", "description", "schema", "strict");
        if (schema["schema"] is not JsonObject) throw new JsonException("Structured output requires a JSON schema object.");
        // 两种协议的文本 json_schema 均默认非 strict；不能套用 Responses 工具 schema 的默认值。
        if (!schema.ContainsKey("strict")) schema["strict"] = false;
        if (target == "responses")
        {
            var flattened = (JsonObject)schema.DeepClone(); flattened["type"] = "json_schema";
            result["text"] = new JsonObject { ["format"] = flattened };
        }
        else result["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = schema };
    }

    private static void CopyProtocolReasoning(JsonObject source, AdapterRequest request, string target, JsonObject result)
    {
        var reasoning = source["reasoning"];
        var effort = source["reasoning_effort"];
        if (reasoning is null && request.Extensions.TryGetValue("reasoning", out var normalized)) reasoning = JsonSerializer.SerializeToNode(normalized);
        if (effort is null && request.Extensions.TryGetValue("reasoning_effort", out var normalizedEffort)) effort = JsonSerializer.SerializeToNode(normalizedEffort);
        if (reasoning is JsonObject configuration)
        {
            RejectProtocolFields(configuration, "effort");
            if (effort is not null && !JsonNode.DeepEquals(effort, configuration["effort"]))
                throw new NotSupportedException("Conflicting reasoning effort settings.");
            effort ??= configuration["effort"];
        }
        else if (reasoning is not null) throw new NotSupportedException("Only reasoning.effort can be mapped across protocols.");
        if (effort is null) return;
        var value = RequireProtocolString(effort, "reasoning effort");
        if (target == "responses") result["reasoning"] = new JsonObject { ["effort"] = value };
        else result["reasoning_effort"] = value;
    }

    private static void ValidateAnthropicConversion(JsonObject source)
    {
        foreach (var message in (source["messages"] as JsonArray) ?? [])
        {
            var item = RequireProtocolObject(message, "Anthropic message");
            RejectProtocolFields(item, "role", "content");
            if (item["content"] is not JsonArray content) continue;
            foreach (var node in content)
            {
                var block = RequireProtocolObject(node, "Anthropic content block");
                var type = ProtocolString(block["type"]);
                switch (type)
                {
                    case "text": RejectProtocolFields(block, "type", "text"); break;
                    case "image": RejectProtocolFields(block, "type", "source"); break;
                    case "document": RejectProtocolFields(block, "type", "source", "title"); break;
                    case "tool_use": RejectProtocolFields(block, "type", "id", "name", "input"); break;
                    case "tool_result":
                        RejectProtocolFields(block, "type", "tool_use_id", "content");
                        if (block["content"] is JsonArray output && output.Any(part => part is not JsonObject obj || ProtocolString(obj["type"]) != "text"))
                            throw new NotSupportedException("Multimodal tool results cannot be converted to both APIs.");
                        break;
                    default: throw new NotSupportedException($"Anthropic block '{type}' cannot be converted.");
                }
                if (block["source"] is JsonObject attachment)
                {
                    RejectProtocolFields(attachment, "type", "media_type", "data", "url");
                    if (ProtocolString(attachment["type"]) is not ("url" or "base64" or "text"))
                        throw new NotSupportedException("Native attachment IDs cannot be converted.");
                }
            }
        }
    }

    private static void RejectNativeFileId(JsonObject value)
    {
        if (value["file_id"] is not null) throw new NotSupportedException("Native file_id is scoped to its provider and cannot be converted.");
    }

    private static void RejectProtocolFields(JsonObject value, params string[] supported)
    {
        foreach (var pair in value)
            if (pair.Value is not null && !supported.Contains(pair.Key, StringComparer.Ordinal))
                throw new NotSupportedException($"Field '{pair.Key}' cannot be preserved by protocol conversion.");
    }

    private static JsonObject RequireProtocolObject(JsonNode? value, string name)
        => value as JsonObject ?? throw new JsonException($"{name} must be a JSON object.");

    private static string RequireProtocolString(JsonNode? value, string name)
        => ProtocolString(value) ?? throw new JsonException($"{name} must be a string.");

    private static string? ProtocolString(JsonNode? value)
        => value is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;

    private static JsonNode? ReadProtocolOption(JsonObject source, string key)
        => source.TryGetPropertyValue(key, out var value) ? value : null;

    private static void CopyProtocolOption(JsonObject source, JsonObject result, string key)
    {
        if (ReadProtocolOption(source, key) is { } value) result[key] = value.DeepClone();
    }
}
