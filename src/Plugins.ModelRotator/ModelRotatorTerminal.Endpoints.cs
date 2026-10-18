using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.ModelRotator;

public sealed partial class ModelRotatorTerminal
{
    [PluginEndpoint("GET", "virtual")]
    public async Task<PluginResult> ListVirtualAsync(PluginHttpContext context)
    {
        var doc = await ReadDocumentAsync(context.CancellationToken);
        return context.Ok(new { virtuals = doc.Virtuals.Select(route => ProjectRoute(doc, route)).ToArray() });
    }

    [PluginEndpoint("GET", "settings")]
    public async Task<PluginResult> GetSettingsAsync(PluginHttpContext context)
    {
        var settings = (await ReadDocumentAsync(context.CancellationToken)).Settings;
        return context.Ok(new
        {
            baseUrl = settings.BaseUrl,
            hasApiKey = !string.IsNullOrEmpty(settings.ApiKey),
            apiKeyMasked = MaskKey(settings.ApiKey),
            // 明文回环 Key：它是 Router2API 自己的下游 Key、已经在本页保存过，掩码排障时看不到全值很痛苦。
            apiKey = settings.ApiKey,
            platformName = Platform,
            firstWaitSeconds = settings.FirstWaitSeconds,
            firstByteBudgetSeconds = settings.FirstByteBudgetSeconds,
            streamIdleSeconds = settings.StreamIdleSeconds,
            probeIntervalMinutes = settings.ProbeIntervalMinutes,
            probeTimeoutSeconds = settings.ProbeTimeoutSeconds,
            lastProbeAt = settings.LastProbeAt,
            activationPolicy = settings.ActivationPolicy
        });
    }

