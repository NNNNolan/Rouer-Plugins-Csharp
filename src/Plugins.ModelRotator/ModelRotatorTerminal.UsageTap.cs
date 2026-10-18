using System.Text;
using System.Text.Json;
using Router.Contracts.Domain;

namespace Plugins.ModelRotator;

public sealed partial class ModelRotatorTerminal
{
    /// <summary>
    /// 从上游响应流里抓 usage：流式响应最后一个分块通常带 usage（OpenAI 的
    /// <c>"usage":{...}</c> 分块、Anthropic 的 message_start/message_delta），
    /// 但插件必须原样转发字节，所以只做旁路嗅探，不改变任何字节和分块边界。
    /// 抓不到就保持 null，由调用方决定是否仍然计一次请求。
    /// </summary>
    internal sealed class StreamUsageTap
    {
        // 单行超过这个长度还没遇到换行，说明不是「一行一个事件」的流（例如整体 JSON），
        // 直接放弃这一行，避免把一个几 MB 的响应全塞进内存。
        private const int MaxPartialLineBytes = 512 * 1024;
        private const string UsageKey = "\"usage\"";
        private byte[] _partial = new byte[1024];
        private int _length;
        private int _prompt;
        private int _completion;

        public Usage? Usage => _prompt == 0 && _completion == 0 ? null : new Usage(_prompt, _completion, _prompt + _completion);

        public void Feed(ReadOnlySpan<byte> chunk)
        {
            foreach (var current in chunk)
            {
                if (current == (byte)'\n')
                {
                    if (_length > 0) FeedLine(System.Text.Encoding.UTF8.GetString(_partial, 0, _length));
                    _length = 0;
                    continue;
                }

                if (_length >= MaxPartialLineBytes)
                {
                    // 超长行（整段 JSON 没有换行）：丢弃这一行，继续转发后续字节。
                    _length = 0;
                    continue;
                }

                if (_length == _partial.Length) Array.Resize(ref _partial, Math.Min(MaxPartialLineBytes, _partial.Length * 2));
                _partial[_length++] = current;
            }
        }

        private void FeedLine(string line)
        {
            var index = 0;
            while ((index = line.IndexOf(UsageKey, index, StringComparison.Ordinal)) >= 0)
            {
                var cursor = index + UsageKey.Length;
                while (cursor < line.Length && char.IsWhiteSpace(line[cursor])) cursor++;
                if (cursor < line.Length && line[cursor] == ':') cursor++;
                while (cursor < line.Length && char.IsWhiteSpace(line[cursor])) cursor++;
                if (cursor >= line.Length || line[cursor] != '{')
                {
                    // "usage":null 之类的占位：跳过这个 key 继续找下一个。
                    index = cursor;
                    continue;
                }

                var end = FindObjectEnd(line, cursor);
                if (end < 0)
                {
                    index = cursor + 1;
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line[cursor..(end + 1)]);
                    if (ParseUsage(document.RootElement) is { } usage)
                    {
                        // Anthropic 的输入 token 在 message_start、输出 token 在 message_delta，
                        // 两次上报都是累加值，取各字段最大值即可同时兼容两种协议。
                        _prompt = Math.Max(_prompt, usage.PromptTokens);
                        _completion = Math.Max(_completion, usage.CompletionTokens);
                    }
                }
                catch (JsonException)
                {
                    // 半个对象或非 JSON 片段：忽略，不影响转发。
                }

                index = end + 1;
            }
        }

        private static int FindObjectEnd(string text, int start)
        {
            var depth = 0;
            var inString = false;
            for (var index = start; index < text.Length; index++)
            {
                var current = text[index];
                if (inString)
                {
                    if (current == '\\') index++;
                    else if (current == '"') inString = false;
                    continue;
                }

                switch (current)
                {
                    case '"': inString = true; break;
                    case '{': depth++; break;
                    case '}':
                        depth--;
                        if (depth == 0) return index;
                        break;
                }
            }

            return -1;
        }
    }
}
