using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// Bing 免密钥后端。官方免密钥路径已死，这里用的是 bing.com 翻译网页自己调用的
    /// <c>ttranslatev3</c> 接口；实现前用 Node 走本机代理逐条实测过，下面是真实结论：
    ///
    /// 1) <c>GET https://edge.microsoft.com/translate/auth</c>（老牌免密钥取 token 路径）
    ///    2026-10-04 实测恒为 <c>HTTP 404</c>、content-length 0，带不带参数、带不带尾斜杠都一样，
    ///    该路径已经下线，不能再用。
    /// 2) <c>POST https://api-edge.cognitive.microsofttranslator.com/translate</c> 不带任何凭据
    ///    实测 <c>HTTP 401</c>（"credentials are missing or invalid"），必须买 key。
    /// 3) <c>GET https://www.bing.com/translator</c> + <c>POST /ttranslatev3?isVertical=1&amp;IG=..&amp;IID=..</c>
    ///    实测可用：页面 HTML 里取 <c>IG</c>、<c>data-iid</c>、<c>params_AbusePreventionHelper</c>
    ///    （key/token）三样，加上页面下发的 Cookie，用表单
    ///    <c>fromLang/text/to/key/token</c> 提交，返回 <c>[{"translations":[{"text":".."}]}]</c>。
    ///
    /// 因此这不是"稳定"的官方接口，而是网页抓取，前提是网页结构不变：
    /// - 页面结构一变（取不到 IG/IID/防滥用参数）就返回可读错误，并建议改用 Google/DeepSeek，绝不假装成功；
    /// - 单次请求上限 1000 字符（实测 990 字符正常、1010 字符返回 <c>{"statusCode":400}</c>），
    ///   所以长文本按 900 字符切块翻译后拼接；
    /// - <c>fromLang</c> 必须给值，自动识别只能写 <c>auto-detect</c>（实测写 auto 或省略都是 400）；
    /// - 会话（IG/IID/key/token/Cookie）在实例内缓存 10 分钟，失败时重建并重试一次。
    ///
    /// 语言差异：source 为 auto 时用 <c>auto-detect</c>；target 用
    /// <see cref="BackendHelpers.NormalizeLanguage"/> 归一化，中文要写成 Bing 的 zh-Hans/zh-Hant。
    /// </summary>
    public sealed class BingFreeBackend : BackendBase
    {
        private const string TranslatorPage = "https://www.bing.com/translator";

        private const string TranslateEndpoint = "https://www.bing.com/ttranslatev3";

        /// <summary>Bing 网页接口的单次上限是 1000 字符，留出余量。</summary>
        private const int MaxQueryChars = 900;

        /// <summary>浏览器 UA。Bing 的网页接口对非浏览器 UA 不友好，每次调用都会覆盖。</summary>
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

        private static readonly Regex IgPattern = new Regex("IG:\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex IidPattern = new Regex("data-iid=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex AbusePattern = new Regex(
            @"params_AbusePreventionHelper\s*=\s*\[([^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>params_AbusePreventionHelper = [时间戳, "token", 毫秒]。</summary>
        private static readonly Regex AbuseValuePattern = new Regex(
            "^\\s*(\\d+)\\s*,\\s*\"([^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly FreeBackendSettings _settings;

        private readonly object _sessionLock = new object();

        private BingSession _session;

        /// <summary>同一个会话里 IID 尾部要递增，避免重复的 IID。</summary>
        private int _requestCounter;

        public BingFreeBackend(BackendContext context)
            : base(context)
        {
            this._settings = context.Configuration.Free ?? new FreeBackendSettings();
        }

        public override TranslationBackend Kind
        {
            get { return TranslationBackend.BingFree; }
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
                // 客户端由本次调用独占：把默认 UA 换成浏览器 UA，保证只发一个 UA 头。
                client.DefaultRequestHeaders.Remove("User-Agent");
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

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

        /// <summary>source 为 auto 时用 Bing 网页接口的 auto-detect（实测唯一可用的自动识别取值）。</summary>
        private static string ResolveSource(TranslationRequest request)
        {
            var value = BackendHelpers.NormalizeLanguage(request == null ? null : request.SourceLanguage, "auto");
            return value == "auto" ? "auto-detect" : value;
        }

        /// <summary>
        /// Bing 的中文代码必须是 zh-Hans / zh-Hant；归一化会把地区丢掉，
        /// 所以这里先在原始配置里认繁体（zh-TW/zh-HK/Hant），其余中文按简体。
        /// </summary>
        private static string ResolveTarget(TranslationRequest request)
        {
            var raw = request == null ? null : request.TargetLanguage;
            var normalized = BackendHelpers.NormalizeLanguage(raw, "zh");
            if (normalized != "zh")
            {
                return normalized;
            }

            if (!string.IsNullOrEmpty(raw)
                && (raw.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.IndexOf("繁体", StringComparison.Ordinal) >= 0))
            {
                return "zh-Hant";
            }

            return "zh-Hans";
        }

        /// <summary>长文本切块；短文本只发一次请求。</summary>
        private async Task<string> TranslateOneAsync(
            HttpClient client,
            string text,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            var chunks = SplitIntoChunks(text, MaxQueryChars);
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

        /// <summary>一次翻译请求：失败（多半是会话过期）就重建会话重试一次。</summary>
        private async Task<string> TranslateChunkAsync(
            HttpClient client,
            string text,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            Exception last = null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var session = await GetSessionAsync(client, attempt > 0, cancellationToken).ConfigureAwait(false);
                    return await PostTranslateAsync(client, session, text, source, target, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    this.InvalidateSession();
                    this.LogError("{0}: 第 {1} 次尝试失败：{2}", this.Kind, attempt + 1, ex.Message);
                }
            }

            throw last;
        }

        /// <summary>取得（必要时重建）网页会话：IG、IID、防滥用 key/token 和 Cookie。</summary>
        private async Task<BingSession> GetSessionAsync(HttpClient client, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!forceRefresh)
            {
                lock (this._sessionLock)
                {
                    if (this._session != null && DateTime.UtcNow < this._session.ExpiresUtc)
                    {
                        return this._session;
                    }
                }
            }

            this.LogInfo("{0}: 正在建立免密钥会话（抓取 {1}）", this.Kind, TranslatorPage);

            using (var response = await client.GetAsync(TranslatorPage, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false))
            {
                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Bing 免密钥接口不可用：翻译页返回 {0} {1}。请改用 Google 免密钥或 DeepSeek 后端。",
                        (int)response.StatusCode,
                        response.ReasonPhrase));
                }

                var session = BingSession.FromHtml(html, ExtractCookies(response));
                if (session == null)
                {
                    throw new InvalidOperationException(
                        "Bing 免密钥接口不可用：翻译页结构已变化（取不到 IG/IID/防滥用参数）。请改用 Google 免密钥或 DeepSeek 后端。");
                }

                lock (this._sessionLock)
                {
                    this._session = session;
                }

                return session;
            }
        }

        private void InvalidateSession()
        {
            lock (this._sessionLock)
            {
                this._session = null;
            }
        }

        private async Task<string> PostTranslateAsync(
            HttpClient client,
            BingSession session,
            string text,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            var counter = Interlocked.Increment(ref this._requestCounter);
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}?isVertical=1&IG={1}&IID={2}.{3}",
                TranslateEndpoint,
                Uri.EscapeDataString(session.Ig),
                Uri.EscapeDataString(session.Iid),
                counter.ToString(CultureInfo.InvariantCulture));

            var form = new StringBuilder(text.Length * 3 + 128);
            form.Append("fromLang=").Append(FormEncode(source));
            form.Append("&text=").Append(FormEncode(text));
            form.Append("&to=").Append(FormEncode(target));
            if (!string.IsNullOrEmpty(session.AbuseKey))
            {
                form.Append("&key=").Append(FormEncode(session.AbuseKey));
                form.Append("&token=").Append(FormEncode(session.AbuseToken));
            }

            using (var message = new HttpRequestMessage(HttpMethod.Post, url))
            {
                message.Headers.TryAddWithoutValidation("Referer", TranslatorPage);
                message.Headers.TryAddWithoutValidation("Accept", "*/*");
                if (!string.IsNullOrEmpty(session.Cookie))
                {
                    message.Headers.TryAddWithoutValidation("Cookie", session.Cookie);
                }

                message.Content = new StringContent(form.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded");

                using (var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.InvariantCulture,
                            "Bing 接口返回 {0} {1}：{2}",
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            BackendHelpers.Shorten(payload)));
                    }

                    return ParseResponse(payload);
                }
            }
        }

        /// <summary>解析 ttranslatev3 的数组响应；statusCode 形式的错误转成可读消息。</summary>
        private static string ParseResponse(string payload)
        {
            using (var document = JsonDocument.Parse(payload))
            {
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    JsonElement code;
                    if (root.TryGetProperty("statusCode", out code))
                    {
                        var message = ReadText(root, "errorMessage");
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.InvariantCulture,
                            "Bing 拒绝请求（statusCode {0}）：{1}",
                            code.ValueKind == JsonValueKind.Number ? code.GetRawText() : (ReadText(root, "statusCode") ?? "未知"),
                            string.IsNullOrWhiteSpace(message)
                                ? "接口没有给出原因，通常是会话过期或单次文本超过 1000 字符"
                                : message));
                    }

                    throw new InvalidOperationException("Bing 响应结构不符合预期：" + BackendHelpers.Shorten(payload, 160));
                }

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("Bing 响应为空：" + BackendHelpers.Shorten(payload, 160));
                }

                var first = root[0];
                if (first.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Bing 响应结构不符合预期：" + BackendHelpers.Shorten(payload, 160));
                }

                JsonElement translations;
                if (!first.TryGetProperty("translations", out translations)
                    || translations.ValueKind != JsonValueKind.Array
                    || translations.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("Bing 响应缺少 translations");
                }

                JsonElement translated;
                if (!translations[0].TryGetProperty("text", out translated) || translated.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("Bing 响应缺少 translations[0].text");
                }

                var value = translated.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException("Bing 返回了空译文");
                }

                return value;
            }
        }

        private static string ReadText(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value))
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

        /// <summary>把页面下发的 Set-Cookie 合成一个 Cookie 头（不依赖底层 handler 的 CookieContainer）。</summary>
        private static string ExtractCookies(HttpResponseMessage response)
        {
            IEnumerable<string> values;
            if (!response.Headers.TryGetValues("Set-Cookie", out values))
            {
                return null;
            }

            var pairs = new List<string>();
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var semi = value.IndexOf(';');
                var pair = (semi > 0 ? value.Substring(0, semi) : value).Trim();
                if (pair.Length > 0)
                {
                    pairs.Add(pair);
                }
            }

            return pairs.Count == 0 ? null : string.Join("; ", pairs);
        }

        /// <summary>表单编码：空格用 +（和浏览器一致），其余按 RFC 3986 转义。</summary>
        private static string FormEncode(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty).Replace("%20", "+");
        }

        /// <summary>
        /// 按字符数切块（Bing 上限 1000 字符），尽量在句末（其次空白）断开。
        /// 所有块首尾相接，拼起来等于原文。
        /// </summary>
        private static List<string> SplitIntoChunks(string text, int maxChars)
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
                    if (count + unit > maxChars)
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
                    cut = end;
                }

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

        /// <summary>网页会话参数，全部来自 https://www.bing.com/translator 的 HTML。</summary>
        private sealed class BingSession
        {
            public string Ig { get; set; }

            public string Iid { get; set; }

            public string AbuseKey { get; set; }

            public string AbuseToken { get; set; }

            public string Cookie { get; set; }

            public DateTime ExpiresUtc { get; set; }

            /// <summary>解析失败返回 null（调用方据此报"接口不可用"）。</summary>
            public static BingSession FromHtml(string html, string cookie)
            {
                if (string.IsNullOrEmpty(html))
                {
                    return null;
                }

                var ig = FirstGroup(IgPattern, html);
                var iid = FirstGroup(IidPattern, html);
                if (string.IsNullOrEmpty(ig) || string.IsNullOrEmpty(iid))
                {
                    return null;
                }

                var session = new BingSession
                {
                    Ig = ig,
                    Iid = iid,
                    Cookie = cookie,
                    ExpiresUtc = DateTime.UtcNow.Add(SessionLifetime)
                };

                var abuse = FirstGroup(AbusePattern, html);
                if (!string.IsNullOrEmpty(abuse))
                {
                    var match = AbuseValuePattern.Match(abuse);
                    if (match.Success)
                    {
                        session.AbuseKey = match.Groups[1].Value;
                        session.AbuseToken = match.Groups[2].Value;
                    }
                }

                return session;
            }
        }

        private static string FirstGroup(Regex pattern, string input)
        {
            var match = pattern.Match(input);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
