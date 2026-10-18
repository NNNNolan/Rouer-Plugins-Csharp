using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Plugins.ModelRotator;

public sealed partial class ModelRotatorTerminal
{
    public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
    {
        if (context.Request.OriginalBody is not { ValueKind: JsonValueKind.Object } originalBody)
            return Fail(AdapterResponse.BadRequest("原始请求体不可用"), "invalid_body", "原始请求体不可用");

        RotatorDocument doc;
        try { doc = await ReadDocumentAsync(context.CancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Fail(AdapterResponse.ServerError("模型轮转配置读取失败"), "config_read_failed", exception.Message);
        }

        var route = doc.Virtuals.FirstOrDefault(item => item.Enabled
            && string.Equals(item.VirtualModel, context.Request.Model, StringComparison.OrdinalIgnoreCase));
        if (route is null)
            return Fail(new AdapterResponse { StatusCode = 404, Error = "虚拟模型未配置或已停用", ErrorType = "invalid_request_error" },
                "virtual_not_found", "虚拟模型未配置或已停用");
        if (!SupportedEndpoints.Contains(NormalizeEndpoint(context.Request.Endpoint), StringComparer.OrdinalIgnoreCase))
            return Fail(AdapterResponse.BadRequest("不支持该模型 API 端点"), "unsupported_endpoint", "不支持该模型 API 端点");

        JsonObject requestBody;
        try
        {
            requestBody = JsonNode.Parse(originalBody.GetRawText()) as JsonObject
                ?? throw new JsonException("request body must be an object");
            requestBody.Remove("endpoint");
            requestBody.Remove("overrides");
            requestBody.Remove("models");
            requestBody["model"] = string.Equals(route.VirtualModel, context.Request.Model, StringComparison.OrdinalIgnoreCase)
                ? SelectFirstTarget(doc, route) ?? route.Targets.FirstOrDefault()
                : route.Targets.FirstOrDefault();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Fail(AdapterResponse.BadRequest("原始请求体无法转换"), "invalid_body", exception.Message);
        }

        var settings = doc.Settings;
        // baseUrl 在读取与保存时都被强制限定为本机回环地址（防 SSRF），SanitizeBaseUrl 不抛异常。
        var baseUri = new Uri(SanitizeBaseUrl(settings.BaseUrl));
        var candidates = SelectCandidates(doc, route);
        if (candidates.Count == 0)
            return Fail(AdapterResponse.ServerError("没有可用的实际模型目标"), "no_healthy_target", "没有可用的实际模型目标");
        var totalBudget = TimeSpan.FromSeconds(Math.Clamp(settings.FirstByteBudgetSeconds, 10, 600));
        var budgetWatch = Stopwatch.StartNew();
        var endpoint = NormalizeEndpoint(context.Request.Endpoint);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HttpStatusCode? lastStatus = null;
        string? lastBody = null;
        var attemptDetails = new List<RequestAttemptDetail>();
        foreach (var model in candidates.Take(MaxAttempts))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!tried.Add(model)) continue;
            var remaining = totalBudget - budgetWatch.Elapsed;
            if (remaining <= TimeSpan.FromSeconds(5)) break;
            var firstWait = ComputeFirstWait(doc, route, model, remaining);
            var attemptStarted = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUri, NormalizeEndpoint(context.Request.Endpoint)))
            {
                Content = new StringContent(RewriteModel(requestBody, model), Encoding.UTF8, "application/json")
            };
            AddRelayHeaders(request, context.Request, settings.ApiKey, endpoint);
            HttpResponseMessage response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            timeout.CancelAfter(firstWait);
            try
            {
                response = await context.HttpClient.SendAsync(request, useProxyPool: false,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                await RecordFailureAsync(route, model, isTimeout: true, context.CancellationToken, attemptStarted.Elapsed.TotalMilliseconds);
                attemptDetails.Add(new(null, "direct", null, "timeout", true, "等待上游首字节超时", (int)attemptStarted.ElapsedMilliseconds));
                continue;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                await RecordFailureAsync(route, model, isTimeout: exception is TaskCanceledException, context.CancellationToken, attemptStarted.Elapsed.TotalMilliseconds);
                attemptDetails.Add(new(null, "direct", null, "transport", true, SafeError(exception.Message), (int)attemptStarted.ElapsedMilliseconds));
                continue;
            }

            var status = (int)response.StatusCode;
            lastStatus = response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            if (response.IsSuccessStatusCode)
            {
                var isSse = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
                var contentLength = response.Content.Headers.ContentLength;
                if (context.Request.Stream || isSse
                    || contentLength is > MaxBufferedResponseBytes
                    || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    await RecordSuccessAsync(route, model, attemptStarted.Elapsed.TotalMilliseconds, null, context.CancellationToken);
                    // 流式中转没有可读的完成体，但要给宿主一条明确的成功尝试记录，
                    // 否则请求详情只剩下一个没有状态码的空行。
                    attemptDetails.Add(new(null, "direct", status, "success", false, null, (int)attemptStarted.ElapsedMilliseconds));
                    return new(new AdapterResponse
                    {
                        StatusCode = status,
                        IsStreaming = context.Request.Stream || isSse,
                        IsRawPassthrough = true,
                        ContentType = contentType,
                        RawStream = TapUsageAsync(ReadRawStreamAsync(response, TimeSpan.FromSeconds(settings.StreamIdleSeconds), context.CancellationToken), model, context.CancellationToken),
                        Lifetime = new ResponseLifetime(response)
                    }, Decision(PluginFailureKind.None, PluginRetryAction.None, "rotator_upstream_stream", statusCode: status), attemptDetails);
                }

                try
                {
                    var bytes = await ReadBoundedAsync(response.Content, MaxBufferedResponseBytes, context.CancellationToken);
                    var usage = TryReadUsage(bytes);
                    await RecordSuccessAsync(route, model, attemptStarted.Elapsed.TotalMilliseconds, usage, context.CancellationToken);
                    attemptDetails.Add(new(null, "direct", status, "success", false, null, (int)attemptStarted.ElapsedMilliseconds));
                    response.Dispose();
                    return new(new AdapterResponse
                    {
                        StatusCode = status,
                        IsRawPassthrough = true,
                        RawContent = bytes,
                        ContentType = contentType,
                        Usage = usage
                    }, Decision(PluginFailureKind.None, PluginRetryAction.None, "rotator_upstream_success", statusCode: status), attemptDetails);
                }
                catch (PayloadTooLargeException overflow)
                {
                    // 600KB 只是统计安全阈值，不是上限：把已读前缀与剩余上游流拼起来继续原样透传，
                    // 不丢任何字节（ReadBoundedAsync 中断时已消费的字节随异常返回）。
                    return new(new AdapterResponse
                    {
                        StatusCode = status,
                        IsRawPassthrough = true,
                        ContentType = contentType,
                        RawStream = TapUsageAsync(ReadRawOverflowAsync(overflow.Prefix, response, TimeSpan.FromSeconds(settings.StreamIdleSeconds), context.CancellationToken), model, context.CancellationToken),
                        Lifetime = new ResponseLifetime(response)
                    }, Decision(PluginFailureKind.None, PluginRetryAction.None, "rotator_large_json_raw", statusCode: status), attemptDetails);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    response.Dispose();
                    return Fail(AdapterResponse.ServerError("读取上游响应失败"), "response_read_failed", SafeError(exception.Message));
                }
            }

            string? rawText;
            byte[]? rawBytes;
            try
            {
                rawBytes = await ReadBoundedAsync(response.Content, 64 * 1024, context.CancellationToken);
                rawText = Encoding.UTF8.GetString(rawBytes);
            }
            catch
            {
                rawBytes = null;
                rawText = null;
            }
            response.Dispose();
            lastBody = rawText;
            attemptDetails.Add(new(null, "direct", status, "failed", status >= 500, DescribeStatus(status), (int)attemptStarted.ElapsedMilliseconds));

            // 上游其实已响应，只是被客户端的 max_tokens 截断（finish_reason=length）：
            // 这是客户端预算问题而不是模型故障；照常计数会把健康模型逐个禁掉，所以不计数、不轮转（与 JS 对齐）。
            if (AnsweredButEmptyRegex.IsMatch(lastBody ?? ""))
                return new(RawError(status, rawBytes, contentType), Decision(PluginFailureKind.Upstream, PluginRetryAction.None, "client_truncated", reason: "已响应但正文被截断（finish_reason=length）：不计数、不轮转", statusCode: status), attemptDetails);
            // 目标写成裸模型名时宿主无法路由（"model must use the platform/model format"）：
            // 换哪个目标都一样，这是配置错误，照常计数会把所有目标逐个禁掉，所以不计数、不轮转。
            if (UnroutableModelRegex.IsMatch(lastBody ?? ""))
                return new(RawError(status, rawBytes, contentType),
                    Decision(PluginFailureKind.Upstream, PluginRetryAction.None, "target_model_unroutable",
                        reason: $"实际模型「{model}」缺少 platform/ 前缀，宿主无法路由；请在路由编辑器里改成 platform/model 形式",
                        statusCode: status),
                    attemptDetails);
            if (status is 400 or 422)
                return new(RawError(status, rawBytes, contentType), Decision(PluginFailureKind.Upstream, PluginRetryAction.None, "client_input_error", reason: DescribeStatus(status), statusCode: status), attemptDetails);
            // 只有「宿主自己的回环 Key 被拒」才是配置级问题：AuthMiddleware 对 /v1 固定回
            // 401 + {"error":"invalid api key"}，所有目标共用同一把回环 Key，换模型毫无意义，
            // 必须立刻收手——照常计数只会把整条路由的目标逐个禁光。
            if (IsLoopbackAuthFailure(status, lastBody))
                return new(RawError(status, rawBytes, contentType), Decision(PluginFailureKind.InvalidCredential, PluginRetryAction.None, "loopback_auth_error", reason: "回环 API Key 被拒绝；未轮转任何模型", statusCode: status), attemptDetails);
            // 其余 401/403 都来自上游、只与「这个目标 + 当前出口」相关：宿主对 /v1 的鉴权失败
            // 一律回 401 且从不回 403。典型信号是 opencode 免费层按数据中心 IP 段回
            // 403 FreeTierError——换成另一个平台的目标就可能成功，所以必须计数并继续轮转；
            // 否则整条路由会一直卡在被拒的目标上（实测：等满 21 秒首字后直接放弃，一个模型都不换）。
            if (status is 401 or 403)
                await TryLogAsync("rotation.target_rejected",
                    $"实际模型 {model} 被上游拒绝（HTTP {status}）："
                    + (IsUpstreamExitRejection(lastBody) ? "该拒绝与出口 IP 相关（如 FreeTierError），" : "")
                    + $"继续轮转下一个目标：{SafeError(lastBody ?? "")}",
                    context.TraceId, context.CancellationToken);
            await RecordFailureAsync(route, model, isTimeout: false, context.CancellationToken);
        }

        var message = lastBody is null ? "所有实际模型均不可用或等待首字节超时" : $"所有实际模型均失败：{SafeError(lastBody)}";
        var finalStatus = lastStatus is null ? 504 : (int)lastStatus.Value;
        var responseFinal = new AdapterResponse { StatusCode = finalStatus, Error = message, ErrorType = "server_error" };
        // 日志只记元信息：上游错误正文可能包含请求派生数据，只回传给客户端，不进宿主日志。
        var logMessage = $"轮转全部失败：尝试 {tried.Count} 个目标，末次状态 {(int?)lastStatus ?? 0}，总耗时 {(int)budgetWatch.ElapsedMilliseconds}ms";
        await TryLogAsync("rotation.exhausted", logMessage, context.TraceId, context.CancellationToken);
        return new(responseFinal,
            Decision(lastStatus is null ? PluginFailureKind.Transport : PluginFailureKind.Upstream,
                PluginRetryAction.None, "rotation_exhausted", reason: message,
                statusCode: lastStatus is null ? null : finalStatus), attemptDetails);
    }

    private static Uri BuildUri(Uri baseUri, string endpoint)
    {
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var suffix = endpoint.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) && basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? endpoint[3..] : endpoint;
        var builder = new UriBuilder(baseUri) { Path = basePath + suffix, Query = "", Fragment = "" };
        return builder.Uri;
    }

    private static string RewriteModel(JsonObject body, string model)
    {
        var copy = body.DeepClone().AsObject();
        copy["model"] = model;
        return copy.ToJsonString(JsonOptions);
    }

    private static readonly string[] SkipForwardHeaders =
    [
        // 鉴权/归属类：换成我们自己的回环 Key 或干脆不加。
        "authorization", "x-api-key", "content-type", "host", "content-length", "x-forwarded-",
        // anthropic-version 单独按 /v1/messages 语义删除后重加，避免与客户端值重复。
        "anthropic-version",
        // 逐跳头：经 System.Net.Http 转发语义不同，不允许透传。
        "connection", "keep-alive", "te", "trailer", "transfer-encoding", "upgrade",
        "proxy-connection", "proxy-authenticate", "proxy-authorization"
    ];

    private static void AddRelayHeaders(HttpRequestMessage message, AdapterRequest request, string apiKey, string endpoint)
    {
        foreach (var header in request.RequestHeaders)
        {
            if (SkipForwardHeaders.Any(skip => header.Key.StartsWith(skip, StringComparison.OrdinalIgnoreCase))) continue;
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (endpoint.Equals("/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Remove("x-api-key");
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                message.Headers.Remove("anthropic-version");
                message.Headers.TryAddWithoutValidation("anthropic-version", request.RequestHeaders.GetValueOrDefault("anthropic-version") ?? "2023-06-01");
            }
            else message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>
    /// 边转发边嗅探 usage：流式响应要到最后一两个分块才带用量，而成功状态在
    /// 响应开始时就已记录（此时还拿不到 token），所以这里在流结束时补记。
    /// </summary>
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> TapUsageAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> source,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tap = new StreamUsageTap();
        try
        {
            await foreach (var chunk in source.WithCancellation(cancellationToken))
            {
                tap.Feed(chunk.Span);
                yield return chunk;
            }
        }
        finally
        {
            if (tap.Usage is { } usage)
            {
                try
                {
                    // 客户端中途断开时也用不受取消影响的令牌记账：token 已经真实消耗了。
                    await RecordUsageAsync(model, usage, CancellationToken.None);
                }
                catch
                {
                    // 统计是旁路，任何失败都不能影响已经开始的响应。
                }
            }
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawStreamAsync(
        HttpResponseMessage response,
        TimeSpan idleTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 与 JS streamIdleSeconds 一致：流式透传时相邻数据块之间超过空闲阈值即中断，
        // 防止卡死/半断开的上游连接把一次调用挂住（外层响应已开始，无法再轮转）。
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                if (idleTimeout > TimeSpan.Zero) idle.CancelAfter(idleTimeout);
                int read;
                try { read = await stream.ReadAsync(buffer, idle.Token); }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"stream idle timeout after {idleTimeout.TotalSeconds:0}s");
                }
                if (read == 0) yield break;
                yield return buffer.AsMemory(0, read).ToArray();
            }
        }
        finally { response.Dispose(); }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawOverflowAsync(
        byte[] prefix,
        HttpResponseMessage response,
        TimeSpan idleTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return prefix;
        await foreach (var chunk in ReadRawStreamAsync(response, idleTimeout, cancellationToken)) yield return chunk;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return memory.ToArray();
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (memory.Length > maxBytes) throw new PayloadTooLargeException(memory.ToArray());
        }
    }

    private static AdapterResponse RawError(int status, byte[]? content, string contentType) => new()
    {
        StatusCode = status,
        IsRawPassthrough = true,
        RawContent = content ?? Encoding.UTF8.GetBytes("{\"error\":\"upstream error\"}"),
        ContentType = contentType
    };

    private static Usage? TryReadUsage(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
            var prompt = ReadCount(usage, "prompt_tokens", "input_tokens");
            var completion = ReadCount(usage, "completion_tokens", "output_tokens");
            if (prompt is null && completion is null) return null;
            return new Usage((int)Math.Min(int.MaxValue, prompt ?? 0), (int)Math.Min(int.MaxValue, completion ?? 0),
                (int)Math.Min(int.MaxValue, (prompt ?? 0) + (completion ?? 0)));
        }
        catch (JsonException) { return null; }
    }

    private static long? ReadCount(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
            if (parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) && count >= 0) return count;
        return null;
    }

    /// <summary>
    /// 是否「宿主自己的回环鉴权失败」：宿主 AuthMiddleware 对 /v1 只回 401，
    /// 且正文固定是 {"error":"invalid api key"}（Results.Json 的默认小写命名）。
    /// 必须按「根对象 + error 字符串属性」做 JSON 精确匹配，不能用子串模糊匹配——
    /// 上游自己回 401 时正文常含 "Invalid API key provided" 之类的短语，模糊匹配会把
    /// 上游拒绝误判成回环 Key 配错，一个模型都不换（对抗验证给出的最小反例）。
    /// 正文一个字节都读不到时（含超 64KiB 截断）按配置级问题保守收手；
    /// 能读到但不是该精确结构（空正文、外层包装、解析失败）一律视为上游拒绝，继续轮转。
    /// </summary>
    internal static bool IsLoopbackAuthFailure(int status, string? body)
    {
        if (status != 401) return false;
        if (body is null) return true;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && string.Equals(error.GetString(), "invalid api key", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 上游按出口 IP 拒绝业务的文字信号（opencode 免费层 403 FreeTierError 等），仅用于把日志说清楚。
    /// </summary>
    internal static bool IsUpstreamExitRejection(string? body)
        => body is not null
           && (body.Contains("FreeTierError", StringComparison.OrdinalIgnoreCase)
               || body.Contains("free tier", StringComparison.OrdinalIgnoreCase)
               || body.Contains("not available in your region", StringComparison.OrdinalIgnoreCase)
               || body.Contains("blocked", StringComparison.OrdinalIgnoreCase));

    private static string DescribeStatus(int status) => status switch
    {
        404 => "上游未找到模型或端点",
        429 => "上游限流",
        >= 500 => "上游服务错误",
        _ => $"上游返回 HTTP {status}"
    };

    private static string SafeError(string value) => value.Length <= 500 ? value : value[..500];
    private sealed class PayloadTooLargeException(byte[] prefix) : Exception
    {
        /// <summary>超限时已经从流里读出的全部字节，透传时必须原样补回到流的最前面。</summary>
        public byte[] Prefix { get; } = prefix;
    }
    private sealed class ResponseLifetime(HttpResponseMessage response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { response.Dispose(); return ValueTask.CompletedTask; }
    }
}
