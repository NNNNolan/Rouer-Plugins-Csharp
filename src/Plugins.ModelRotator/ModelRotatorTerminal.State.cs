using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Plugins.ModelRotator;

public sealed partial class ModelRotatorTerminal
{
    private static string StateKey(string virtualModel, string model) => virtualModel + "\u001f" + model;
    private static string DisabledKey(string virtualModel, string model) => StateKey(virtualModel, model);

    private static void RemoveByPrefix<T>(Dictionary<string, T> map, string prefix)
    {
        foreach (var key in map.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            map.Remove(key);
    }

    /// <summary>还没从失败/超时里恢复（计数未清零）的目标。</summary>
    private static bool IsUnsettled(RotatorDocument doc, string virtualModel, string model) =>
        doc.State.Timeouts.ContainsKey(StateKey(virtualModel, model))
        || doc.State.Failures.ContainsKey(StateKey(virtualModel, model));

    internal static List<string> SelectCandidates(RotatorDocument doc, RotatorRoute route)
    {
        var healthy = route.Targets.Where(model => !doc.State.Disabled.ContainsKey(DisabledKey(route.VirtualModel, model))).ToList();
        if (healthy.Count == 0) return [];
        // 计数未清零的目标排到最后：刚失败/超时过的目标不该每次都被第一个撞，
        // 否则固定顺序 + sticky 会把整条链路反复拖进同一次超时。
        var ordered = healthy.OrderBy(model => IsUnsettled(doc, route.VirtualModel, model) ? 1 : 0).ToList();
        if (doc.Settings.ActivationPolicy == "prefer-highest") return ordered;
        if (doc.State.Active.TryGetValue(route.VirtualModel, out var active)
            && ordered.Contains(active, StringComparer.OrdinalIgnoreCase)
            && !IsUnsettled(doc, route.VirtualModel, active))
            return [active, .. ordered.Where(model => !model.Equals(active, StringComparison.OrdinalIgnoreCase))];
        return ordered;
    }

    private static string? SelectFirstTarget(RotatorDocument doc, RotatorRoute route) => SelectCandidates(doc, route).FirstOrDefault();

    // 已经超时过的目标不再给满额等待：上一次在配置值内都没等到响应头，再等同样久只会把整条链路拖长，
    // 给一次短复检窗口即可，让轮转尽快落到下一个候选。
    private const double RecentlyTimedOutWaitSeconds = 20;

    internal static TimeSpan ComputeFirstWait(RotatorDocument doc, RotatorRoute route, string model, TimeSpan remaining)
    {
        // 与 JS 对齐：自适应等待取 max(配置值, EMA 首字节×1.5)，上限为未公开设置的 firstWaitMaxSeconds（默认 80s）。
        var configured = doc.Settings.FirstWaitSeconds;
        var cap = Math.Max(configured, ClampSeconds(doc.Settings.FirstWaitMaxSeconds, 80, 10, 300));
        var predicted = doc.State.Latency.GetValueOrDefault(StateKey(route.VirtualModel, model));
        var waitSeconds = Math.Clamp(Math.Max(configured, predicted > 0 ? predicted * 1.5 / 1000.0 : configured), configured, cap);
        if (doc.State.Timeouts.GetValueOrDefault(StateKey(route.VirtualModel, model)) > 0)
            waitSeconds = Math.Max(10, Math.Min(waitSeconds, RecentlyTimedOutWaitSeconds));
        return Min(TimeSpan.FromSeconds(waitSeconds), remaining - TimeSpan.FromSeconds(5));
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private async Task RecordSuccessAsync(RotatorRoute route, string model, double firstByteMs, Usage? usage, CancellationToken cancellationToken)
    {
        var stateKey = StateKey(route.VirtualModel, model);
        var day = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mutated = await MutateDocumentAsync(doc =>
        {
            doc.State.Failures.Remove(stateKey);
            doc.State.Timeouts.Remove(stateKey);
            // 记录首字耗时（EMA 0.7/0.3，与 JS observeFirstByte 一致）：
            // 稳定值附近不再写回，避免每次成功都触发 CAS 冲突和文档重写。
            if (firstByteMs > 0)
            {
                var previous = doc.State.Latency.GetValueOrDefault(stateKey);
                var next = previous > 0
                    ? Math.Round(previous * 0.7 + firstByteMs * 0.3, MidpointRounding.AwayFromZero)
                    : Math.Max(1, Math.Round(firstByteMs, MidpointRounding.AwayFromZero));
                if (previous <= 0 || Math.Abs(next - previous) >= Math.Max(500, previous * 0.15))
                    doc.State.Latency[stateKey] = next;
            }
            var isFirst = !doc.State.Active.TryGetValue(route.VirtualModel, out var active);
            var activeHealthy = active is not null
                && !doc.State.Disabled.ContainsKey(DisabledKey(route.VirtualModel, active))
                && !IsUnsettled(doc, route.VirtualModel, active);
            if (doc.Settings.ActivationPolicy == "prefer-highest" || isFirst || !activeHealthy) doc.State.Active[route.VirtualModel] = model;
            // 请求数总是 +1：流式响应此刻还看不到 usage，但这一次成功请求必须计入，
            // 否则面板里的请求数会明显少于宿主日志。token 稍后由 RecordUsageAsync 补上。
            UpsertUsage(doc, day, model, 1, usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0);
            return true;
        }, cancellationToken);
        if (!mutated) await TryLogAsync("state.conflict", "成功状态和用量更新发生并发冲突", null, cancellationToken);
    }

    /// <summary>
    /// 流式响应结束后补记 token 用量：请求数在成功时已经记过，这里只累加 token，
    /// 避免同一次请求被算成两次。
    /// </summary>
    private async Task RecordUsageAsync(string model, Usage usage, CancellationToken cancellationToken)
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mutated = await MutateDocumentAsync(doc =>
        {
            UpsertUsage(doc, day, model, 0, usage.PromptTokens, usage.CompletionTokens);
            return true;
        }, cancellationToken);
        if (!mutated) await TryLogAsync("state.conflict", "流式用量补记发生并发冲突", null, cancellationToken);
    }

    private static void UpsertUsage(RotatorDocument doc, string day, string model, int requests, int prompt, int completion)
    {
        var index = doc.Usage.FindIndex(row => row.Day == day && row.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var row = doc.Usage[index];
            doc.Usage[index] = row with
            {
                Requests = row.Requests + requests,
                PromptTokens = row.PromptTokens + prompt,
                CompletionTokens = row.CompletionTokens + completion
            };
        }
        else if (requests > 0 || prompt > 0 || completion > 0)
        {
            doc.Usage.Add(new(day, model, requests, prompt, completion));
        }

        PruneUsage(doc);
    }

    private async Task RecordFailureAsync(RotatorRoute route, string model, bool isTimeout,
        CancellationToken cancellationToken, double? elapsedMs = null)
    {
        var stateKey = StateKey(route.VirtualModel, model);
        await MutateDocumentAsync(doc =>
        {
            var counts = isTimeout ? doc.State.Timeouts : doc.State.Failures;
            var next = counts.GetValueOrDefault(stateKey) + 1;
            counts[stateKey] = next;
            // 超时不再上调首字节观测值：观测值只来自成功请求，否则会形成
            // 超时→观测变大→等更久→更容易超时的正反馈（这一点故意偏离 JS observeFirstByte）。
            var threshold = isTimeout ? Math.Max(route.FailureThreshold * 2, 4) : route.FailureThreshold;
            if (next >= threshold)
            {
                // 达到阈值即禁用：删除计数（重开下一步从 1 数起），禁用时间用 ISO 8601（JS 兼容），
                // 并让出活跃位，让下一个健康目标接管。
                counts.Remove(stateKey);
                doc.State.Disabled[DisabledKey(route.VirtualModel, model)] = NowIsoTimestamp();
                if (doc.State.Active.TryGetValue(route.VirtualModel, out var active)
                    && active.Equals(model, StringComparison.OrdinalIgnoreCase)) doc.State.Active.Remove(route.VirtualModel);
            }
            return true;
        }, cancellationToken);
    }

    /// <summary>禁用时间戳，格式与 JS 版 new Date().toISOString() 一致，便于数据互相迁移。</summary>
    internal static string NowIsoTimestamp() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void PruneUsage(RotatorDocument doc)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        doc.Usage = doc.Usage.Where(row => StringComparer.Ordinal.Compare(row.Day, cutoff) >= 0)
            .OrderBy(row => row.Day, StringComparer.Ordinal).TakeLast(400).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions).Length;
        while (bytes > MaxDocBytes && doc.Usage.Count > 0)
        {
            doc.Usage.RemoveAt(0);
            doc.UsageTruncated = true;
            bytes = JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions).Length;
        }
    }

    private async Task TryLogAsync(string eventType, string message, string? traceId, CancellationToken cancellationToken)
    {
        try
        {
            await _services.Log.WriteAsync(new PluginLog
            {
                PluginKey = _services.PluginKey,
                Platform = Platform,
                EventType = eventType,
                Message = message.Length > 1000 ? message[..1000] : message,
                TraceId = traceId,
                Level = "Warning"
            }, cancellationToken);
        }
        catch { /* diagnostics must not change the model response */ }
    }

    private static bool TryReadUsageFromJson(JsonElement root, out Usage? usage)
    {
        usage = root.TryGetProperty("usage", out var node) ? ParseUsage(node) : null;
        return usage is not null;
    }

    private static Usage? ParseUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        static int Read(JsonElement element, string key, string alt)
        {
            if (element.TryGetProperty(key, out var value) && value.TryGetInt32(out var n)) return Math.Max(0, n);
            return element.TryGetProperty(alt, out value) && value.TryGetInt32(out n) ? Math.Max(0, n) : 0;
        }
        var prompt = Read(usage, "prompt_tokens", "input_tokens");
        var completion = Read(usage, "completion_tokens", "output_tokens");
        if (prompt == 0 && completion == 0) return null;
        return new Usage(prompt, completion, prompt + completion);
    }

    internal sealed record ProbeResult(
        string VirtualModel,
        string Model,
        bool Ok,
        bool Inconclusive,
        bool Recovered,
        int? StatusCode,
        int DurationMs,
        string? Error,
        bool TimedOut = false);
}
