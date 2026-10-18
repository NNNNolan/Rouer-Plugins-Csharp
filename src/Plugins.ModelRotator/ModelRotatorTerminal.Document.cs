using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Plugins.ModelRotator;

public sealed partial class ModelRotatorTerminal
{
    internal async Task<RotatorDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        Account? account;
        try
        {
            account = await _services.Accounts.GetAsync(ConfigId, cancellationToken);
        }
        catch
        {
            // A storage failure is not an empty configuration. Never create defaults over a read error.
            throw;
        }

        if (account is null) return EmptyDocument();
        if (!TryReadDocument(account, out var doc))
        {
            // 配置 JSON 损坏或账号放着别的东西：回退默认配置但仍绑定原账号（保存时经 CAS 覆盖回来）。
            // 不抛异常、不新建账号——否则插件启动失败、所有管理端点 5xx、用户连修复配置都做不到。
            doc = EmptyDocument();
            doc.StorageAccountId = account.Id;
            await TryLogDocumentFallback(account, cancellationToken);
        }
        else doc.StorageAccountId = account.Id;
        doc.CredentialVersion = account.CredentialVersion;
        return doc;
    }

    private async Task TryLogDocumentFallback(Account account, CancellationToken cancellationToken)
    {
        try
        {
            await _services.Log.WriteAsync(new PluginLog
            {
                PluginKey = _services.PluginKey,
                Platform = Platform,
                EventType = "config.parse_failed",
                Message = "配置账号缺少有效配置 JSON，已回退默认配置（保存时会覆盖回原账号）",
                AccountId = account.Id,
                Level = "Warning"
            }, cancellationToken);
        }
        catch
        {
            // 日志失败不影响读取流程。
        }
    }

    internal async Task<(bool Saved, RotatorDocument Document)> SaveDocumentAsync(
        RotatorDocument next,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = await ReadDocumentAsync(cancellationToken);
        if (expectedVersion.HasValue && expectedVersion.Value != current.CredentialVersion)
            return (false, current);

        var account = await _services.Accounts.GetAsync(current.StorageAccountId, cancellationToken);
        if (account is null)
        {
            if (current.StorageAccountId != ConfigId)
                return (false, current);
            account = new Account
            {
                Id = ConfigId,
                PluginKey = _services.PluginKey,
                Platform = Platform,
                Label = "模型轮转配置",
                Credential = ToCredential(next)
            };
            var created = await _services.Accounts.SaveAsync(account, cancellationToken);
            next.StorageAccountId = created.Id;
            next.CredentialVersion = created.CredentialVersion;
            return (true, next);
        }

        var credential = ToCredential(next);
        try
        {
            var updated = await _services.Accounts.CompareExchangeCredentialAsync(
                account.Id, account.CredentialVersion, credential, cancellationToken);
            if (updated is null) return (false, await ReadDocumentAsync(cancellationToken));
            next.StorageAccountId = updated.Id;
            next.CredentialVersion = updated.CredentialVersion;
            return (true, next);
        }
        catch (NotSupportedException)
        {
            // Compatibility fallback for a Contracts host without credential CAS; the local gate
            // prevents same-instance overwrites, while optimistic version checking detects stale UI edits.
            await _docMutationGate.WaitAsync(cancellationToken);
            try
            {
                var latest = await _services.Accounts.GetAsync(account.Id, cancellationToken);
                if (latest is null || latest.CredentialVersion != account.CredentialVersion)
                    return (false, await ReadDocumentAsync(cancellationToken));
                var saved = await _services.Accounts.SaveAsync(new Account
                {
                    Id = latest.Id,
                    PluginKey = latest.PluginKey,
                    Platform = latest.Platform,
                    Label = latest.Label,
                    Status = latest.Status,
                    ExpiresAt = latest.ExpiresAt,
                    Credential = credential
                }, cancellationToken);
                next.StorageAccountId = saved.Id;
                next.CredentialVersion = saved.CredentialVersion;
                return (true, next);
            }
            finally { _docMutationGate.Release(); }
        }
    }

    internal async Task<bool> MutateDocumentAsync(
        Func<RotatorDocument, bool> mutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await ReadDocumentAsync(cancellationToken);
            var next = CloneDocument(current);
            if (!mutation(next)) return true;
            var (saved, _) = await SaveDocumentAsync(next, current.CredentialVersion, cancellationToken);
            if (saved) return true;
        }
        return false;
    }

    internal static RotatorDocument CloneDocument(RotatorDocument doc)
        => JsonSerializer.Deserialize<RotatorDocument>(JsonSerializer.Serialize(doc, JsonOptions), JsonOptions)
           ?? EmptyDocument();

    internal static RotatorDocument EmptyDocument() => new()
    {
        Version = DocVersion,
        Settings = new RotatorSettings(),
        Virtuals = [],
        State = new RotatorState(),
        Usage = [],
        UsageTruncated = false
    };

    /// <summary>与 JS clampSeconds 一致：正值收敛进 [min,max]，非法/非正值用默认值。</summary>
    internal static int ClampSeconds(int value, int fallback, int min, int max)
        => value > 0 ? Math.Clamp(value, min, max) : fallback;

    internal static RotatorDocument NormalizeDocument(RotatorDocument? doc)
    {
        if (doc is null) return EmptyDocument();
        var normalized = new RotatorDocument
        {
            Version = DocVersion,
            Settings = doc.Settings ?? new RotatorSettings(),
            Virtuals = [],
            State = doc.State ?? new RotatorState(),
            Usage = [],
            UsageTruncated = doc.UsageTruncated
        };
        var settings = normalized.Settings;
        settings.BaseUrl = SanitizeBaseUrl(settings.BaseUrl);
        settings.ApiKey ??= "";
        settings.FirstWaitSeconds = ClampSeconds(settings.FirstWaitSeconds, 30, 10, 300);
        settings.FirstByteBudgetSeconds = ClampSeconds(settings.FirstByteBudgetSeconds, 180, 10, 600);
        settings.StreamIdleSeconds = ClampSeconds(settings.StreamIdleSeconds, 30, 5, 600);
        settings.ProbeIntervalMinutes = ClampSeconds(settings.ProbeIntervalMinutes, 5, 1, 60);
        settings.ProbeTimeoutSeconds = ClampSeconds(settings.ProbeTimeoutSeconds, 45, 5, 180);
        settings.ActivationPolicy = settings.ActivationPolicy == "prefer-highest" ? "prefer-highest" : "sticky";

        var seenRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in doc.Virtuals ?? [])
        {
            if (normalized.Virtuals.Count >= MaxVirtuals) break;
            var name = route.VirtualModel?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxModelLength || !seenRoutes.Add(name)) continue;
            var targets = (route.Targets ?? [])
                .Where(IsModelId)
                .Select(value => value.Trim())
                .Where(value => !Guid.TryParse(value, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTargets)
                .ToList();
            normalized.Virtuals.Add(new RotatorRoute
            {
                VirtualModel = name,
                Targets = targets,
                FailureThreshold = Math.Clamp(route.FailureThreshold, 1, 100),
                Enabled = route.Enabled,
                ProbeEndpoint = SupportedEndpoints.Contains(NormalizeEndpoint(route.ProbeEndpoint), StringComparer.OrdinalIgnoreCase)
                    ? NormalizeEndpoint(route.ProbeEndpoint) : "/v1/chat/completions"
            });
        }

        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        normalized.Usage = (doc.Usage ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(row.Day) && DateOnly.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day >= utcToday.AddDays(-30) && day <= utcToday)
            .GroupBy(row => (row.Day, row.Model), StringTupleComparer.Instance)
            .Select(group => new RotatorUsageRow(group.Key.Day, group.Key.Model,
                group.Sum(row => Math.Max(0, row.Requests)),
                group.Sum(row => Math.Max(0, row.PromptTokens)),
                group.Sum(row => Math.Max(0, row.CompletionTokens))))
            .OrderBy(row => row.Day, StringComparer.Ordinal)
            .TakeLast(400)
            .ToList();
        normalized.UsageTruncated |= (doc.Usage?.Count ?? 0) > normalized.Usage.Count;
        MigrateLegacyStateKeys(normalized);
        normalized.State.Failures = BoundedIntMap(normalized.State.Failures);
        normalized.State.Timeouts = BoundedIntMap(normalized.State.Timeouts);
        normalized.State.Latency = (normalized.State.Latency ?? new()).Where(pair => pair.Key.Length <= 500 && double.IsFinite(pair.Value) && pair.Value >= 0).Take(20_000).ToDictionary(StringComparer.Ordinal);
        normalized.State.Active = BoundedStringMap(normalized.State.Active);
        normalized.State.Disabled = BoundedStringMap(normalized.State.Disabled);
        return normalized;
    }

    /// <summary>
    /// 迁移 JS 版插件留下的状态键：失败/超时/首字/禁用用 "&lt;小写虚拟模型&gt;|&lt;实际模型&gt;"，
    /// 活跃位用 "active/&lt;小写虚拟模型&gt;"。虚拟模型名以当前配置的大小写为准，迁不动的旧键原样保留。
    /// </summary>
    private static void MigrateLegacyStateKeys(RotatorDocument doc)
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in doc.Virtuals) canonical[route.VirtualModel] = route.VirtualModel;
        MigrateLegacyCountedKeys(doc.State.Failures, canonical);
        MigrateLegacyCountedKeys(doc.State.Timeouts, canonical);
        MigrateLegacyCountedKeys(doc.State.Latency, canonical);
        MigrateLegacyDisabledKeys(doc.State.Disabled, canonical);
        if (doc.State.Active.Count == 0) return;
        foreach (var pair in doc.State.Active.ToArray())
        {
            var legacyVirtual = pair.Key.StartsWith("active/", StringComparison.OrdinalIgnoreCase)
                ? pair.Key["active/".Length..] : null;
            if (legacyVirtual is null || !canonical.TryGetValue(legacyVirtual, out var name)) continue;
            doc.State.Active.Remove(pair.Key);
            doc.State.Active[name] = pair.Value;
        }
    }

    private static void MigrateLegacyCountedKeys<TValue>(Dictionary<string, TValue> map, Dictionary<string, string> canonical)
    {
        if (map.Count == 0) return;
        var updates = new List<KeyValuePair<string, TValue>>();
        var removals = new List<string>();
        foreach (var pair in map)
        {
            if (!TrySplitLegacyStateKey(pair.Key, canonical, out var migrated)) continue;
            removals.Add(pair.Key);
            if (!map.ContainsKey(migrated)) updates.Add(new(migrated, pair.Value));
        }
        foreach (var pair in updates) map[pair.Key] = pair.Value;
        foreach (var key in removals) map.Remove(key);
    }

    private static void MigrateLegacyDisabledKeys(Dictionary<string, string> map, Dictionary<string, string> canonical)
    {
        if (map.Count == 0) return;
        // 先枚举收集，再统一应用：Dictionary 枚举期间移除元素会抛 InvalidOperationException。
        var changes = new List<(string OldKey, string NewKey, string Value)>();
        foreach (var pair in map)
        {
            var key = TrySplitLegacyStateKey(pair.Key, canonical, out var migrated) ? migrated : pair.Key;
            // 早期 C# 版本把禁用时间写成 unix 毫秒；统一成与 JS 相同的 ISO 8601。
            var value = long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs) && unixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                : pair.Value;
            if (key == pair.Key && value == pair.Value) continue;
            changes.Add((pair.Key, key, value));
        }
        if (changes.Count == 0) return;
        foreach (var change in changes) map.Remove(change.OldKey);
        foreach (var change in changes) map[change.NewKey] = change.Value;
    }

    private static bool TrySplitLegacyStateKey(string key, Dictionary<string, string> canonical, out string migrated)
    {
        migrated = key;
        if (key.Contains('\u001f')) return false;
        var separator = key.IndexOf('|');
        if (separator <= 0 || separator == key.Length - 1) return false;
        var virtualPart = key[..separator];
        var modelPart = key[(separator + 1)..];
        if (!canonical.TryGetValue(virtualPart, out var name)) return false;
        migrated = name + '\u001f' + modelPart;
        return true;
    }

    private static bool IsModelId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxModelLength; private static Dictionary<string, int> BoundedIntMap(Dictionary<string, int>? source) =>
        (source ?? new()).Where(pair => pair.Key.Length <= 500 && pair.Value >= 0).Take(20_000).ToDictionary(StringComparer.Ordinal);
    private static Dictionary<string, string> BoundedStringMap(Dictionary<string, string>? source) =>
        (source ?? new()).Where(pair => pair.Key.Length <= 500 && pair.Value.Length <= MaxModelLength).Take(20_000).ToDictionary(StringComparer.Ordinal);

    private sealed class StringTupleComparer : IEqualityComparer<(string Day, string Model)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Day, string Model) x, (string Day, string Model) y) => x.Day == y.Day && StringComparer.OrdinalIgnoreCase.Equals(x.Model, y.Model);
        public int GetHashCode((string Day, string Model) value) => HashCode.Combine(value.Day, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Model ?? ""));
    }
}

