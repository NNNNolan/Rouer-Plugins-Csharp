using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.ForwardAPI;

public sealed partial class ForwardApiTerminal
{
    private const string DailyCheckInTaskName = "forwardapi-daily-checkin";

    /// <summary>
    /// 将已通过账号模型/端点策略的请求转发到配置的上游，并将响应和尝试结果交回宿主。
    /// 支持原始非流式响应和不改写内容的 SSE/字节流透传。
    /// </summary>
    /// <param name="context">本次账号尝试的请求、账号凭据、HTTP 客户端、追踪信息和取消令牌。</param>
    /// <returns>上游响应以及供宿主决定成功、重试、冷却或停用账号的结果。</returns>
    public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
    {
        var result = await InvokeCoreAsync(context);
        var decision = result.Response.IsSuccess ? new PluginAttemptDecision() : FailureDecision(result.Attempt);
        return result with { Attempt = decision.ToResult(result.Attempt.StatusCode, result.Attempt.Reason) };
    }

    private static PluginAttemptDecision FailureDecision(PluginAttemptResult attempt)
    {
        var disable = attempt.IndicatesInvalidCredential;
        var cooldown = !disable && (attempt.IsTransportFailure || attempt.StatusCode is 429 or >= 500);
        return new PluginAttemptDecision
        {
            FailureKind = attempt.IsTransportFailure ? PluginFailureKind.Transport
                : disable ? PluginFailureKind.InvalidCredential : PluginFailureKind.Upstream,
            Retry = disable || attempt.IsTransportFailure || attempt.StatusCode is 408 or 425 or 429 or >= 500
                ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
            AccountAction = disable ? PluginAccountAction.Disable : cooldown ? PluginAccountAction.Cooldown : PluginAccountAction.None,
            AccountCooldownUntil = cooldown ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
            ProxyAction = attempt.IsTransportFailure || attempt.StatusCode == 407 ? PluginProxyAction.Cooldown : PluginProxyAction.None,
            ReasonCode = "forwardapi.upstream"
        };
    }

    private async Task<PluginInvocationResult> InvokeCoreAsync(PluginAttemptContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!TryReadSettings(context.Account, out var settings))
            return RequestFailure(context, AdapterResponse.BadRequest("ForwardAPI account settings are missing"), "账号配置缺失");
        if (!IsAccountEligible(context.Account, context.Request))
            return RequestFailure(context, AdapterResponse.BadRequest("account does not support the requested model or endpoint"), "账号未启用此模型或端点");
        if (context.Request.OriginalBody is not { ValueKind: JsonValueKind.Object } originalBody)
            return RequestFailure(context, AdapterResponse.BadRequest("original request body is unavailable"), "请求体不是有效 JSON 对象");

        JsonObject body;
        try
        {
            body = JsonNode.Parse(originalBody.GetRawText()) as JsonObject
                ?? throw new JsonException("request body must be an object");
        }
        catch (JsonException exception)
        {
            return RequestFailure(context, AdapterResponse.BadRequest(exception.Message), "无法读取原始请求体");
        }

        body.Remove("endpoint");
        body.Remove("overrides");
        body.Remove("models");
        body["model"] = context.Request.Model;
        var extra = ReadExtraParams(settings);
        var uri = BuildUri(settings.BaseUrl, context.Request.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
        ApplyRequestHeaders(request, context.Request, settings, extra);

        HttpResponseMessage response;
        try
        {
            response = await context.HttpClient.SendAsync(
                request,
                useProxyPool: false,
                HttpCompletionOption.ResponseHeadersRead,
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            var error = SanitizeMessage(exception.Message, settings);
            await TryLogAsync(
                "request.upstream.failed",
                $"直连上游失败：{error}",
                "Error",
                context.TraceId,
                context.Account.Id,
                context.Request.Model,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                details: new { endpoint = context.Request.Endpoint, route = "direct" });
            return new(
                AdapterResponse.ServerError(error),
                new PluginAttemptResult(
                    PluginAttemptOutcome.CooldownAccount,
                    IsTransportFailure: true,
                    Reason: error));
        }

        var statusCode = (int)response.StatusCode;
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var rawContent = await response.Content.ReadAsByteArrayAsync(context.CancellationToken);
                var rawText = Encoding.UTF8.GetString(rawContent);
                var error = DescribeResponseError(response.StatusCode, rawText, settings);
                await TryLogAsync(
                    "request.upstream.failed",
                    error,
                    "Error",
                    context.TraceId,
                    context.Account.Id,
                    context.Request.Model,
                    statusCode: statusCode,
                    durationMs: (int)stopwatch.ElapsedMilliseconds,
                    details: new { endpoint = context.Request.Endpoint, route = "direct" });
                var invalidCredential = statusCode == 401
                    || statusCode == 403 && (error.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("credential", StringComparison.OrdinalIgnoreCase));
                var outcome = invalidCredential
                    ? PluginAttemptOutcome.DisableAccount
                    : statusCode is 429 or >= 500
                        ? PluginAttemptOutcome.CooldownAccount
                        : PluginAttemptOutcome.NoPenalty;
                return new(
                    RawResponse(statusCode, rawContent, contentType),
                    new PluginAttemptResult(
                        outcome,
                        statusCode,
                        IndicatesInvalidCredential: invalidCredential,
                        Reason: error));
            }
            finally
            {
                response.Dispose();
            }
        }

