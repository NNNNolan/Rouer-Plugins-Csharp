using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.ForwardAPI;

public sealed partial class ForwardApiTerminal
{
    /// <summary>列出当前插件实例保存的 ForwardAPI 账号及页面展示所需的脱敏信息。</summary>
    /// <param name="context">包含插件实例、平台和请求取消令牌的管理端点上下文。</param>
    /// <returns>包含账号卡片数组的 JSON 响应。</returns>
    [PluginEndpoint("GET", "accounts")]
    public async Task<PluginResult> ListAccountsAsync(PluginHttpContext context)
    {
        var accounts = await _host.Accounts.ListAsync(
            context.Platform,
            context.CancellationToken);
        return context.Ok(new { accounts = accounts.Select(ToAccountCard).ToArray() });
    }

    /// <summary>
    /// 新增或更新一个上游账号。保存前会校验站点设置、模型目录和所选模型，
    /// 并在凭据或连接参数变化后要求重新发现模型。
    /// </summary>
    /// <param name="context">包含 JSON 表单数据和宿主账号存储访问能力的端点上下文。</param>
    /// <returns>保存后的脱敏账号卡片，或描述校验/上游发现失败原因的响应。</returns>
    [PluginEndpoint("POST", "accounts/save")]
    public async Task<PluginResult> SaveAccountAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountSaveInput>(context.Body, out var input))
            return context.BadRequest("请求内容无效");

        Account? existing = null;
        if (!string.IsNullOrWhiteSpace(input.Id))
        {
            existing = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
            if (existing is null) return context.Json(404, new { error = "账号不存在" });
        }

        var previous = existing is not null && TryReadSettings(existing, out var oldSettings)
            ? oldSettings
            : new ForwardApiSettings();
        var siteType = NormalizeSiteType(input.SiteType ?? previous.SiteType);
        if (siteType is null)
            return context.BadRequest("站点类型必须是 NewAPI、Sub2API 或 Custom");

        var baseUrl = (input.BaseUrl ?? previous.BaseUrl).Trim().TrimEnd('/');
        if (!TryValidateBaseUrl(baseUrl, out var urlError))
            return context.BadRequest(urlError);

        var apiKey = string.IsNullOrWhiteSpace(input.ApiKey) ? previous.ApiKey : input.ApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return context.BadRequest("API Key 不能为空");

        var label = input.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
            return context.BadRequest("账号名称不能为空");

        var endpoints = (input.Endpoints ?? previous.Endpoints)
            .Select(NormalizeEndpoint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (endpoints.Length == 0
            || endpoints.Any(endpoint => !SupportedEndpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase)))
            return context.BadRequest("至少选择一个有效的请求端点");

        if (!TryReadExtraParams(input.ExtraParams, previous.ExtraParams, out var extraParams, out var extraError))
            return context.BadRequest(extraError);

        var settings = new ForwardApiSettings
        {
            SiteType = siteType,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Username = input.Username?.Trim() ?? previous.Username,
            Password = string.IsNullOrEmpty(input.Password) ? previous.Password : input.Password,
            Weight = Math.Clamp(input.Weight ?? previous.Weight, 0, 1000),
            Endpoints = endpoints,
            AutoCheckIn = input.AutoCheckIn ?? previous.AutoCheckIn,
            Enabled = input.Enabled ?? previous.Enabled,
            ExtraParams = extraParams
        };

        var credentialsChanged = existing is null
            || !string.Equals(siteType, previous.SiteType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(baseUrl, previous.BaseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(apiKey, previous.ApiKey, StringComparison.Ordinal)
            || !string.Equals(extraParams, previous.ExtraParams, StringComparison.Ordinal);
        var models = input.Models is null
            ? credentialsChanged || existing is null ? [] : ReadModels(existing)
            : input.Models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (models.Length == 0)
            return context.BadRequest("请先获取模型并至少选择一个允许使用的模型");

        var discoveredModels = input.AvailableModels?
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (input.AvailableModels is not null && discoveredModels is not { Length: > 0 })
            return context.BadRequest("获取到的可用模型列表为空，请重新获取模型");
        var hasSavedModelCatalog = existing is not null
            && !string.IsNullOrWhiteSpace(GetCredentialField(existing, "modelsUpdatedAt"))
            && ReadModelList(existing, "availableModels").Length > 0;
        var requiresModelDiscovery = existing is null || credentialsChanged || !hasSavedModelCatalog;
        if (requiresModelDiscovery && discoveredModels is not { Length: > 0 })
            return context.BadRequest("保存前请先获取当前 API Key 可用模型");

        var allowedModels = discoveredModels
            ?? (credentialsChanged ? null : existing is null ? null : ReadModelList(existing, "availableModels"));
        if (allowedModels is { Length: > 0 }
            && models.Any(model => !allowedModels.Contains(model, StringComparer.OrdinalIgnoreCase)))
            return context.BadRequest("只能选择本次模型目录中返回的模型");

        var fields = existing?.Credential is CustomCredential custom
            ? new Dictionary<string, string?>(custom.Fields, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);
        if (credentialsChanged)
        {
            fields.Remove("availableModels");
            fields.Remove("modelsUpdatedAt");
            fields.Remove("quotaSnapshot");
            fields.Remove("quotaUpdatedAt");
        }
        if (discoveredModels is not null)
        {
            fields["availableModels"] = JsonSerializer.Serialize(discoveredModels, JsonOptions);
            fields["modelsUpdatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        }
        fields["models"] = JsonSerializer.Serialize(models, JsonOptions);
        fields["modelsConfigured"] = "true";
        fields["settings"] = JsonSerializer.Serialize(settings, JsonOptions);
        var status = existing?.Status ?? new ResourceStatus();
        if (existing is not null
            && (credentialsChanged || (input.Enabled == true && settings.Enabled))
            && (status.State is ResourceState.Disabled or ResourceState.Invalid))
            status = new ResourceStatus { Version = status.Version + 1 };
        var account = new Account
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            PluginKey = context.PluginKey,
            Platform = context.Platform,
            Label = label,
            Credential = new CustomCredential(fields),
            Status = status
        };
        var saved = await _host.Accounts.SaveAsync(account, context.CancellationToken);
        await RebuildAccountModelSnapshotAsync(context.PluginKey, context.CancellationToken);
        _host.Models.Invalidate(ForwardApiPlatform);
        await TryLogAsync(
            "account.saved",
            $"账号“{label}”配置已保存",
            accountId: saved.Id,
            details: new { settings.SiteType, settings.Weight, settings.Endpoints, settings.Enabled });
        return context.Ok(new { account = ToAccountCard(saved) });
    }

    /// <summary>使用表单中的站点类型、Base URL、API Key 和高级参数临时查询上游模型目录。</summary>
    /// <param name="context">包含模型发现表单和取消令牌的端点上下文。</param>
    /// <returns>发现到的模型 ID、数量和时间；上游错误会映射为 HTTP 错误响应。</returns>
    [PluginEndpoint("POST", "models/discover")]
    public async Task<PluginResult> DiscoverModelsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<ModelDiscoveryInput>(context.Body, out var input))
            return context.BadRequest("请求内容无效");

        Account? existing = null;
        if (!string.IsNullOrWhiteSpace(input.Id))
        {
            existing = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
            if (existing is null) return context.Json(404, new { error = "账号不存在" });
        }

        var previous = existing is not null && TryReadSettings(existing, out var oldSettings)
            ? oldSettings
            : new ForwardApiSettings();
        var siteType = NormalizeSiteType(input.SiteType ?? previous.SiteType);
        if (siteType is null) return context.BadRequest("站点类型必须是 NewAPI、Sub2API 或 Custom");

        var baseUrl = (input.BaseUrl ?? previous.BaseUrl).Trim().TrimEnd('/');
        if (!TryValidateBaseUrl(baseUrl, out var urlError)) return context.BadRequest(urlError);

        var apiKey = string.IsNullOrWhiteSpace(input.ApiKey) ? previous.ApiKey : input.ApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) return context.BadRequest("API Key 不能为空");
        if (!TryReadExtraParams(input.ExtraParams, previous.ExtraParams, out var extraParams, out var extraError))
            return context.BadRequest(extraError);

        var settings = new ForwardApiSettings
        {
            SiteType = siteType,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            ExtraParams = extraParams
        };

        try
        {
            var result = await FetchModelsAsync(settings, context.CancellationToken);
            if (result.Error is not null)
                return context.Json(result.StatusCode, new { error = result.Error });
            return context.Ok(new { models = result.Models, count = result.Models!.Length, updatedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return context.Json(502, new { error = SanitizeMessage(exception.Message, settings) });
        }
    }

    /// <summary>从宿主账号存储中删除指定账号，并刷新本地模型快照。</summary>
    /// <param name="context">请求体中需包含要删除的账号 ID。</param>
    /// <returns>删除结果；账号不存在或 ID 无效时返回相应错误。</returns>
    [PluginEndpoint("POST", "accounts/delete")]
    public async Task<PluginResult> DeleteAccountAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountIdInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("账号 ID 不能为空");
        var account = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
        if (account is null) return context.Json(404, new { error = "账号不存在" });
        await _host.Accounts.DeleteAsync(account.Id, context.CancellationToken);
        await RebuildAccountModelSnapshotAsync(context.PluginKey, context.CancellationToken);
        _host.Models.Invalidate(ForwardApiPlatform);
        await TryLogAsync("account.deleted", $"账号“{account.Label ?? account.Id}”已删除", accountId: account.Id);
        return context.Ok(new { deleted = true });
    }

    /// <summary>重新读取指定账号的上游模型目录并更新已保存的候选模型及更新时间。</summary>
    /// <param name="context">请求体中需包含账号 ID。</param>
    /// <returns>更新后的候选模型列表和数量。</returns>
    [PluginEndpoint("POST", "models/refresh")]
    public async Task<PluginResult> RefreshModelsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountIdInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("账号 ID 不能为空");
        var account = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
        if (account is null) return context.Json(404, new { error = "账号不存在" });
        if (!TryReadSettings(account, out var settings))
            return context.BadRequest("账号配置无效");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await FetchModelsAsync(settings, context.CancellationToken);
            if (result.Error is not null)
            {
                await TryLogAsync(
                    "models.refresh.failed",
                    result.Error,
                    "Error",
                    accountId: account.Id,
                    statusCode: result.StatusCode,
                    durationMs: (int)stopwatch.ElapsedMilliseconds);
                return context.Json(result.StatusCode, new { error = result.Error });
            }

            var models = result.Models!;

            var fields = CopyCredentialFields(account);
            fields["availableModels"] = JsonSerializer.Serialize(models, JsonOptions);
            fields["modelsUpdatedAt"] = DateTimeOffset.UtcNow.ToString("O");
            account.Credential = new CustomCredential(fields);
            await _host.Accounts.SaveAsync(account, context.CancellationToken);
            await TryLogAsync(
                "models.refresh.succeeded",
                $"成功拉取 {models.Length} 个模型",
                accountId: account.Id,
                statusCode: result.StatusCode,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                details: new { count = models.Length });
            return context.Ok(new { models, count = models.Length });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = SanitizeMessage(exception.Message, settings);
            await TryLogAsync(
                "models.refresh.failed",
                error,
                "Error",
                accountId: account.Id,
                durationMs: (int)stopwatch.ElapsedMilliseconds);
            return context.Json(502, new { error });
        }
    }

    /// <summary>查询指定 NewAPI 或 Sub2API 账号的额度，并保存最新额度快照。</summary>
    /// <param name="context">请求体中需包含账号 ID。</param>
    /// <returns>额度总量、已用量、剩余量、单位及更新时间；不支持的站点或上游错误返回错误响应。</returns>
    [PluginEndpoint("POST", "quota/refresh")]
    public async Task<PluginResult> RefreshQuotaAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountIdInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("账号 ID 不能为空");
        var account = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
        if (account is null) return context.Json(404, new { error = "账号不存在" });
        if (!TryReadSettings(account, out var settings)) return context.BadRequest("账号配置无效");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var quota = await FetchQuotaAsync(settings, context.CancellationToken);
            var fields = CopyCredentialFields(account);
            fields["quotaSnapshot"] = JsonSerializer.Serialize(quota, JsonOptions);
            fields["quotaUpdatedAt"] = quota.UpdatedAt.ToString("O");
            account.Credential = new CustomCredential(fields);
            await _host.Accounts.SaveAsync(account, context.CancellationToken);
            await TryLogAsync(
                "quota.refresh.succeeded",
                "额度刷新成功",
                accountId: account.Id,
                statusCode: 200,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                details: new { settings.SiteType });
            return context.Ok(new { quota });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = SanitizeMessage(exception.Message, settings);
            await TryLogAsync(
                "quota.refresh.failed",
                error,
                "Error",
                accountId: account.Id,
                statusCode: 502,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                details: new { settings.SiteType, exceptionType = exception.GetType().Name });
            return context.Json(502, new { error });
        }
    }

    /// <summary>立即为指定账号运行一次站点签到，并将结果写入宿主任务日志。</summary>
    /// <param name="context">请求体中需包含账号 ID。</param>
    /// <returns>签到状态、结果说明和可用的上游 HTTP 状态码。</returns>
    [PluginEndpoint("POST", "checkin/run")]
    public async Task<PluginResult> RunCheckInAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountIdInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("账号 ID 不能为空");
        var account = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
        if (account is null) return context.Json(404, new { error = "账号不存在" });
        var result = await RunCheckInForAccountAsync(account, context.Platform, "forwardapi-manual-checkin", context.CancellationToken);
        return context.Json(result.Status == "Failed" ? 502 : 200, result);
    }

    /// <summary>构造管理页面使用的账号数据，并确保 API Key 以掩码形式返回。</summary>
    private static object ToAccountCard(Account account)
    {
        var hasSettings = TryReadSettings(account, out var settings);
        var key = hasSettings ? settings.ApiKey : string.Empty;
        return new
        {
            id = account.Id,
            label = account.Label ?? string.Empty,
            siteType = settings.SiteType,
            baseUrl = settings.BaseUrl,
            apiKeyMasked = MaskSecret(key),
            hasApiKey = !string.IsNullOrWhiteSpace(key),
            username = settings.Username,
            hasPassword = !string.IsNullOrEmpty(settings.Password),
            weight = settings.Weight,
            endpoints = settings.Endpoints,
            autoCheckIn = settings.AutoCheckIn,
            enabled = settings.Enabled,
            extraParams = settings.ExtraParams,
            models = ReadModels(account),
            availableModels = ReadAvailableModels(account),
            modelsUpdatedAt = GetCredentialField(account, "modelsUpdatedAt"),
            quota = ReadQuotaSnapshot(account),
            quotaUpdatedAt = GetCredentialField(account, "quotaUpdatedAt"),
            state = account.Status.State.ToString()
        };
    }

    /// <summary>调用上游 `/v1/models`，将模型目录解析为模型 ID 列表。</summary>
    private async Task<(string[]? Models, int StatusCode, string? Error)> FetchModelsAsync(
        ForwardApiSettings settings,
        CancellationToken cancellationToken)
    {
        using var client = _host.Http.CreateDirectClient(new PluginHttpClientOptions { AllowAutoRedirect = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings.BaseUrl, "/v1/models"));
        ApplyApiKeyHeader(request, settings);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (null, (int)response.StatusCode, DescribeResponseError(response.StatusCode, responseText, settings));

        string[] models;
        try
        {
            models = ParseModelIds(responseText);
        }
        catch (JsonException)
        {
            return (null, 502, "上游模型列表不是有效的 JSON");
        }

        return models.Length == 0
            ? (null, 502, "上游返回成功，但响应中没有可用模型（data[].id）")
            : (models, (int)response.StatusCode, null);
    }

    /// <summary>
    /// 读取 NewAPI 或 Sub2API 额度响应，兼容其常见额度、订阅周期和 rate limit 字段，
    /// 并按 NewAPI 站点配置换算展示单位。
    /// </summary>
    private async Task<ApiKeyQuotaSnapshot> FetchQuotaAsync(
        ForwardApiSettings settings,
        CancellationToken cancellationToken)
    {
        var isNewApi = settings.SiteType.Equals("NewAPI", StringComparison.OrdinalIgnoreCase);
        var isSub2Api = settings.SiteType.Equals("Sub2API", StringComparison.OrdinalIgnoreCase);
        if (!isNewApi && !isSub2Api)
            throw new InvalidOperationException("通用 API 没有标准的额度查询接口");

        using var client = _host.Http.CreateDirectClient(new PluginHttpClientOptions { AllowAutoRedirect = true });
        var path = isNewApi ? "/api/usage/token" : "/v1/usage";
        using var response = await SendQuotaRequestAsync(client, settings, path, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeResponseError(response.StatusCode, responseText, settings));

        using var document = JsonDocument.Parse(responseText);
        var data = document.RootElement;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("data", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
            data = nested;

        var quotaData = data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("quota", out var nestedQuota)
            && nestedQuota.ValueKind == JsonValueKind.Object
                ? nestedQuota
                : data;
        var total = ReadQuotaNumber(data, "total_granted", "quota", "total", "limit", "quota_limit")
            ?? ReadQuotaNumber(quotaData, "total_granted", "total", "limit", "quota_limit");
        var used = ReadQuotaNumber(data, "total_used", "used_quota", "used", "quota_used")
            ?? ReadQuotaNumber(quotaData, "total_used", "used_quota", "used", "quota_used");
        var remaining = ReadQuotaNumber(data, "total_available", "remaining_quota", "remaining", "available", "quota_remaining")
            ?? ReadQuotaNumber(quotaData, "total_available", "remaining_quota", "remaining", "available", "quota_remaining");
        var unlimited = ReadQuotaBoolean(data, "unlimited_quota", "unlimited");
        if (isSub2Api && remaining is < 0m)
        {
            unlimited = true;
            remaining = null;
        }

        if (isSub2Api
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("subscription", out var subscription)
            && subscription.ValueKind == JsonValueKind.Object)
        {
            var subscriptionWindows = new[]
            {
                (Limit: "monthly_limit_usd", Used: "monthly_usage_usd"),
                (Limit: "weekly_limit_usd", Used: "weekly_usage_usd"),
                (Limit: "daily_limit_usd", Used: "daily_usage_usd")
            };
            foreach (var window in subscriptionWindows)
            {
                var windowTotal = ReadQuotaNumber(subscription, window.Limit);
                if (windowTotal is not > 0m) continue;
                total ??= windowTotal;
                used ??= ReadQuotaNumber(subscription, window.Used);
                break;
            }
        }

        if (isSub2Api
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("rate_limits", out var rateLimits)
            && rateLimits.ValueKind == JsonValueKind.Array
            && ReadTightestRateLimit(rateLimits) is { } rateLimit)
        {
            total ??= rateLimit.Total;
            used ??= rateLimit.Used;
            remaining ??= rateLimit.Remaining;
        }

        if (isSub2Api
            && data.ValueKind == JsonValueKind.Object
            && used is null
            && data.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("total", out var usageTotal))
            used = ReadQuotaNumber(usageTotal, "cost");

        if (!unlimited && remaining is null && total is not null && used is not null)
            remaining = Math.Max(0m, total.Value - used.Value);
        if (!unlimited && total is null && remaining is not null && used is not null)
            total = remaining.Value + used.Value;

        if (total is null && used is not null && remaining is not null) total = used + remaining;
        if (remaining is null && total is not null && used is not null) remaining = Math.Max(0, total.Value - used.Value);
        if (remaining is null && total is null && used is null && !unlimited)
            throw new InvalidOperationException("上游额度响应中没有可识别的额度字段");

        var unit = ReadQuotaString(data, "currency", "unit") ?? (isSub2Api ? "USD" : "额度");
        if (isNewApi)
        {
            var status = await TryReadNewApiStatusAsync(client, settings, cancellationToken);
            var quotaPerUnit = ReadQuotaNumber(status, "quota_per_unit");
            if (ReadQuotaBoolean(status, "display_in_currency") && quotaPerUnit is > 0m)
            {
                total /= quotaPerUnit;
                used /= quotaPerUnit;
                remaining /= quotaPerUnit;
                var displayType = ReadQuotaString(status, "quota_display_type", "custom_currency_symbol", "currency");
                unit = displayType?.ToUpperInvariant() switch
                {
                    "CNY" or "RMB" => "¥",
                    "USD" => "$",
                    _ => displayType ?? "余额"
                };
            }
        }

        return new ApiKeyQuotaSnapshot(total, used, remaining, unlimited, unit, DateTimeOffset.UtcNow);
    }

    /// <summary>向额度接口发送 GET；遇到 404/405 时再尝试路径末尾带斜杠的形式。</summary>
    private static async Task<HttpResponseMessage> SendQuotaRequestAsync(
        HttpClient client,
        ForwardApiSettings settings,
        string path,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildUri(settings.BaseUrl, path);
        var response = await SendQuotaRequestAsync(client, settings, requestUri, cancellationToken);
        if (response.IsSuccessStatusCode
            || response.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed))
            return response;

        response.Dispose();
        var trailingSlashUri = BuildUri(settings.BaseUrl, path.TrimEnd('/') + "/");
        return await SendQuotaRequestAsync(client, settings, trailingSlashUri, cancellationToken);
    }

    /// <summary>
    /// 执行单次额度 GET 请求。只有同主机、同路径且同源或 HTTP 到 HTTPS 安全升级的重定向，
    /// 才会在重定向后重新附加 API Key。
    /// </summary>
    private static async Task<HttpResponseMessage> SendQuotaRequestAsync(
        HttpClient client,
        ForwardApiSettings settings,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyQuotaApiKeyHeader(request, settings);
        var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var effectiveUri = response.RequestMessage?.RequestUri;
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized
            || effectiveUri is null
            || !IsSafeQuotaRedirect(requestUri, effectiveUri))
            return response;

        // HttpClient clears Authorization when it follows a redirect. NewAPI deployments
        // may redirect between slash forms or upgrade the same host from HTTP to HTTPS.
        response.Dispose();
        using var redirectedRequest = new HttpRequestMessage(HttpMethod.Get, effectiveUri);
        ApplyQuotaApiKeyHeader(redirectedRequest, settings);
        return await client.SendAsync(
            redirectedRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    /// <summary>判断额度查询重定向是否只改变斜杠形式或将同一主机安全升级到 HTTPS。</summary>
    private static bool IsSafeQuotaRedirect(Uri originalUri, Uri effectiveUri)
    {
        if (!originalUri.Host.Equals(effectiveUri.Host, StringComparison.OrdinalIgnoreCase)
            || !originalUri.Query.Equals(effectiveUri.Query, StringComparison.Ordinal))
            return false;

        var sameOrigin = originalUri.Scheme.Equals(effectiveUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && originalUri.Port == effectiveUri.Port;
        var secureUpgrade = originalUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && effectiveUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && originalUri.IsDefaultPort
            && effectiveUri.IsDefaultPort;
        if (!sameOrigin && !secureUpgrade) return false;

        var originalPath = originalUri.AbsolutePath;
        var effectivePath = effectiveUri.AbsolutePath;
        return originalPath.Equals(effectivePath, StringComparison.Ordinal)
            || (originalPath.EndsWith('/')
            ? originalPath[..^1].Equals(effectivePath, StringComparison.Ordinal)
            : (originalPath + "/").Equals(effectivePath, StringComparison.Ordinal));
    }

    /// <summary>尽力读取 NewAPI 公共状态，用于将额度值换算成站点显示的货币单位。</summary>
    private static async Task<JsonElement> TryReadNewApiStatusAsync(
        HttpClient client,
        ForwardApiSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings.BaseUrl, "/api/status"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return default;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var status = document.RootElement;
            return status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                    ? data.Clone()
                    : status.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>按候选字段名读取额度数字，兼容 JSON 数字和使用不变量格式的数字字符串。</summary>
    private static decimal? ReadQuotaNumber(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    /// <summary>按候选字段名读取 JSON 布尔值或布尔字符串。</summary>
    private static bool ReadQuotaBoolean(JsonElement element, params string[] names)
        => element.ValueKind == JsonValueKind.Object
            && names.Any(name => element.TryGetProperty(name, out var value)
                && (value.ValueKind == JsonValueKind.True
                    || value.ValueKind == JsonValueKind.String
                    && bool.TryParse(value.GetString(), out var parsed)
                    && parsed));

    /// <summary>按优先顺序返回第一个非空的字符串字段。</summary>
    private static string? ReadQuotaString(JsonElement element, params string[] names)
        => element.ValueKind == JsonValueKind.Object
            ? names.Select(name => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;

    /// <summary>从多个速率限制窗口中选取使用比例最高的一项作为当前有效额度。</summary>
    private static (decimal Total, decimal Used, decimal Remaining)? ReadTightestRateLimit(JsonElement limits)
    {
        var candidates = new List<(decimal Total, decimal Used, decimal Remaining, decimal Ratio)>();
        foreach (var limit in limits.EnumerateArray())
        {
            var total = ReadQuotaNumber(limit, "limit", "total");
            var used = ReadQuotaNumber(limit, "used");
            var remaining = ReadQuotaNumber(limit, "remaining", "available");
            if (total is null && used is not null && remaining is not null)
                total = used.Value + remaining.Value;
            if (total is not > 0m) continue;
            if (used is null && remaining is null) continue;
            used ??= remaining is not null ? Math.Max(0m, total.Value - remaining.Value) : 0m;
            remaining ??= Math.Max(0m, total.Value - used.Value);
            candidates.Add((total.Value, used.Value, remaining.Value, used.Value / total.Value));
        }

        if (candidates.Count == 0) return null;
        var tightest = candidates.OrderByDescending(candidate => candidate.Ratio).First();
        return (tightest.Total, tightest.Used, tightest.Remaining);
    }

    /// <summary>从账号凭据字段读取最近一次持久化的额度快照；缺失或损坏时返回空值。</summary>
    private static ApiKeyQuotaSnapshot? ReadQuotaSnapshot(Account account)
    {
        var json = GetCredentialField(account, "quotaSnapshot");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ApiKeyQuotaSnapshot>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>将密钥压缩为管理页面可识别但不能用于认证的显示掩码。</summary>
    private static string MaskSecret(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length <= 6 ? "••••••" : $"••••••{value[^4..]}";

    /// <summary>规范化并限制站点类型为 NewAPI、Sub2API 或 Custom。</summary>
    private static string? NormalizeSiteType(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "newapi" => "NewAPI",
            "sub2api" => "Sub2API",
            "custom" => "Custom",
            _ => null
        };

    /// <summary>将宿主提供的 JSON 请求体反序列化为指定管理端点输入类型。</summary>
    private static bool TryReadBody<T>(object? body, out T input) where T : class
    {
        try
        {
            if (body is JsonElement { ValueKind: JsonValueKind.Object } element
                && JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions) is { } value)
            {
                input = value;
                return true;
            }
        }
        catch (JsonException)
        {
            // The caller returns a concise validation response.
        }

        input = null!;
        return false;
    }

    /// <summary>校验并规范化高级参数为 JSON 对象；未提交新值时沿用账号原配置。</summary>
    private static bool TryReadExtraParams(
        JsonElement? requested,
        string fallback,
        out string value,
        out string error)
    {
        value = fallback;
        error = "extraParams 必须是 JSON 对象";
        if (requested is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;

        try
        {
            var json = element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText();
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            value = document.RootElement.GetRawText();
            return TryReadReplaceHeaders(JsonNode.Parse(value)!.AsObject(), out _, out error);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>复制账号自定义凭据字段，供更新模型或额度快照时保留其他字段。</summary>
    private static Dictionary<string, string?> CopyCredentialFields(Account account)
        => account.Credential is CustomCredential custom
            ? new Dictionary<string, string?>(custom.Fields, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>从自定义凭据中安全读取一个字段；不存在时返回空值。</summary>
    private static string? GetCredentialField(Account account, string name)
        => account.Credential is CustomCredential custom
            && custom.Fields.TryGetValue(name, out var value)
                ? value
                : null;

    /// <summary>账号新建/编辑端点接受的 JSON 字段；可空字段用于区分未提交和显式值。</summary>
    private sealed class AccountSaveInput
    {
        /// <summary>创建供宿主 JSON 请求体绑定使用的空输入对象。</summary>
        public AccountSaveInput() { }

        /// <summary>编辑时指定既有账号；为空时创建新账号。</summary>
        public string? Id { get; init; }
        /// <summary>管理页面展示的账号名称。</summary>
        public string? Label { get; init; }
        /// <summary>站点类型：NewAPI、Sub2API 或 Custom。</summary>
        public string? SiteType { get; init; }
        /// <summary>上游站点 Base URL。</summary>
        public string? BaseUrl { get; init; }
        /// <summary>上游 API Key；编辑时为空表示保留现有值。</summary>
        public string? ApiKey { get; init; }
        /// <summary>供站点签到登录使用的用户名。</summary>
        public string? Username { get; init; }
        /// <summary>供站点签到登录使用的密码；编辑时为空表示保留现有值。</summary>
        public string? Password { get; init; }
        /// <summary>参与同模型账号选择时的权重。</summary>
        public int? Weight { get; init; }
        /// <summary>该账号允许处理的端点路径。</summary>
        public string[]? Endpoints { get; init; }
        /// <summary>已选择并允许向下游公开的模型 ID。</summary>
        public string[]? Models { get; init; }
        /// <summary>本次发现得到的候选模型目录，用于校验所选模型。</summary>
        public string[]? AvailableModels { get; init; }
        /// <summary>是否在每日自动签到任务中处理该账号。</summary>
        public bool? AutoCheckIn { get; init; }
        /// <summary>是否启用账号路由。</summary>
        public bool? Enabled { get; init; }
        /// <summary>站点认证、额度或签到流程的 JSON 扩展参数。</summary>
        public JsonElement? ExtraParams { get; init; }
    }

    /// <summary>临时模型发现端点使用的站点连接参数。</summary>
    private sealed class ModelDiscoveryInput
    {
        /// <summary>创建供模型发现 JSON 请求体绑定使用的空输入对象。</summary>
        public ModelDiscoveryInput() { }

        /// <summary>可选的既有账号 ID，用于继承没有重新提交的设置。</summary>
        public string? Id { get; init; }
        /// <summary>站点适配类型。</summary>
        public string? SiteType { get; init; }
        /// <summary>待查询站点的 Base URL。</summary>
        public string? BaseUrl { get; init; }
        /// <summary>用于本次目录查询的 API Key。</summary>
        public string? ApiKey { get; init; }
        /// <summary>可选的模型 API 认证头等扩展参数。</summary>
        public JsonElement? ExtraParams { get; init; }
    }

    /// <summary>从上游解析并持久化的额度总量、用量、余额和单位。</summary>
    /// <param name="Total">可用额度总量；无限额或上游未提供时为空。</param>
    /// <param name="Used">已使用额度；上游未提供时为空。</param>
    /// <param name="Remaining">剩余额度；上游未提供时为空。</param>
    /// <param name="Unlimited">上游是否明确报告无限额。</param>
    /// <param name="Unit">额度展示单位，例如额度、美元或站点配置的货币符号。</param>
    /// <param name="UpdatedAt">生成该快照的时间。</param>
    private sealed record ApiKeyQuotaSnapshot(
        decimal? Total,
        decimal? Used,
        decimal? Remaining,
        bool Unlimited,
        string Unit,
        DateTimeOffset UpdatedAt);

    /// <summary>需要通过账号 ID 操作账号的管理端点输入。</summary>
    private sealed class AccountIdInput
    {
        /// <summary>创建供账号操作 JSON 请求体绑定使用的空输入对象。</summary>
        public AccountIdInput() { }

        /// <summary>要读取、更新或删除的宿主账号 ID。</summary>
        public string? Id { get; init; }
    }
}