internal sealed class RotatorDocument
{
    public int Version { get; set; } = 3;
    public RotatorSettings Settings { get; set; } = new();
    public List<RotatorRoute> Virtuals { get; set; } = [];
    public RotatorState State { get; set; } = new();
    [JsonConverter(typeof(RotatorUsageListConverter))]
    public List<RotatorUsageRow> Usage { get; set; } = [];
    public bool UsageTruncated { get; set; }
    [JsonIgnore] public string StorageAccountId { get; set; } = ModelRotatorTerminal.ConfigId;
    [JsonIgnore] public long CredentialVersion { get; set; }
}

internal sealed class RotatorSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string ApiKey { get; set; } = "";
    public int FirstWaitSeconds { get; set; } = 30;
    /// <summary>自适应等待上限秒数（JS 未在 UI 公开的 firstWaitMaxSeconds，默认 80）。</summary>
    public int FirstWaitMaxSeconds { get; set; } = 80;
    public int FirstByteBudgetSeconds { get; set; } = 180;
    public int StreamIdleSeconds { get; set; } = 30;
    public int ProbeIntervalMinutes { get; set; } = 5;
    /// <summary>单个目标的探测等待上限秒数。免费模型首字节常超过 12 秒，太短会把慢但可用的目标判成异常。</summary>
    public int ProbeTimeoutSeconds { get; set; } = 45;
    public long LastProbeAt { get; set; }
    public string ActivationPolicy { get; set; } = "sticky";
}