        var isEventStream = context.Request.Stream
            || contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (isEventStream)
        {
            await TryLogAsync(
                "request.upstream.connected",
                "直连上游已建立流式响应",
                traceId: context.TraceId,
                accountId: context.Account.Id,
                model: context.Request.Model,
                statusCode: statusCode,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                details: new { endpoint = context.Request.Endpoint, route = "direct", contentType });
            return new(
                new AdapterResponse
                {
                    StatusCode = statusCode,
                    IsStreaming = true,
                    IsRawPassthrough = true,
                    ContentType = contentType,
                    RawStream = ReadRawStreamAsync(response, context.CancellationToken)
                },
                new PluginAttemptResult(PluginAttemptOutcome.Healthy, statusCode));
        }

        try
        {
            var content = await response.Content.ReadAsByteArrayAsync(context.CancellationToken);
            var usage = TryReadUsage(content);
            await TryLogAsync(
                "request.upstream.succeeded",
                "直连上游请求成功",
                traceId: context.TraceId,
                accountId: context.Account.Id,
                model: context.Request.Model,
                statusCode: statusCode,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                details: new { endpoint = context.Request.Endpoint, route = "direct", contentType });
            return new(
                new AdapterResponse
                {
                    StatusCode = statusCode,
                    IsRawPassthrough = true,
                    RawContent = content,
                    ContentType = contentType,
                    Usage = usage
                },
                new PluginAttemptResult(PluginAttemptOutcome.Healthy, statusCode));
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>执行每日签到任务，并仅处理已启用且打开自动签到的账号。</summary>
    private async Task DailyCheckInAsync(PluginScheduledTaskContext context)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await TryLogAsync(
            "checkin.task.started",
            "开始执行 ForwardAPI 每日自动签到",
            taskName: DailyCheckInTaskName);
        var accounts = await _host.Accounts.ListAsync(
            context.PlatformName,
            context.CancellationToken);
        var selected = accounts
            .Where(account => TryReadSettings(account, out var settings) && settings.Enabled && settings.AutoCheckIn)
            .ToArray();

        foreach (var account in selected)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await RunCheckInForAccountAsync(
                account,
                context.PlatformName,
                DailyCheckInTaskName,
                context.CancellationToken);
        }

        await TryLogAsync(
            "checkin.task.completed",
            $"每日自动签到任务完成：处理 {selected.Length} 个账号",
            taskName: DailyCheckInTaskName,
            durationMs: (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            details: new { selectedCount = selected.Length });
    }

