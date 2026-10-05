using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// MyMemory 免密钥后端：<c>api.mymemory.translated.net/get?q=...&amp;langpair={src}|{dst}</c>。
    ///
    /// 语言差异：MyMemory 的 <c>langpair</c> 必须给两个明确的语种，没有 auto，
    /// 所以 source 为 auto（或为空）时退化为 en；两端都用
    /// <see cref="BackendHelpers.NormalizeLanguage"/> 归一化成两字母代码。
    ///
    /// 两个必须处理的坑（都不是异常，而是"HTTP 200 + 正文里报错"）：
    /// 1) 每日免费额度用尽时返回 <c>responseStatus != 200</c>（常见 403），
    ///    <c>quotaFinished=true</c>，细节在 <c>responseDetails</c> 里。这里必须读正文判断，
    ///    否则会把一句 "MYMEMORY WARNING..." 当成译文写进 Emby。
    /// 2) 单次查询有 500 字符上限（超了返回 QUERY LENGTH LIMIT EXCEEDED）。单集简介经常
    ///    超过它，所以长文本按字符数切块（优先在句末/空白处切），逐块翻译后再拼起来；
    ///    每个请求仍带同样的 langpair，条数/顺序与输入保持一一对应。
    /// </summary>
    public sealed class MyMemoryFreeBackend : BackendBase
    {
        private const string Endpoint = "https://api.mymemory.translated.net/get";

        /// <summary>MyMemory 的硬上限是 500 字符，留一点余量避免边界判断差异。</summary>
        private const int MaxQueryChars = 480;

        private readonly FreeBackendSettings _settings;

        public MyMemoryFreeBackend(BackendContext context)
            : base(context)
        {
            this._settings = context.Configuration.Free ?? new FreeBackendSettings();
        }

        public override TranslationBackend Kind
        {
            get { return TranslationBackend.MyMemoryFree; }
        }

        /// <summary>免费接口没有可靠的批量编号协议，固定逐条翻译。</summary>
        public override bool SupportsBatch
        {
            get { return false; }
        }

        public override async Task<IList<TranslationResult>> TranslateAsync(
            IList<TranslationItem> items,
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            var results = new List<TranslationResult>(items == null ? 0 : items.Count);
            if (items == null || items.Count == 0)
            {
                return results;
            }

            var source = ResolveSource(request);
            var target = ResolveTarget(request);
            var seconds = this._settings.TimeoutSeconds;

            using (var client = this.Context.Http.Create(this.Context.Configuration.Network, seconds))
            {
                foreach (var item in items)
                {
                    if (item == null)
                    {
                        results.Add(new TranslationResult { Error = "输入条目为空" });
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(item.Text))
                    {
                        results.Add(BackendHelpers.Failure(item, "原文为空，跳过"));
                        continue;
                    }

                    try
                    {
                        var translated = await TranslateOneAsync(client, item.Text, source, target, cancellationToken)
                            .ConfigureAwait(false);
                        results.Add(new TranslationResult { ItemId = item.ItemId, TranslatedText = translated });
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // HttpClient 超时只是这一条失败，不能中断整批。
                        this.LogError("{0}: 请求超时（{1} 秒）", this.Kind, seconds);
                        results.Add(BackendHelpers.Failure(item, "请求超时"));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        this.LogError("{0}: 翻译失败：{1}", this.Kind, ex.Message);
                        results.Add(BackendHelpers.Failure(item, ex.Message));
                    }
                }
            }

            return results;
        }

        public override async Task<string> TestAsync(CancellationToken cancellationToken)
        {
            try
            {
                var probe = new List<TranslationItem>
                {
                    new TranslationItem { ItemId = "self-test", Text = "Hello, world." }
                };

                var request = new TranslationRequest
                {
                    SourceLanguage = "en",
                    TargetLanguage = this.Context.Configuration.TargetLanguage
                };

                var results = await this.TranslateAsync(probe, request, cancellationToken).ConfigureAwait(false);
                if (results.Count == 0)
                {
                    return "后端没有返回结果";
                }

                return results[0].Succeeded ? null : results[0].Error;
            }
            catch (Exception ex)
            {
                // 契约要求 TestAsync 永不抛异常。
                return ex.Message;
            }
        }

        /// <summary>MyMemory 不支持 auto：auto 或未填时退化为 en（它的主力语种）。</summary>
        private static string ResolveSource(TranslationRequest request)
        {
            var value = BackendHelpers.NormalizeLanguage(request == null ? null : request.SourceLanguage, "en");
            return value == "auto" ? "en" : value;
        }

        private static string ResolveTarget(TranslationRequest request)
        {
            return BackendHelpers.NormalizeLanguage(request == null ? null : request.TargetLanguage, "zh");
        }

        /// <summary>长文本切块后逐块翻译并拼起来；短文本只发一次请求。</summary>
        private async Task<string> TranslateOneAsync(
            HttpClient client,
            string text,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            var chunks = SplitIntoChunks(text);
            if (chunks.Count == 1)
            {
                return await TranslateChunkAsync(client, chunks[0], source, target, cancellationToken).ConfigureAwait(false);
            }

            var parts = new List<string>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                try
                {
                    parts.Add(await TranslateChunkAsync(client, chunks[i], source, target, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 让用户知道是整段里的第几块出的问题（否则错误看起来像整条都失败得莫名其妙）。
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0}/{1} 段失败：{2}",
                        i + 1,
                        chunks.Count,
                        ex.Message));
                }
            }

            return JoinTranslated(parts, target);
        }

        private async Task<string> TranslateChunkAsync(
            HttpClient client,
            string text,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            // langpair 里的竖线必须编码成 %7C。
            var langPair = Uri.EscapeDataString(source + "|" + target);
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}?q={1}&langpair={2}",
                Endpoint,
                Uri.EscapeDataString(text),
                langPair);

            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false))
            {
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "MyMemory 返回 {0} {1}：{2}",
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        BackendHelpers.Shorten(payload)));
                }

                return ParseResponse(payload);
            }
        }

        /// <summary>读出 responseData.translatedText，并先判断额度/状态类错误。</summary>
        private static string ParseResponse(string payload)
        {
            using (var document = JsonDocument.Parse(payload))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("MyMemory 响应不是 JSON 对象");
                }

                JsonElement quota;
                var quotaFinished = root.TryGetProperty("quotaFinished", out quota) && quota.ValueKind == JsonValueKind.True;

                var status = ReadStatus(root);
                var details = ReadText(root, "responseDetails");

                // 额度用尽走的是 HTTP 200 + responseStatus 403，只能从正文判断。
                if (quotaFinished || (status.HasValue && status.Value != 200))
                {
                    var reason = string.IsNullOrWhiteSpace(details) ? "响应没有给出细节" : details.Trim();
                    if (quotaFinished || LooksLikeQuotaExhausted(reason) || LooksLikeQuotaExhausted(payload))
                    {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.InvariantCulture,
                            "MyMemory 今日免费额度已用尽（{0}）。请改用 Google 免密钥或 DeepSeek 后端，或等额度重置。",
                            BackendHelpers.Shorten(reason, 160)));
                    }

                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "MyMemory 返回状态 {0}：{1}",
                        status.HasValue ? status.Value.ToString(CultureInfo.InvariantCulture) : "未知",
                        BackendHelpers.Shorten(reason, 160)));
                }

                JsonElement data;
                if (!root.TryGetProperty("responseData", out data) || data.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("MyMemory 响应缺少 responseData");
                }

                JsonElement translated;
                if (!data.TryGetProperty("translatedText", out translated) || translated.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("MyMemory 响应缺少 responseData.translatedText");
                }

                var value = translated.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException("MyMemory 返回了空译文");
                }

                // 有些错误（超长、语种无效）是放在 translatedText 里的提示语，不能当译文用。
                if (LooksLikeErrorText(value))
                {
                    throw new InvalidOperationException("MyMemory 返回错误提示：" + BackendHelpers.Shorten(value, 160));
                }

                return value;
            }
        }

        /// <summary>responseStatus 既可能是数字也可能是字符串。</summary>
        private static int? ReadStatus(JsonElement root)
        {
            JsonElement status;
            if (!root.TryGetProperty("responseStatus", out status))
            {
                return null;
            }

            if (status.ValueKind == JsonValueKind.Number)
            {
                int number;
                return status.TryGetInt32(out number) ? number : (int?)null;
            }

            if (status.ValueKind == JsonValueKind.String)
            {
                int parsed;
                return int.TryParse(status.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    ? parsed
                    : (int?)null;
            }

            return null;
        }

        /// <summary>读取字符串字段；字段是对象/数组时退化为它的原始 JSON 文本。</summary>
        private static string ReadText(JsonElement root, string name)
        {
            JsonElement value;
            if (!root.TryGetProperty(name, out value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined
                ? null
                : value.GetRawText();
        }

        private static bool LooksLikeQuotaExhausted(string text)
        {
            return ContainsAny(text, "YOU USED ALL AVAILABLE FREE", "FREE TRANSLATIONS FOR TODAY", "QUOTA");
        }

        private static bool LooksLikeErrorText(string text)
        {
            return ContainsAny(
                text,
                "MYMEMORY WARNING",
                "INVALID LANGUAGE PAIR",
                "QUERY LENGTH LIMIT EXCEEDED",
                "PLEASE SELECT TWO DISTINCT LANGUAGES",
                "TOO MANY REQUESTS");
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 按字符数把长文本切成不超过 <see cref="MaxQueryChars"/> 的块，
        /// 尽量在句末（其次空白）处断开。所有块首尾相接，拼起来等于原文。
        /// </summary>
        private static List<string> SplitIntoChunks(string text)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                chunks.Add(text ?? string.Empty);
                return chunks;
            }

            var start = 0;
            while (start < text.Length)
            {
                var end = start;
                var count = 0;
                var sentenceBreak = -1;
                var spaceBreak = -1;

                while (end < text.Length)
                {
                    var unit = UnitLength(text, end);
                    if (count + unit > MaxQueryChars)
                    {
                        break;
                    }

                    count += unit;
                    end += unit;

                    var last = text[end - 1];
                    if (IsSentenceEnder(last))
                    {
                        sentenceBreak = end;
                    }
                    else if (char.IsWhiteSpace(last))
                    {
                        spaceBreak = end;
                    }
                }

                int cut;
                if (end >= text.Length)
                {
                    cut = text.Length;
                }
                else if (sentenceBreak > start)
                {
                    cut = sentenceBreak;
                }
                else if (spaceBreak > start)
                {
                    cut = spaceBreak;
                }
                else
                {
                    // 一段没有任何断点的超长文本：只能硬切。
                    cut = end;
                }

                // 绝不把代理对（emoji 等）劈成两半。
                if (cut < text.Length && char.IsHighSurrogate(text[cut - 1]))
                {
                    cut--;
                }

                if (cut <= start)
                {
                    cut = Math.Min(start + 1, text.Length);
                }

                chunks.Add(text.Substring(start, cut - start));
                start = cut;
            }

            return chunks;
        }

        /// <summary>返回该位置上一个"字符单元"占用的 UTF-16 长度（代理对算 2）。</summary>
        private static int UnitLength(string text, int index)
        {
            return char.IsHighSurrogate(text[index])
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1])
                    ? 2
                    : 1;
        }

        private static bool IsSentenceEnder(char c)
        {
            switch (c)
            {
                case '.':
                case '!':
                case '?':
                case ';':
                case '\n':
                case '\r':
                case '。':
                case '！':
                case '？':
                case '；':
                case '…':
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>中文/日文/韩文不需要用空格连接译文块，其它语言需要。</summary>
        private static bool IsCjkTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            return target.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>拼接各块译文：CJK 目标直接相连，其它语种在缺空格处补一个空格。</summary>
        private static string JoinTranslated(List<string> parts, string target)
        {
            var builder = new StringBuilder();
            var cjk = IsCjkTarget(target);
            foreach (var raw in parts)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                if (!cjk && builder.Length > 0 && !char.IsWhiteSpace(builder[builder.Length - 1]) && !char.IsWhiteSpace(raw[0]))
                {
                    builder.Append(' ');
                }

                builder.Append(raw);
            }

            return builder.ToString();
        }
    }
}