internal sealed class RotatorRoute
{
    public string VirtualModel { get; set; } = "";
    public List<string> Targets { get; set; } = [];
    public int FailureThreshold { get; set; } = 3;
    public bool Enabled { get; set; } = true;
    public string ProbeEndpoint { get; set; } = "/v1/chat/completions";
}

internal sealed class RotatorState
{
    public Dictionary<string, int> Failures { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Timeouts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Latency { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Active { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Disabled { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record RotatorUsageRow(string Day, string Model, long Requests, long PromptTokens, long CompletionTokens);

internal sealed class RotatorUsageListConverter : JsonConverter<List<RotatorUsageRow>>
{
    public override List<RotatorUsageRow> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var rows = new List<RotatorUsageRow>();
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return rows; }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var values = new JsonElement[5];
                var count = 0;
                using var row = JsonDocument.ParseValue(ref reader);
                foreach (var item in row.RootElement.EnumerateArray())
                {
                    if (count >= values.Length) break;
                    values[count++] = item.Clone();
                }
                if (count == 5 && values[0].ValueKind == JsonValueKind.String && values[1].ValueKind == JsonValueKind.String)
                    rows.Add(new(values[0].GetString()!, values[1].GetString()!, AsLong(values[2]), AsLong(values[3]), AsLong(values[4])));
            }
            else reader.Skip();
        }
        return rows;
    }

    public override void Write(Utf8JsonWriter writer, List<RotatorUsageRow> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var row in value)
        {
            writer.WriteStartArray(); writer.WriteStringValue(row.Day); writer.WriteStringValue(row.Model);
            writer.WriteNumberValue(row.Requests); writer.WriteNumberValue(row.PromptTokens); writer.WriteNumberValue(row.CompletionTokens); writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static long AsLong(JsonElement element) => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) ? Math.Max(0, number) : 0;
}