    /// <summary>为单个账号执行一次完整签到流程并写入对应的账号任务日志。</summary>
    private async Task<CheckInResult> RunCheckInForAccountAsync(
        Account account,
        string platform,
        string taskName,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var label = account.Label ?? account.Id;
        CheckInResult result;
        if (!TryReadSettings(account, out var settings))
        {
            result = new(account.Id, label, "Failed", "账号配置无效", Error: "账号配置无效");
        }
        else
        {
            try
            {
                result = await ExecuteCheckInAsync(account, settings, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var message = "签到因任务取消而中断";
                await WriteAccountTaskLogAsync(account, platform, taskName, "Cancelled", message, message, startedAt, stopwatch);
                await TryLogAsync(
                    "checkin.cancelled",
                    message,
                    "Warning",
                    taskName: taskName,
                    accountId: account.Id,
                    durationMs: (int)stopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (Exception exception)
            {
                var error = SanitizeMessage(exception.Message, settings);
                result = new(account.Id, label, "Failed", $"签到请求异常：{error}", Error: error);
            }
        }

        result = result with
        {
            Message = SanitizeMessage(result.Message, settings),
            Error = result.Error is null
                ? null
                : SanitizeMessage(result.Error, settings)
        };

        var level = result.Status switch
        {
            "Success" => "Information",
            "Skipped" => "Warning",
            _ => "Error"
        };
        await TryLogAsync(
            $"checkin.{result.Status.ToLowerInvariant()}",
            result.Message,
            level,
            taskName: taskName,
            accountId: account.Id,
            statusCode: result.StatusCode,
            durationMs: (int)stopwatch.ElapsedMilliseconds,
            details: new { result.Status, result.Error, siteType = TryReadSettings(account, out var value) ? value.SiteType : null });
        await WriteAccountTaskLogAsync(
            account,
            platform,
            taskName,
            result.Status,
            result.Message,
            result.Error,
            startedAt,
            stopwatch,
            result.StatusCode);
        return result;
    }

    /// <summary>
    /// 按站点配置执行登录和签到：NewAPI 可使用内置默认路径，其他站点必须显式声明路径；
    /// 若某个候选路径不存在或不支持方法，则尝试下一个候选路径。
    /// </summary>
    private async Task<CheckInResult> ExecuteCheckInAsync(
        Account account,
        ForwardApiSettings settings,
        CancellationToken cancellationToken)
    {
        var extra = ReadExtraParams(settings);
        var customCheckInPath = ReadString(extra, "checkInPath");

        string[] checkInPaths;
        if (!string.IsNullOrWhiteSpace(customCheckInPath))
            checkInPaths = [customCheckInPath];
        else if (settings.SiteType.Equals("NewAPI", StringComparison.OrdinalIgnoreCase))
            checkInPaths = ["/api/user/checkin", "/api/user/sign_in"];
        else
            checkInPaths = [];
        if (checkInPaths.Length == 0)
            return new(account.Id, account.Label ?? account.Id, "Skipped", "未配置签到路径；Sub2API/Custom 账号不会猜测签到接口");
        if (checkInPaths.Any(path => !IsSafeSitePath(path)))
            return new(account.Id, account.Label ?? account.Id, "Failed", "签到路径无效", Error: "签到路径必须是站点内的相对路径");

        var accessToken = settings.ApiKey;
        var userId = string.Empty;
        var loginCookies = string.Empty;
        var loginPath = ReadString(extra, "loginPath");
        if (settings.SiteType.Equals("NewAPI", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(loginPath))
        {
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
                return new(account.Id, account.Label ?? account.Id, "Failed", "签到需要站点用户名和密码", Error: "username/password not configured");
            loginPath ??= "/api/user/login";
            if (!IsSafeSitePath(loginPath))
                return new(account.Id, account.Label ?? account.Id, "Failed", "登录路径无效", Error: "loginPath must be a relative site path");

            var loginBody = CreateLoginBody(extra, settings);
            var loginResult = await SendSiteJsonAsync(
                settings,
                loginPath,
                ReadString(extra, "loginMethod") ?? "POST",
                loginBody,
                ReadHeaders(extra, "loginHeaders"),
                cancellationToken,
                authenticateWithApiKey: false);
            if (!loginResult.Success)
            {
                return new(
                    account.Id,
                    account.Label ?? account.Id,
                    "Failed",
                    $"登录失败：{loginResult.Error}",
                    loginResult.StatusCode,
                    loginResult.Error);
            }
            loginCookies = loginResult.Cookies ?? string.Empty;
            accessToken = ReadJsonString(loginResult.Body, "data", "access_token")
                ?? ReadJsonString(loginResult.Body, "data", "token")
                ?? ReadJsonString(loginResult.Body, "access_token")
                ?? ReadJsonString(loginResult.Body, "token")
                ?? string.Empty;
            userId = ReadJsonStringOrNumber(loginResult.Body, "data", "user", "id")
                ?? ReadJsonStringOrNumber(loginResult.Body, "data", "id")
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(loginCookies))
                return new(
                    account.Id,
                    account.Label ?? account.Id,
                    "Failed",
                    "登录响应没有返回 access_token/token 或登录 Cookie",
                    loginResult.StatusCode,
                    "access_token and Set-Cookie missing in login response");
        }

        var checkInMethod = ReadString(extra, "checkInMethod") ?? "POST";
        var checkInBody = ReadObject(extra, "checkInBody") ?? new JsonObject();
        ReplaceTokens(checkInBody, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["access_token"] = accessToken,
            ["token"] = accessToken,
            ["user_id"] = userId,
            ["username"] = settings.Username
        });
        var checkInHeaders = ReadHeaders(extra, "checkInHeaders");
        if (!string.IsNullOrWhiteSpace(loginCookies))
            checkInHeaders["Cookie"] = MergeCookieHeaders(ReadHeader(checkInHeaders, "Cookie"), loginCookies);

        var attemptedPaths = new List<string>();
        CheckInResult? lastResult = null;
        foreach (var path in checkInPaths)
        {
            attemptedPaths.Add(path);
            var response = await SendSiteJsonAsync(
                settings,
                path,
                checkInMethod,
                checkInBody,
                checkInHeaders,
                cancellationToken,
                accessToken,
                userId);
            var result = InterpretCheckInResponse(account, response, path);
            if (result.Status == "Failed"
                && attemptedPaths.Count < checkInPaths.Length
                && IsCheckInPathUnavailable(response))
            {
                lastResult = result;
                continue;
            }
            return result;
        }

        return lastResult is null
            ? new(account.Id, account.Label ?? account.Id, "Failed", "签到请求没有可用路径", Error: "no check-in path was attempted")
            : lastResult with
            {
                Message = $"已尝试签到路径 {string.Join(", ", attemptedPaths)}，均失败：{lastResult.Message}"
            };
    }

    /// <summary>把签到接口的 HTTP 状态和 JSON 业务字段归一为 Success、Already 或 Failed。</summary>
    private static CheckInResult InterpretCheckInResponse(
        Account account,
        SiteResponse response,
        string path)
    {
        if (response.StatusCode is < 200 or >= 300)
            return new(account.Id, account.Label ?? account.Id, "Failed", $"签到失败：{response.Error ?? $"HTTP {response.StatusCode}"}", response.StatusCode, response.Error);
        if (!TryReadJsonObject(response.Body, out var root))
            return new(account.Id, account.Label ?? account.Id, "Failed", $"签到响应不是有效 JSON（{path}）", response.StatusCode, "invalid JSON response");

        var message = ReadElementString(root, "message");
        if (IsAlreadyCheckInMessage(message))
            return new(account.Id, account.Label ?? account.Id, "Already", message!, response.StatusCode);

        var hasSuccess = root.TryGetProperty("success", out var success);
        if (root.TryGetProperty("error", out var error)
            && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
            && (!hasSuccess || success.ValueKind != JsonValueKind.True))
        {
            var errorMessage = error.ValueKind == JsonValueKind.Object
                ? ReadElementString(error, "message") ?? error.ToString()
                : error.ToString();
            return new(account.Id, account.Label ?? account.Id, "Failed", $"签到失败：{errorMessage}", response.StatusCode, errorMessage);
        }

        if (hasSuccess && success.ValueKind == JsonValueKind.True)
        {
            if (string.IsNullOrWhiteSpace(message) && !HasCheckInReward(root))
                return new(account.Id, account.Label ?? account.Id, "Already", "今日已签到", response.StatusCode);
            return new(account.Id, account.Label ?? account.Id, "Success", message ?? "签到成功", response.StatusCode);
        }

        if (hasSuccess && success.ValueKind == JsonValueKind.False)
            return new(account.Id, account.Label ?? account.Id, "Failed", message ?? $"签到失败（{path}）", response.StatusCode, response.Error ?? message);

        if (root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var codeValue)
            && codeValue != 0)
            return new(account.Id, account.Label ?? account.Id, "Failed", message ?? $"签到返回错误码 {codeValue}", response.StatusCode, response.Error ?? message);

        if (HasCheckInReward(root) || ContainsSignInSuccess(message))
            return new(account.Id, account.Label ?? account.Id, "Success", message ?? "签到成功", response.StatusCode);

        return new(account.Id, account.Label ?? account.Id, "Failed", message ?? $"签到响应无法识别（{path}）", response.StatusCode, message ?? "unrecognized check-in response");
    }

    /// <summary>判断签到响应是否表示接口路径不存在或不接受当前 HTTP 方法。</summary>
    private static bool IsCheckInPathUnavailable(SiteResponse response)
        => response.StatusCode == 404
            || (response.Body?.Contains("Invalid URL", StringComparison.OrdinalIgnoreCase) ?? false)
            || (response.Body?.Contains("不存在", StringComparison.Ordinal) ?? false);

    /// <summary>尝试解析 JSON 对象；输入为空、格式错误或根节点不是对象时返回 false。</summary>
    private static bool TryReadJsonObject(string? json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>读取 JSON 对象中的字符串字段，其他 JSON 类型不会被当作成功消息。</summary>
    private static string? ReadElementString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

    /// <summary>检查签到响应是否包含可识别的奖励字段或奖励对象。</summary>
    private static bool HasCheckInReward(JsonElement root)
    {
        string[] rewardFields = ["quota_awarded", "checkin_quota", "check_in_quota", "checkin_reward", "reward"];
        return HasRewardField(root, rewardFields)
            || root.TryGetProperty("data", out var data) && HasRewardField(data, rewardFields);
    }

    /// <summary>递归检查响应对象及其嵌套对象是否包含任一指定奖励字段。</summary>
    private static bool HasRewardField(JsonElement element, IEnumerable<string> fields)
        => element.ValueKind == JsonValueKind.Object
            && fields.Any(field => element.TryGetProperty(field, out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));

    /// <summary>识别表示“今日已签到”等幂等成功状态的上游消息。</summary>
    private static bool IsAlreadyCheckInMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var text = message.Trim();
        return text.Contains("今日已签到", StringComparison.Ordinal)
            || text.Contains("已经签到", StringComparison.Ordinal)
            || text.Contains("已签到", StringComparison.Ordinal)
            || text.Contains("already checked in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("already signed in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("checked in today", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>识别上游中文/英文消息中明确表示签到成功的文本。</summary>
    private static bool ContainsSignInSuccess(string? message)
        => !string.IsNullOrWhiteSpace(message)
            && (message.Contains("签到成功", StringComparison.Ordinal)
                || message.Contains("success", StringComparison.OrdinalIgnoreCase));

    /// <summary>对站点相对路径发送 JSON 请求，并收集状态码、响应正文和会话 Cookie。</summary>
    private async Task<SiteResponse> SendSiteJsonAsync(
        ForwardApiSettings settings,
        string path,
        string method,
        JsonObject? body,
        IReadOnlyDictionary<string, string> customHeaders,
        CancellationToken cancellationToken,
        string? accessToken = null,
        string? userId = null,
        bool authenticateWithApiKey = true)
    {
        var httpMethod = method.Trim().ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            _ => null
        };
        if (httpMethod is null)
            return new(false, 400, null, "签到 method 只支持 GET、POST 或 PUT");

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["access_token"] = accessToken ?? string.Empty,
            ["token"] = accessToken ?? string.Empty,
            ["user_id"] = userId ?? string.Empty,
            ["username"] = settings.Username,
            ["password"] = settings.Password
        };
        var headers = customHeaders.ToDictionary(
            pair => pair.Key,
            pair => ReplaceTokens(pair.Value, tokens),
            StringComparer.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(httpMethod, BuildUri(settings.BaseUrl, path));
        if (httpMethod != HttpMethod.Get && body is not null)
        {
            var bodyCopy = body.DeepClone() as JsonObject ?? new JsonObject();
            ReplaceTokens(bodyCopy, tokens);
            request.Content = new StringContent(bodyCopy.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        }

        foreach (var header in headers)
        {
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header.Value);
                continue;
            }
            if (!IsSafeCustomHeader(header.Key) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (!request.Headers.Contains("Accept"))
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (!request.Headers.Contains("Origin"))
            request.Headers.TryAddWithoutValidation("Origin", new Uri(settings.BaseUrl).GetLeftPart(UriPartial.Authority));
        if (!request.Headers.Contains("Referer"))
            request.Headers.TryAddWithoutValidation("Referer", settings.BaseUrl.TrimEnd('/') + "/");
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(userId) && !request.Headers.Contains("new-api-user"))
            request.Headers.TryAddWithoutValidation("new-api-user", userId);
        if (!headers.TryGetValue("Authorization", out var authorization))
        {
            var token = accessToken ?? (authenticateWithApiKey ? settings.ApiKey : string.Empty);
            if (!string.IsNullOrWhiteSpace(token))
            {
                if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    token = token["Bearer ".Length..].Trim();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var client = _host.Http.CreateDirectClient(new PluginHttpClientOptions { AllowAutoRedirect = true });
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var error = response.IsSuccessStatusCode
            ? IsApplicationError(text) ? DescribeResponseError(response.StatusCode, text, settings) : null
            : DescribeResponseError(response.StatusCode, text, settings);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? string.Join("; ", setCookies.Select(cookie => cookie.Split(';', 2)[0].Trim()).Where(cookie => cookie.Contains('=')))
            : string.Empty;
        return new SiteResponse(response.IsSuccessStatusCode && error is null, (int)response.StatusCode, text, error, cookies);
    }

    /// <summary>从不区分大小写的请求头集合中读取指定头值。</summary>
    private static string? ReadHeader(IReadOnlyDictionary<string, string> headers, string name)
        => headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>合并多个 Cookie 请求头，并按 Cookie 名保留最后一次出现的值。</summary>
    private static string MergeCookieHeaders(params string?[] cookieHeaders)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in cookieHeaders.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var pair in header!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                cookies[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
            }
        }
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}"));
    }

    /// <summary>写入单个账号的签到任务结果；日志写入失败只记录诊断信息，不改变签到结果。</summary>
    private async Task WriteAccountTaskLogAsync(
        Account account,
        string platform,
        string taskName,
        string status,
        string message,
        string? error,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        int? statusCode = null)
    {
        try
        {
            await _host.Tasks.WriteLogAsync(new TaskLog
            {
                PluginKey = _host.PluginKey,
                Platform = platform,
                TaskName = taskName,
                AccountId = account.Id,
                Status = status,
                Message = message,
                Error = error,
                DetailsJson = JsonSerializer.Serialize(new { statusCode }),
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await TryLogAsync(
                "checkin.tasklog.failed",
                $"签到结果无法写入任务日志：{SanitizeMessage(exception.Message, null)}",
                "Warning",
                taskName: taskName,
                accountId: account.Id);
        }
    }

    /// <summary>写入 ForwardAPI 插件诊断日志，并隔离日志服务异常避免影响请求或任务。</summary>
    private async Task TryLogAsync(
        string eventType,
        string message,
        string level = "Information",
        string? traceId = null,
        string? accountId = null,
        string? model = null,
        int? statusCode = null,
        int? durationMs = null,
        string? taskName = null,
        object? details = null)
    {
        try
        {
            await _host.LogAsync(
                "forwardapi",
                eventType,
                SanitizeMessage(message, null),
                level,
                traceId,
                taskName,
                accountId,
                model,
                statusCode,
                durationMs,
                details,
                CancellationToken.None);
        }
        catch
        {
            // Diagnostic logging must never fail an upstream request or scheduled task.
        }
    }

    /// <summary>构造不惩罚账号的本地请求失败结果，例如账号配置或请求体无效。</summary>
    private static PluginInvocationResult RequestFailure(
        PluginAttemptContext context,
        AdapterResponse response,
        string reason)
        => new(
            response,
            new PluginAttemptResult(PluginAttemptOutcome.NoPenalty, response.StatusCode, Reason: reason));

    /// <summary>构造保留上游状态码、内容类型和原始字节的透传响应。</summary>
    private static AdapterResponse RawResponse(int statusCode, byte[] content, string? contentType)
        => new()
        {
            StatusCode = statusCode,
            IsRawPassthrough = true,
            RawContent = content,
            ContentType = contentType
        };

    /// <summary>分块读取上游响应原始字节，并在枚举结束或取消时释放响应流和响应对象。</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) yield break;
                yield return buffer.AsMemory(0, count).ToArray();
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>从原始 JSON 响应提取用量供宿主统计，响应正文仍按原始字节透传。</summary>
    private static Usage? TryReadUsage(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("usage", out var usage)) return null;
            var prompt = ReadInt(usage, "prompt_tokens") ?? ReadInt(usage, "input_tokens") ?? 0;
            var completion = ReadInt(usage, "completion_tokens") ?? ReadInt(usage, "output_tokens") ?? 0;
            var total = ReadInt(usage, "total_tokens") ?? prompt + completion;
            return new Usage(prompt, completion, total);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var number)
                ? number
                : null;

    /// <summary>从模型接口的 `data` 或 `models` 数组读取并去重模型 ID/名称。</summary>
    private static string[] ParseModelIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var list = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
                ? data
                : root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("models", out var models) ? models : default;
        if (list.ValueKind != JsonValueKind.Array) return [];

        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>将站点 Base URL 与 API 路径组合，并避免重复的 `/v1` 路径段。</summary>
    private static Uri BuildUri(string baseUrl, string path)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var route = path.Trim().TrimStart('/');
        if (basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            && route.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
            route = route["v1/".Length..];
        if (route.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            && basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUri = new Uri(baseUri, "../");
        return new Uri(baseUri, route);
    }

    /// <summary>读取高级参数 JSON 对象；配置无效时使用空对象和各功能默认值。</summary>
    private static JsonObject ReadExtraParams(ForwardApiSettings settings)
    {
        try { return JsonNode.Parse(settings.ExtraParams) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    /// <summary>按高级参数设置模型发现和普通上游请求的 API Key 请求头。</summary>
    private static void ApplyApiKeyHeader(HttpRequestMessage request, ForwardApiSettings settings)
    {
        var extra = ReadExtraParams(settings);
        var keyHeader = ReadString(extra, "apiKeyHeader") ?? "Authorization";
        if (!IsSafeCustomHeader(keyHeader)) keyHeader = "Authorization";
        var prefix = ReadString(extra, "apiKeyPrefix")
            ?? (keyHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " : string.Empty);
        request.Headers.TryAddWithoutValidation(keyHeader, prefix + settings.ApiKey);
    }

    /// <summary>按额度专用覆盖项设置额度接口的 API Key 请求头。</summary>
    private static void ApplyQuotaApiKeyHeader(HttpRequestMessage request, ForwardApiSettings settings)
    {
        var extra = ReadExtraParams(settings);
        var keyHeader = ReadString(extra, "quotaApiKeyHeader") ?? "Authorization";
        if (!IsSafeCustomHeader(keyHeader)) keyHeader = "Authorization";
        var prefix = ReadString(extra, "quotaApiKeyPrefix")
            ?? (keyHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " : string.Empty);
        request.Headers.TryAddWithoutValidation(keyHeader, prefix + settings.ApiKey);
    }

    /// <summary>读取并深复制指定 JSON 对象，避免后续模板替换修改原始配置树。</summary>
    private static JsonObject? ReadObject(JsonObject value, string property)
        => value[property] is JsonObject body ? body.DeepClone() as JsonObject : null;

    /// <summary>读取 JSON 对象中的字符串属性；非字符串属性返回空值。</summary>
    private static string? ReadString(JsonObject value, string property)
        => value[property] is JsonValue node && node.TryGetValue<string>(out var result)
            ? result
            : null;

    /// <summary>将指定 JSON 子对象转换为字符串请求头集合。</summary>
    private static Dictionary<string, string> ReadHeaders(JsonObject extra, string property)
    {
        if (extra[property] is not JsonObject values) return new Dictionary<string, string>();
        return values
            .Where(pair => pair.Value is JsonValue value && value.TryGetValue<string>(out _))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!.GetValue<string>(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>生成登录 JSON 请求体；未提供自定义模板时使用账号用户名和密码。</summary>
    private static JsonObject CreateLoginBody(JsonObject extra, ForwardApiSettings settings)
    {
        var configured = ReadObject(extra, "loginBody");
        if (configured is null)
            return new JsonObject
            {
                ["username"] = settings.Username,
                ["password"] = settings.Password
            };
        ReplaceTokens(configured, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["username"] = settings.Username,
            ["password"] = settings.Password
        });
        return configured;
    }

    /// <summary>按属性路径从 JSON 文本中读取字符串值；解析失败或路径不存在时返回空值。</summary>
    private static string? ReadJsonString(string? json, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (var part in path)
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(part, out current))
                    return null;
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>按属性路径读取字符串或数字文本，供账号 ID 等可能为数字的站点字段使用。</summary>
    private static string? ReadJsonStringOrNumber(string? json, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (var part in path)
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(part, out current))
                    return null;
            }
            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.GetRawText(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>检查 HTTP 200 JSON 是否通过 success、error 或 code 字段报告了业务错误。</summary>
    private static bool IsApplicationError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return true;
            if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                return true;
            if (root.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var number)
                && number != 0)
                return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>从上游错误正文提取可读摘要并隐藏已配置密钥和密码。</summary>
    private static string DescribeResponseError(HttpStatusCode statusCode, string body, ForwardApiSettings settings)
    {
        var summary = ReadJsonString(body, "message")
            ?? ReadJsonString(body, "error", "message")
            ?? ReadJsonString(body, "error")
            ?? ReadJsonString(body, "data", "message")
            ?? body.Trim();
        if (string.IsNullOrWhiteSpace(summary)) summary = statusCode.ToString();
        return SanitizeMessage($"HTTP {(int)statusCode} {statusCode}: {summary}", settings);
    }

    /// <summary>从诊断文本中移除账号密钥/密码，并限制日志消息最大长度。</summary>
    private static string SanitizeMessage(string? message, ForwardApiSettings? settings)
    {
        var safe = message ?? "未知错误";
        if (settings is not null)
        {
            foreach (var secret in new[] { settings.ApiKey, settings.Password })
                if (!string.IsNullOrEmpty(secret))
                    safe = safe.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        }
        return safe.Length <= 1200 ? safe : safe[..1200];
    }

    /// <summary>限制登录和签到路径为站点内相对路径，阻止绝对 URL 和路径穿越。</summary>
    private static bool IsSafeSitePath(string path)
        => !string.IsNullOrWhiteSpace(path)
            && !path.Contains("://", StringComparison.Ordinal)
            && !path.StartsWith("//", StringComparison.Ordinal)
            && !path.Contains("..", StringComparison.Ordinal)
            && !path.Contains('#');

    /// <summary>拒绝会覆盖连接路由、代理、Cookie 或消息长度语义的危险自定义请求头。</summary>
    private static bool IsSafeCustomHeader(string name)
        => !string.IsNullOrWhiteSpace(name)
            && !name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>转发允许的客户端请求头，并由账号凭据生成认证头和 Messages 端点必需头。</summary>
    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        AdapterRequest source,
        ForwardApiSettings settings,
        JsonObject extra)
    {
        foreach (var header in source.RequestHeaders)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var isMessages = source.Endpoint.Equals("/v1/messages", StringComparison.OrdinalIgnoreCase);
        var keyHeader = ReadString(extra, "apiKeyHeader")
            ?? (isMessages ? "x-api-key" : "Authorization");
        if (!IsSafeCustomHeader(keyHeader))
            keyHeader = "Authorization";
        var prefix = ReadString(extra, "apiKeyPrefix") ?? (keyHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " : string.Empty);
        request.Headers.TryAddWithoutValidation(keyHeader, prefix + settings.ApiKey);
        if (isMessages
            && !request.Headers.Contains("anthropic-version")
            && !source.RequestHeaders.ContainsKey("anthropic-version"))
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
    }

    /// <summary>递归替换 JSON 请求模板中所有字符串节点的占位符。</summary>
    private static void ReplaceTokens(JsonObject body, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var property in body.ToArray())
        {
            if (property.Value is JsonObject child)
                ReplaceTokens(child, tokens);
            else if (property.Value is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                    if (array[index] is JsonObject nested) ReplaceTokens(nested, tokens);
                    else if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                        array[index] = ReplaceTokens(text, tokens);
            }
            else if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                body[property.Key] = ReplaceTokens(text, tokens);
        }
    }

    /// <summary>在单个模板字符串中按不区分大小写的方式替换配置占位符。</summary>
    private static string ReplaceTokens(string value, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var token in tokens)
            value = value.Replace("{{" + token.Key + "}}", token.Value, StringComparison.OrdinalIgnoreCase);
        return value;
    }

    /// <summary>签到相关站点请求的归一结果，包含 HTTP/业务成功状态及登录 Cookie。</summary>
    /// <param name="Success">HTTP 和站点业务逻辑是否都报告成功。</param>
    /// <param name="StatusCode">上游 HTTP 状态码。</param>
    /// <param name="Body">上游响应正文文本。</param>
    /// <param name="Error">失败时提取的可读错误摘要。</param>
    /// <param name="Cookies">登录响应中可供后续签到使用的 Cookie 集合。</param>
    private sealed record SiteResponse(bool Success, int StatusCode, string? Body, string? Error, string? Cookies = null);

    /// <summary>单个账号的一次签到结果及管理页面/任务日志展示信息。</summary>
    /// <param name="AccountId">对应的宿主账号标识。</param>
    /// <param name="Label">管理页面显示的账号名称。</param>
    /// <param name="Status">签到结果状态：Success、Already、Skipped 或 Failed。</param>
    /// <param name="Message">面向管理页面和任务日志的结果说明。</param>
    /// <param name="StatusCode">如有上游请求，对应的 HTTP 状态码。</param>
    /// <param name="Error">失败时保留的错误详情。</param>
    private sealed record CheckInResult(
        string AccountId,
        string Label,
        string Status,
        string Message,
        int? StatusCode = null,
        string? Error = null);
}