    [PluginEndpoint("GET", "usage/stats")]
    public async Task<PluginResult> GetUsageStatsAsync(PluginHttpContext context)
    {
        var doc = await ReadDocumentAsync(context.CancellationToken);
        var from = ParseQueryDate(context.Query.GetValueOrDefault("from")) ?? DateTimeOffset.UtcNow.AddDays(-7);
        var to = ParseQueryDate(context.Query.GetValueOrDefault("to")) ?? DateTimeOffset.UtcNow;
        if (from > to || to - from > TimeSpan.FromDays(366)) return context.BadRequest("统计时间范围无效");
        var rows = doc.Usage.Where(row => DateOnly.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day)
            && new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) < to
            && new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) > from).ToArray();
        var totals = rows.GroupBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                model = group.Key,
                requests = group.Sum(item => item.Requests),
                promptTokens = group.Sum(item => item.PromptTokens),
                completionTokens = group.Sum(item => item.CompletionTokens),
                totalTokens = group.Sum(item => item.PromptTokens + item.CompletionTokens)
            }).OrderByDescending(item => item.totalTokens).Take(50).ToArray();
        var series = rows.OrderBy(row => row.Day, StringComparer.Ordinal)
            .GroupBy(row => row.Day, StringComparer.Ordinal)
            .Select(group => new
            {
                day = group.Key,
                models = group.GroupBy(row => row.Model, StringComparer.OrdinalIgnoreCase).ToDictionary(
                    inner => inner.Key, inner => inner.Sum(item => item.PromptTokens + item.CompletionTokens))
            }).ToArray();
        return context.Ok(new
        {
            from,
            to,
            totals,
            series,
            retainedModels = rows.Select(row => row.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            totalsOverall = new
            {
                requests = totals.Sum(item => item.requests),
                promptTokens = totals.Sum(item => item.promptTokens),
                completionTokens = totals.Sum(item => item.completionTokens),
                totalTokens = totals.Sum(item => item.totalTokens)
            },
            truncated = doc.UsageTruncated,
            retentionDays = 31
        });
    }

    [PluginEndpoint("GET", "platform/models")]
    public async Task<PluginResult> GetPlatformModelsAsync(PluginHttpContext context)
    {
        var doc = await ReadDocumentAsync(context.CancellationToken);
        // /v1 受宿主下游密钥校验保护，没填密钥就发请求只会拿回一个无信息量的 401。
        if (string.IsNullOrEmpty(doc.Settings.ApiKey))
            return context.Ok(new
            {
                models = Array.Empty<object>(),
                detail = "尚未配置平台 API Key：请在「设置」里填写 Router2API 的访问密钥（管理后台的 API 密钥）后再读取目录"
            });
        var (status, ids, detail) = await FetchPlatformModelIdsAsync(doc, context.CancellationToken);
        if (detail is not null)
            // 目录读取一律回 200 并用 detail 说明原因：把上游 401/403 原样透传会让插件页看起来像
            // 管理会话失效，用户只看到“请求失败（401）”，看不到真正该做的是去设置里填密钥。
            return context.Ok(new { models = Array.Empty<object>(), detail });
        var selfVirtuals = doc.Virtuals.Select(route => route.VirtualModel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var models = ids.Select(id =>
        {
            // 目录 id 形如 platform/model；剥掉平台前缀后与本插件虚拟模型比对，
            // 避免把虚拟模型本身选成实际目标造成回环。
            var slash = id.IndexOf('/');
            var platform = slash > 0 ? id[..slash] : "";
            var bare = slash > 0 ? id[(slash + 1)..] : id;
            return new { id, name = bare, platform };
        }).Where(item => !selfVirtuals.Contains(item.id) && !selfVirtuals.Contains(item.name))
            .OrderBy(item => item.id, StringComparer.OrdinalIgnoreCase).ToArray();
        return context.Ok(new { models, platformName = Platform });
    }

    /// <summary>回环读取平台模型目录全量 id（platform/model 形式）；失败时用 detail 给出可执行原因。</summary>
    private async Task<(int Status, string[] Ids, string? Detail)> FetchPlatformModelIdsAsync(
        RotatorDocument doc,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _services.Http.CreateDirectClient(new PluginHttpClientOptions
            {
                AllowAutoRedirect = false,
                RequestTimeout = TimeSpan.FromSeconds(15)
            });
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(new Uri(doc.Settings.BaseUrl), "/v1/models"));
            SetApiKey(request, doc.Settings.ApiKey, "/v1/models");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return (status, [], status is 401 or 403
                    ? $"平台拒绝了当前密钥（HTTP {status}）：请在「设置」中确认 API Key 是否正确"
                    : $"平台返回 HTTP {status}，请检查回环地址与平台状态");
            }
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (200, [], "平台模型目录格式无效（缺少 data 数组）");
            var ids = data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var value) ? value.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray();
            return (200, ids, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or JsonException or ArgumentException)
        {
            return (0, [], SafeError(exception.Message));
        }
    }

    /// <summary>把裸模型名补成 platform/model（目录里唯一命中才补），并返回仍无法判定平台的裸名。</summary>
    private async Task<(List<string> Targets, string[] Unresolved)> ResolveTargetsAsync(
        List<string> input,
        CancellationToken cancellationToken)
    {
        if (!input.Any(model => !model.Contains('/', StringComparison.Ordinal))) return (input, []);
        var doc = await ReadDocumentAsync(cancellationToken);
        var (status, ids, _) = await FetchPlatformModelIdsAsync(doc, cancellationToken);
        // 目录读不到时不猜：保持用户原样输入，让面板自己提示，避免把配置改成另一个模型。
        if (status != 200 || ids.Length == 0)
            return (input, input.Where(model => !model.Contains('/', StringComparison.Ordinal)).ToArray());
        var byBare = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var slash = id.IndexOf('/');
            if (slash <= 0) continue;
            var bare = id[(slash + 1)..];
            if (!byBare.TryGetValue(bare, out var candidates)) byBare[bare] = candidates = [];
            candidates.Add(id);
        }
        var targets = input.Select(model => model.Contains('/', StringComparison.Ordinal)
            ? model
            : byBare.TryGetValue(model, out var candidates) && candidates.Count == 1 ? candidates[0] : model)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (targets, targets.Where(model => !model.Contains('/', StringComparison.Ordinal)).ToArray());
    }

    [PluginEndpoint("POST", "virtual/save")]
    public async Task<PluginResult> SaveVirtualAsync(PluginHttpContext context)
    {
        if (!TryReadBody<VirtualInput>(context.Body, out var input)) return context.BadRequest("虚拟模型配置无效");
        var name = input.VirtualModel?.Trim();
        var inputTargets = (input.Targets ?? []).Select(value => value?.Trim()).Where(IsModelId).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (inputTargets.Count > MaxTargets) return context.BadRequest($"实际模型数量超过上限（{MaxTargets}）");
        var (targets, unresolved) = await ResolveTargetsAsync(inputTargets, context.CancellationToken);
        if (targets.Count == 0) return context.BadRequest("实际模型列表无效");
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxModelLength)
            return context.BadRequest("虚拟模型名称或实际模型列表无效");
        if (!SupportedEndpoints.Contains(NormalizeEndpoint(input.ProbeEndpoint), StringComparer.OrdinalIgnoreCase))
            return context.BadRequest("探测端点不受支持");
        var overLimit = false;
        var saved = await MutateDocumentAsync(doc =>
        {
            var existing = doc.Virtuals.FirstOrDefault(item => item.VirtualModel.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                if (doc.Virtuals.Count >= MaxVirtuals) { overLimit = true; return false; }
                existing = new RotatorRoute { VirtualModel = name };
                doc.Virtuals.Add(existing);
            }
            var targetsChanged = !existing.Targets.SequenceEqual(targets, StringComparer.OrdinalIgnoreCase);
            existing.Targets = targets;
            existing.FailureThreshold = Math.Clamp(input.FailureThreshold ?? 3, 1, 100);
            existing.Enabled = input.Enabled ?? true;
            existing.ProbeEndpoint = NormalizeEndpoint(input.ProbeEndpoint);
            // 状态键一律以已存在路由的规范名（也是存储时的大小写）算，输入大小写不同也不会留下孤儿键。
            var canonical = existing.VirtualModel;
            if (targetsChanged) doc.State.Active.Remove(canonical);
            foreach (var removed in doc.State.Disabled.Keys.Where(key => key.StartsWith(canonical + "\u001f", StringComparison.OrdinalIgnoreCase)
                && !targets.Contains(key[(canonical.Length + 1)..], StringComparer.OrdinalIgnoreCase)).ToArray()) doc.State.Disabled.Remove(removed);
            return true;
        }, context.CancellationToken);
        if (overLimit) return context.BadRequest($"虚拟模型数量已达上限（{MaxVirtuals}）");
        if (!saved) return context.Json(409, new { error = "配置发生并发修改，请刷新后重试" });
        _services.Models.Invalidate(Platform);
        // unresolved 里是回环目录里找不到（或重名无法判定）平台前缀的裸模型名，原样保存但明确告知。
        return context.Ok(new { saved = true, callModel = Platform + "/" + name, unresolved });
    }

    [PluginEndpoint("POST", "virtual/toggle")]
    public async Task<PluginResult> ToggleVirtualAsync(PluginHttpContext context)
    {
        if (!TryReadBody<ToggleInput>(context.Body, out var input) || string.IsNullOrWhiteSpace(input.VirtualModel))
            return context.BadRequest("虚拟模型名称无效");
        var changed = false;
        var saved = await MutateDocumentAsync(doc =>
        {
            var route = doc.Virtuals.FirstOrDefault(item => item.VirtualModel.Equals(input.VirtualModel, StringComparison.OrdinalIgnoreCase));
            if (route is null) return false;
            changed = route.Enabled != input.Enabled;
            route.Enabled = input.Enabled;
            return changed;
        }, context.CancellationToken);
        if (!saved) return context.Json(409, new { error = "配置发生并发修改，请重试" });
        if (!changed) return context.Json(404, new { error = "虚拟模型不存在或状态未改变" });
        _services.Models.Invalidate(Platform);
        return context.Ok(new { saved = true });
    }

    [PluginEndpoint("POST", "virtual/delete")]
    public async Task<PluginResult> DeleteVirtualAsync(PluginHttpContext context)
    {
        if (!TryReadBody<NameInput>(context.Body, out var input) || string.IsNullOrWhiteSpace(input.VirtualModel))
            return context.BadRequest("虚拟模型名称无效");
        var deleted = false;
        var saved = await MutateDocumentAsync(doc =>
        {
            var route = doc.Virtuals.FirstOrDefault(item => item.VirtualModel.Equals(input.VirtualModel, StringComparison.OrdinalIgnoreCase));
            if (route is null) return false;
            doc.Virtuals.Remove(route);
            doc.State.Active.Remove(route.VirtualModel);
            var prefix = route.VirtualModel + "\u001f";
            RemoveByPrefix(doc.State.Disabled, prefix);
            RemoveByPrefix(doc.State.Failures, prefix);
            RemoveByPrefix(doc.State.Timeouts, prefix);
            RemoveByPrefix(doc.State.Latency, prefix);
            deleted = true;
            return true;
        }, context.CancellationToken);
        if (!saved) return context.Json(409, new { error = "配置发生并发修改，请重试" });
        if (!deleted) return context.Json(404, new { error = "虚拟模型不存在" });
        _services.Models.Invalidate(Platform);
        return context.Ok(new { deleted = true });
    }

    [PluginEndpoint("POST", "virtual/activate")]
    public async Task<PluginResult> ActivateVirtualAsync(PluginHttpContext context)
    {
        if (!TryReadBody<ActivateInput>(context.Body, out var input) || string.IsNullOrWhiteSpace(input.VirtualModel))
            return context.BadRequest("虚拟模型名称无效");
        var notFound = false;
        var invalidTarget = false;
        var saved = await MutateDocumentAsync(doc =>
        {
            var route = doc.Virtuals.FirstOrDefault(item => item.VirtualModel.Equals(input.VirtualModel, StringComparison.OrdinalIgnoreCase));
            if (route is null) { notFound = true; return false; }
            var model = input.Model?.Trim();
            if (!string.IsNullOrEmpty(model) && !route.Targets.Contains(model, StringComparer.OrdinalIgnoreCase))
            { invalidTarget = true; return false; }
            if (string.IsNullOrEmpty(model)) doc.State.Active.Remove(route.VirtualModel);
            else
            {
                doc.State.Active[route.VirtualModel] = model;
                doc.State.Disabled.Remove(DisabledKey(route.VirtualModel, model));
                doc.State.Failures.Remove(StateKey(route.VirtualModel, model));
                doc.State.Timeouts.Remove(StateKey(route.VirtualModel, model));
            }
            return true;
        }, context.CancellationToken);
        if (!saved) return context.Json(409, new { error = "配置发生并发修改，请重试" });
        if (notFound) return context.Json(404, new { error = "虚拟模型不存在" });
        if (invalidTarget) return context.BadRequest("该实际模型不属于此虚拟模型");
        return context.Ok(new { saved = true });
    }

    [PluginEndpoint("POST", "settings/save")]
    public async Task<PluginResult> SaveSettingsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<SettingsInput>(context.Body, out var input)) return context.BadRequest("平台设置无效");
        if (!TryNormalizeBaseUrl(input.BaseUrl, out var baseUrl, out var error)) return context.BadRequest(error);
        if (input.FirstWaitSeconds is < 10 or > 300 || input.FirstByteBudgetSeconds is < 10 or > 600
            || input.StreamIdleSeconds is < 5 or > 600 || input.ProbeIntervalMinutes is < 1 or > 60
            || input.ProbeTimeoutSeconds is < 5 or > 180)
            return context.BadRequest("等待、预算或探测间隔超出允许范围");
        if (input.ActivationPolicy is not ("sticky" or "prefer-highest")) return context.BadRequest("活跃位切换策略无效");

        var current = await ReadDocumentAsync(context.CancellationToken);
        var apiKey = input.ClearApiKey ? "" : input.ApiKey?.Trim() ?? current.Settings.ApiKey;
        var saved = await MutateDocumentAsync(doc =>
        {
            doc.Settings.BaseUrl = baseUrl;
            doc.Settings.ApiKey = apiKey;
            doc.Settings.FirstWaitSeconds = input.FirstWaitSeconds;
            doc.Settings.FirstByteBudgetSeconds = input.FirstByteBudgetSeconds;
            doc.Settings.StreamIdleSeconds = input.StreamIdleSeconds;
            doc.Settings.ProbeIntervalMinutes = input.ProbeIntervalMinutes;
            doc.Settings.ProbeTimeoutSeconds = input.ProbeTimeoutSeconds;
            doc.Settings.ActivationPolicy = input.ActivationPolicy;
            return true;
        }, context.CancellationToken);
        if (!saved) return context.Json(409, new { error = "配置发生并发修改，请刷新后重试" });
        return context.Ok(new { saved = true });
    }

    [PluginEndpoint("POST", "probe/run")]
    public async Task<PluginResult> RunProbeEndpointAsync(PluginHttpContext context)
    {
        if (!TryReadBody<ProbeInput>(context.Body, out var input)) input = new ProbeInput();
        var doc = await ReadDocumentAsync(context.CancellationToken);
        var selected = (input.Targets ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = doc.Virtuals.SelectMany(route => route.Targets.Select(model => (Route: route, Model: model)))
            .Where(item => selected.Count == 0 || selected.Contains(item.Model))
            .Take(200).ToArray();
        var timeoutSeconds = Math.Clamp(doc.Settings.ProbeTimeoutSeconds, 5, 180);
        const int concurrency = 4;
        var batches = Math.Max(1, (int)Math.Ceiling(targets.Length / (double)concurrency));
        var budgetSeconds = Math.Clamp(batches * timeoutSeconds + 15, 20, 300);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
        // 并发 4 而不是 8：这些免费上游被自己连续探测打爆过，批量越小越不容易互相拖慢。
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(budget.Token);
            try { return await ProbeOneAsync(target.Route, target.Model, doc.Settings, budget.Token); }
            finally { gate.Release(); }
        }).ToArray();
        List<ProbeResult> results;
        try
        {
            results = (await Task.WhenAll(tasks)).Where(item => item is not null).Select(item => item!).ToList();
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            results = tasks.Where(task => task.IsCompletedSuccessfully && task.Result is not null).Select(task => task.Result).ToList()!;
        }
        await TrySetLastProbeAtAsync(context.CancellationToken);
        return context.Ok(new
        {
            total = results.Count,
            healthy = results.Count(item => item.Ok && !item.Inconclusive),
            failed = results.Count(item => item.Ok is false && !item.Inconclusive),
            inconclusive = results.Count(item => item.Inconclusive),
            timeout = results.Count(item => item.TimedOut),
            recovered = results.Count(item => item.Recovered),
            remaining = Math.Max(0, targets.Length - results.Count),
            results = results.Select(item => new
            {
                virtualModel = item.VirtualModel,
                model = item.Model,
                ok = item.Ok,
                inconclusive = item.Inconclusive,
                timedOut = item.TimedOut,
                recovered = item.Recovered,
                status = item.StatusCode,
                elapsedMs = item.DurationMs,
                slow = item.DurationMs >= 8000,
                message = item.Error
            }).ToArray()
        });
    }

    private async Task<PluginResult> RunScheduledProbeAsync(PluginScheduledTaskContext context)
    {
        var doc = await ReadDocumentAsync(context.CancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (doc.Settings.LastProbeAt > 0
            && TimeSpan.FromMilliseconds(now - doc.Settings.LastProbeAt) < TimeSpan.FromMinutes(doc.Settings.ProbeIntervalMinutes))
            return new PluginResult { StatusCode = 204 };
        // 后台探测不只看被禁用的目标：失败/超时计数未清零但还没禁用的目标同样沉在候选队尾，
        // 也需要靠探测成功来清计数，否则永远回不到轮转前排。
        var targets = doc.Virtuals.SelectMany(route => route.Targets.Select(model => (Route: route, Model: model)))
            .Where(item => doc.State.Disabled.ContainsKey(DisabledKey(item.Route.VirtualModel, item.Model))
                || item.Route.Enabled && IsUnsettled(doc, item.Route.VirtualModel, item.Model))
            .Take(200).ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(240));
        using var gate = new SemaphoreSlim(8, 8);
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(timeout.Token);
            try { return await ProbeOneAsync(target.Route, target.Model, doc.Settings, timeout.Token); }
            finally { gate.Release(); }
        }).ToArray();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        await TrySetLastProbeAtAsync(context.CancellationToken);
        await TryLogAsync("probe.scheduled", $"后台探测完成：{tasks.Count(task => task.IsCompletedSuccessfully && task.Result.Ok)} 成功，共 {targets.Length} 个目标", null, context.CancellationToken);
        return new PluginResult { StatusCode = 204 };
    }

    private async Task<ProbeResult> ProbeOneAsync(RotatorRoute route, string model, RotatorSettings settings, CancellationToken cancellationToken)
    {
        var key = DisabledKey(route.VirtualModel, model);
        var stateKey = StateKey(route.VirtualModel, model);
        var before = await ReadDocumentAsync(cancellationToken);
        var priorDisabled = before.State.Disabled.GetValueOrDefault(key);
        var started = Stopwatch.StartNew();
        try
        {
            var uri = BuildUri(new Uri(settings.BaseUrl), NormalizeEndpoint(route.ProbeEndpoint));
            var timeoutSeconds = Math.Clamp(settings.ProbeTimeoutSeconds, 5, 180);
            using var client = _services.Http.CreateDirectClient(new PluginHttpClientOptions
            {
                AllowAutoRedirect = false,
                RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds + 10)
            });
            using var request = CreateProbeRequest(uri, route.ProbeEndpoint, model);
            SetApiKey(request, settings.ApiKey, route.ProbeEndpoint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var good = response.IsSuccessStatusCode;
            // 上游答了错误（如推理占满额度返回 finish_reason=length 的 502）：这不是健康问题，
            // 但也不足以判定可用，所以记成 ok=true 但「结果不确定」，不恢复、不清计数（与 JS 一致）。
            var inconclusive = !good && IsChatLikeEndpoint(route.ProbeEndpoint) && AnsweredButEmptyRegex.IsMatch(body);
            if (inconclusive) good = true;
            var recovered = false;
            // 探测成功即可清零失败/超时计数：不只限于已被禁用的目标。否则一个只是抖动过几次、
            // 没到禁用阈值的目标，计数永远没有出口（真实请求轮不到它成功），会被 SelectCandidates
            // 永久沉底，表现为「轮转一直不取第一个」。
            if (good && !inconclusive
                && (priorDisabled is not null || before.State.Failures.ContainsKey(stateKey) || before.State.Timeouts.ContainsKey(stateKey)))
            {
                var priorFailures = before.State.Failures.GetValueOrDefault(stateKey);
                var priorTimeouts = before.State.Timeouts.GetValueOrDefault(stateKey);
                recovered = await MutateDocumentAsync(doc =>
                {
                    // 旧探测结果不能清除探测期间真实请求产生的新故障状态（与 JS 的 stale 检查一致）。
                    if (doc.State.Failures.GetValueOrDefault(stateKey) != priorFailures
                        || doc.State.Timeouts.GetValueOrDefault(stateKey) != priorTimeouts) return false;
                    if (priorDisabled is not null
                        && (!doc.State.Disabled.TryGetValue(key, out var current) || current != priorDisabled)) return false;
                    doc.State.Disabled.Remove(key);
                    doc.State.Failures.Remove(stateKey);
                    doc.State.Timeouts.Remove(stateKey);
                    return true;
                }, cancellationToken);
                if (recovered) _services.Models.Invalidate(Platform);
            }
            return new(route.VirtualModel, model, good, inconclusive, recovered, status,
                (int)started.ElapsedMilliseconds, good ? null : DescribeStatus(status));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外层预算取消：让批量逻辑收尾，不要伪装成这个目标探测失败。
            throw;
        }
        catch (OperationCanceledException)
        {
            var seconds = Math.Clamp(settings.ProbeTimeoutSeconds, 5, 180);
            return new(route.VirtualModel, model, false, false, false, null,
                (int)started.ElapsedMilliseconds, $"探测超时（{seconds} 秒内没有响应）", TimedOut: true);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException or ArgumentException or UriFormatException
            or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            // 探测是辅助动作：任何可预期异常都降级成单个目标失败结果，不允许弄炸整批 Task.WhenAll。
            return new(route.VirtualModel, model, false, false, false, null, (int)started.ElapsedMilliseconds, SafeError(exception.Message));
        }
    }

    private static HttpRequestMessage CreateProbeRequest(Uri uri, string endpoint, string model)
    {
        var normalized = NormalizeEndpoint(endpoint);
        var body = normalized switch
        {
            "/v1/embeddings" => new { model, input = "health probe" },
            "/v1/completions" => new { model, prompt = "health probe", max_tokens = 1 },
            "/v1/responses" => new { model, input = "health probe", max_output_tokens = 1 },
            "/v1/messages" => (object)new { model, max_tokens = 1, messages = new[] { new { role = "user", content = "health probe" } } },
            _ => new { model, max_tokens = 1, messages = new[] { new { role = "user", content = "health probe" } } }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        if (normalized == "/v1/messages") request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return request;
    }

    /// <summary>「上游其实答了，只是正文被截断」——按客户端 max_tokens 预算问题处理（与 JS 的 ANSWERED_BUT_EMPTY 对齐）。</summary>
    internal static readonly System.Text.RegularExpressions.Regex AnsweredButEmptyRegex =
        new("""no answer content or tool calls|finish_reason["']?\s*[:=]\s*["']?length""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>宿主在多平台下无法把裸模型名解析到平台：目标少了 platform/ 前缀，属于配置错误而不是模型故障。</summary>
    internal static readonly System.Text.RegularExpressions.Regex UnroutableModelRegex =
        new("model must use the platform/model format",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>「答了但没正文」只对 chat 类端点有意义，embeddings/responses 不适用（与 JS CHAT_LIKE_ENDPOINTS 对齐）。</summary>
    internal static bool IsChatLikeEndpoint(string endpoint) => NormalizeEndpoint(endpoint) is
        "/v1/chat/completions" or "/v1/completions" or "/v1/messages";

    private async Task<bool> TrySetLastProbeAtAsync(CancellationToken cancellationToken)
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return await MutateDocumentAsync(doc => { doc.Settings.LastProbeAt = time; return true; }, cancellationToken);
    }

    private static bool IsLocalLoopback(Uri uri) => uri.IsLoopback
        || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);

    private static void SetApiKey(HttpRequestMessage request, string apiKey, string endpoint)
    {
        if (string.IsNullOrEmpty(apiKey)) return;
        if (NormalizeEndpoint(endpoint).Equals("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static object ProjectRoute(RotatorDocument doc, RotatorRoute route)
    {
        var active = doc.State.Active.GetValueOrDefault(route.VirtualModel);
        // 下游必须用 platform/model 形式调用；插件自身收到的是去掉前缀后的裸名。
        var callModel = Platform + "/" + route.VirtualModel;
        var targets = route.Targets.Select(model =>
        {
            var stateKey = StateKey(route.VirtualModel, model);
            var disabled = doc.State.Disabled.ContainsKey(DisabledKey(route.VirtualModel, model));
            return new
            {
                model,
                isActive = string.Equals(active, model, StringComparison.OrdinalIgnoreCase),
                disabled,
                disabledSince = disabled ? doc.State.Disabled.GetValueOrDefault(DisabledKey(route.VirtualModel, model)) : "",
                failures = doc.State.Failures.GetValueOrDefault(stateKey),
                timeouts = doc.State.Timeouts.GetValueOrDefault(stateKey),
                firstByteMs = doc.State.Latency.GetValueOrDefault(stateKey),
                waitSeconds = doc.Settings.FirstWaitSeconds
            };
        }).ToArray();
        // 未加前缀的目标会在回环转发时被宿主以 "model must use the platform/model format" 拒绝，
        // 每次调用都会白跑一轮，所以提前在面板上标出来。
        var bareTargets = route.Targets.Where(model => !model.Contains('/', StringComparison.Ordinal)).ToArray();
        return new
        {
            route.VirtualModel,
            route.Enabled,
            route.FailureThreshold,
            route.ProbeEndpoint,
            activeModel = active,
            callModel,
            bareTargets,
            targets
        };
    }

    private static DateTimeOffset? ParseQueryDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : null;

    private static string MaskKey(string value) => string.IsNullOrEmpty(value) ? "" : value.Length <= 4 ? "••••" : new string('•', Math.Min(12, value.Length - 4)) + value[^4..];

    private static bool TryReadBody<T>(object? body, out T value)
    {
        try
        {
            var element = body switch
            {
                JsonElement json => json,
                string text => JsonDocument.Parse(text).RootElement.Clone(),
                null => default,
                _ => JsonSerializer.SerializeToElement(body, JsonOptions)
            };
            value = element.ValueKind == JsonValueKind.Object ? element.Deserialize<T>(JsonOptions)! : default!;
            return value is not null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        { value = default!; return false; }
    }

    private sealed class VirtualInput
    {
        public string? VirtualModel { get; init; }
        public string?[]? Targets { get; init; }
        public int? FailureThreshold { get; init; }
        public bool? Enabled { get; init; }
        public string? ProbeEndpoint { get; init; }
    }
    private sealed class ToggleInput { public string? VirtualModel { get; init; } public bool Enabled { get; init; } }
    private sealed class NameInput { public string? VirtualModel { get; init; } }
    private sealed class ActivateInput { public string? VirtualModel { get; init; } public string? Model { get; init; } }
    private sealed class SettingsInput
    {
        public string? BaseUrl { get; init; }
        public string? ApiKey { get; init; }
        public bool ClearApiKey { get; init; }
        public int FirstWaitSeconds { get; init; } = 30;
        public int FirstByteBudgetSeconds { get; init; } = 180;
        public int StreamIdleSeconds { get; init; } = 30;
        public int ProbeIntervalMinutes { get; init; } = 5;
        public int ProbeTimeoutSeconds { get; init; } = 45;
        public string ActivationPolicy { get; init; } = "sticky";
    }
    private sealed class ProbeInput { public string[]? Targets { get; init; } }
}
