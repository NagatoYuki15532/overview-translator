using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// Base class for the chat-completions family (DeepSeek and every OpenAI-compatible
    /// endpoint). It owns the prompt, the JSON request shape, the response parsing and the
    /// batch protocol; subclasses only supply endpoint, credentials, model and defaults.
    /// </summary>
    public abstract class ChatCompletionsBackendBase : BackendBase
    {
        protected LlmSettings Settings { get; private set; }

        protected ChatCompletionsBackendBase(BackendContext context)
            : base(context)
        {
            this.Settings = context.Configuration.Llm ?? new LlmSettings();
        }

        /// <summary>Full URL of the chat completions endpoint.</summary>
        protected abstract string GetEndpoint();

        /// <summary>Header values to add to every request. Never add an empty Authorization header.</summary>
        protected abstract void ApplyAuth(HttpRequestMessage message);

        /// <summary>Model name to send. Separate hook so a subclass can map friendly names to real ones.</summary>
        protected virtual string GetModel()
        {
            return this.Settings.Model;
        }

        public override bool SupportsBatch
        {
            get { return true; }
        }

        /// <summary>
        /// The translation prompt. Deliberately strict: the caller parses the answer, so the
        /// model must return numbered lines and nothing else.
        /// </summary>
        protected virtual string BuildSystemPrompt(TranslationRequest request, int count)
        {
            var target = string.IsNullOrWhiteSpace(request.TargetLanguage) ? "zh-CN" : request.TargetLanguage;
            var source = string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : request.SourceLanguage;

            var builder = new StringBuilder();
            builder.Append("You are a professional audiovisual translator specializing in anime and TV series metadata. ");
            builder.Append("Translate the given synopsis text");
            builder.Append(source == "auto" ? string.Empty : " from " + source);
            builder.Append(" into ").Append(target).Append(". ");
            builder.Append("Rules: keep proper nouns, character names and place names natural and consistent with official releases; ");
            builder.Append("do not add, remove or explain anything; keep the tone of a series synopsis; ");
            builder.Append("output plain text only, with no quotes, no markdown and no comments. ");
            builder.Append("If a text is already written in the target language, return it unchanged.");

            if (count > 1)
            {
                builder.Append(" You will receive ").Append(count).Append(" numbered texts. ");
                builder.Append("Return exactly ").Append(count).Append(" lines. ");
                builder.Append("Each line must start with the same number, then a full-width or half-width pipe character, then the translation. ");
                builder.Append("Example reply format: 1|译文一  2|译文二. ");
                // The parser treats any extra line as ambiguity and refuses the whole batch, so
                // say this explicitly rather than letting a polite "以上。" cost a whole run.
                builder.Append("Output nothing else: no greeting, no closing remark, no summary line, no code fence. ");
                builder.Append("Every line must be numbered; never leave a line unnumbered.");
            }

            if (!string.IsNullOrWhiteSpace(request.ExtraPrompt))
            {
                builder.Append(' ').Append(request.ExtraPrompt.Trim());
            }

            return builder.ToString();
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

            var body = this.BuildRequestBody(items, request);
            var endpoint = this.GetEndpoint();

            using (var client = this.Context.Http.Create(this.Context.Configuration.Network, this.Settings.TimeoutSeconds))
            using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                this.ApplyAuth(message);

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    this.LogError("{0}: request to {1} failed: {2}", this.Kind, endpoint, ex.Message);
                    return this.AllFailed(items, "请求失败: " + ex.Message);
                }

                using (response)
                {
                    var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var reason = string.Format(
                            "{0} {1}: {2}",
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            BackendHelpers.Shorten(payload));
                        this.LogError("{0}: {1}", this.Kind, reason);
                        return this.AllFailed(items, reason);
                    }

                    string content;
                    try
                    {
                        content = this.ExtractMessageContent(payload);
                    }
                    catch (Exception ex)
                    {
                        this.LogError("{0}: could not parse the response: {1} | body={2}", this.Kind, ex.Message, BackendHelpers.Shorten(payload));
                        return this.AllFailed(items, "响应解析失败: " + ex.Message);
                    }

                    return this.MapContentToResults(items, content);
                }
            }
        }

        /// <summary>Number of items to send in one request.</summary>
        protected virtual int GetBatchSize()
        {
            var size = this.Settings.BatchSize;
            return size < 1 ? 1 : size;
        }

        /// <summary>Builds the JSON request body for one call.</summary>
        protected virtual string BuildRequestBody(IList<TranslationItem> items, TranslationRequest request)
        {
            var system = this.BuildSystemPrompt(request, items.Count);
            var user = this.BuildUserContent(items);

            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"model\":").Append(JsonString(this.GetModel())).Append(',');
            builder.Append("\"messages\":[");
            builder.Append("{\"role\":\"system\",\"content\":").Append(JsonString(system)).Append("},");
            builder.Append("{\"role\":\"user\",\"content\":").Append(JsonString(user)).Append("}");
            builder.Append("],");
            builder.Append("\"temperature\":").Append(this.Settings.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            builder.Append("\"max_tokens\":").Append(this.Settings.MaxTokens < 1 ? 8192 : this.Settings.MaxTokens).Append(',');
            builder.Append("\"stream\":false");
            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>Formats the items into the numbered text the prompt asks for.</summary>
        protected virtual string BuildUserContent(IList<TranslationItem> items)
        {
            if (items.Count == 1)
            {
                var single = items[0];
                if (!string.IsNullOrWhiteSpace(single.Context))
                {
                    return "作品名：" + single.Context + "\n简介：\n" + single.Text;
                }

                return single.Text;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < items.Count; i++)
            {
                builder.Append(i + 1).Append('|').AppendLine(NormalizeForPrompt(items[i].Text));
            }

            return builder.ToString();
        }

        /// <summary>Collapses newlines so one item stays on one numbered line.</summary>
        protected static string NormalizeForPrompt(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
        }

        /// <summary>Pulls <c>choices[0].message.content</c> out of a chat-completions response.</summary>
        protected virtual string ExtractMessageContent(string json)
        {
            var choice = JsonPath.FindFirstObject(json, "choices");
            if (choice == null)
            {
                throw new InvalidOperationException("no choices in response");
            }

            var message = JsonPath.FindProperty(choice, "message");
            if (message == null)
            {
                throw new InvalidOperationException("no message in choices[0]");
            }

            var content = JsonPath.FindString(message, "content");
            if (content == null)
            {
                throw new InvalidOperationException("no content in choices[0].message");
            }

            return content;
        }

        /// <summary>
        /// Maps the model answer back onto the input items.
        ///
        /// Correctness rule: a translation may only be attached to the item whose number it
        /// carries. If the numbering does not line up with the inputs, the run reports failures
        /// rather than guessing — silently attaching the wrong synopsis to an episode is far
        /// worse than translating nothing.
        /// </summary>
        protected virtual IList<TranslationResult> MapContentToResults(IList<TranslationItem> items, string content)
        {
            var results = new List<TranslationResult>(items.Count);
            var trimmed = StripOuterFence(content ?? string.Empty).Trim();

            // Single item: accept the text as-is, but strip a leading "1|" / "1:" the model may
            // still have added, which would otherwise end up inside the user's synopsis.
            if (items.Count == 1)
            {
                results.Add(new TranslationResult
                {
                    ItemId = items[0].ItemId,
                    TranslatedText = StripLeadingNumber(trimmed)
                });
                return results;
            }

            var byNumber = ParseNumberedLines(trimmed);

            // Three shapes, and they must be handled differently:
            //   * every line unnumbered  -> positional mapping is safe (the model kept order);
            //   * any unnumbered line    -> ambiguous, refuse the whole batch;
            //   * every line numbered    -> REQUIRED to match 1..N exactly; the numbers are the
            //                              only thing that says which item each text belongs to.
            //
            // An earlier revision got this wrong and mapped "all numbered" positionally, so
            // "3|丙\n1|甲\n2|乙" wrote 丙 to item 1, 甲 to item 2 and 乙 to item 3 — every item
            // wrong. Numbering must never be ignored once it is present.
            var hasUnnumbered = false;
            var hasNumbered = false;
            foreach (var key in byNumber.Keys)
            {
                if (key < 0)
                {
                    hasUnnumbered = true;
                }
                else
                {
                    hasNumbered = true;
                }
            }

            if (hasUnnumbered && hasNumbered)
            {
                // Refuse instead of guessing: attaching the wrong synopsis to an episode is worse
                // than translating nothing. (This shape, "译文一\n1|译文二", used to hand item 0
                // the text belonging to item 1.)
                for (var i = 0; i < items.Count; i++)
                {
                    results.Add(BackendHelpers.Failure(
                        items[i],
                        "模型返回的编号不完整（部分行未编号），无法确定归属，已放弃以免写错剧集"));
                }

                return results;
            }

            if (!hasNumbered)
            {
                // No numbering at all: positional mapping, which also restores the documented
                // "model dropped the numbering but kept the order" fallback.
                if (byNumber.Count != items.Count)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        results.Add(BackendHelpers.Failure(
                            items[i],
                            string.Format("模型返回了 {0} 行，但需要 {1} 条译文", byNumber.Count, items.Count)));
                    }

                    return results;
                }

                var ordered = new List<string>(byNumber.Values);
                for (var i = 0; i < items.Count; i++)
                {
                    results.Add(new TranslationResult
                    {
                        ItemId = items[i].ItemId,
                        TranslatedText = StripLeadingNumber(ordered[i].Trim())
                    });
                }

                return results;
            }

            // Numbered exactly 1..N, or nothing.
            for (var i = 0; i < items.Count; i++)
            {
                string value;
                if (byNumber.TryGetValue(i + 1, out value) && !string.IsNullOrWhiteSpace(value))
                {
                    results.Add(new TranslationResult
                    {
                        ItemId = items[i].ItemId,
                        TranslatedText = value.Trim()
                    });
                }
                else
                {
                    results.Add(BackendHelpers.Failure(
                        items[i],
                        "模型返回的编号与条目对不上（期望第 " + (i + 1) + " 条），已放弃以免写错剧集"));
                }
            }

            return results;
        }

        /// <summary>Removes a surrounding ``` fence, which models add despite being told not to.</summary>
        protected static string StripOuterFence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var trimmed = text.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return trimmed;
            }

            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline < 0)
            {
                return trimmed;
            }

            var body = trimmed.Substring(firstNewline + 1);
            var lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
            return lastFence >= 0 ? body.Substring(0, lastFence).Trim() : body.Trim();
        }

        /// <summary>
        /// Strips a leading list marker such as "1|", "1. ", "1: " or "1、", including full-width
        /// digits, and returns the input unchanged when there is no marker.
        ///
        /// Deliberately conservative. An earlier version stripped any leading digit run and
        /// silently corrupted legitimate text: "2024年，人类首次登陆火星。" became
        /// "4年，人类首次登陆火星。" and "12 只猴子" became "只猴子". A synopsis that starts with
        /// a year is common, so a number is only treated as a marker when all of the following
        /// hold:
        ///   * at most 3 digits (a year has 4), and
        ///   * it is followed by an explicit separator, and
        ///   * what follows the separator is not another digit run (so "3:14 是圆周率" survives).
        /// </summary>
        protected static string StripLeadingNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var match = Regex.Match(text, @"^\s*([0-9０-９]{1,3})\s*([|｜\.．:：、,，)）\-—])\s*");
            if (!match.Success)
            {
                return text;
            }

            var rest = text.Substring(match.Length);
            if (rest.Length == 0)
            {
                return text;
            }

            // "3:14 是圆周率" / "1.5 倍" are not list markers.
            if (rest[0] >= '0' && rest[0] <= '9')
            {
                return text;
            }

            if (rest[0] >= '０' && rest[0] <= '９')
            {
                return text;
            }

            return rest.TrimStart();
        }

        /// <summary>
        /// Parses "1|text" lines into a map. Numbered lines keep their real number; lines without
        /// a number are stored under successive negative keys so they can never collide with a
        /// real number (colliding was a real bug: it silently mis-assigned translations).
        /// </summary>
        protected static Dictionary<int, string> ParseNumberedLines(string text)
        {
            var map = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return map;
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var unnumbered = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var separator = line.IndexOf('|');
                if (separator < 0)
                {
                    separator = line.IndexOf('｜');
                }

                if (separator <= 0)
                {
                    // No pipe at all. The model may still have used a list marker ("1. text",
                    // "1: text"); recognise it so the numbering is honoured rather than treated
                    // as body text. A line that is not a marker stays positional.
                    // \s* not \s+: models emit "1:译文" as often as "1: 译文". The trailing
                    // lookahead is what keeps "3:14 是圆周率" out of this branch.
                    var marker = Regex.Match(line, @"^\s*([0-9０-９]{1,3})\s*([\.．:：、)）])\s*(?![0-9０-９])");
                    if (marker.Success)
                    {
                        int markerNumber;
                        if (TryParseIndex(marker.Groups[1].Value, out markerNumber))
                        {
                            map[markerNumber] = line.Substring(marker.Length).Trim();
                            continue;
                        }
                    }

                    unnumbered++;
                    map[-unnumbered] = line;
                    continue;
                }

                var numberPart = line.Substring(0, separator).Trim().TrimEnd('.', ':', '．', '：', '、', ')', '）');
                var rest = line.Substring(separator + 1).Trim();

                int number;
                if (TryParseIndex(numberPart, out number))
                {
                    map[number] = rest;
                }
                else
                {
                    unnumbered++;
                    map[-unnumbered] = line;
                }
            }

            return map;
        }

        /// <summary>Parses a line-number prefix, accepting ASCII and full-width digits.</summary>
        private static bool TryParseIndex(string value, out int number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            var builder = new StringBuilder(normalized.Length);

            foreach (var c in normalized)
            {
                if (c >= '0' && c <= '9')
                {
                    builder.Append(c);
                }
                else if (c >= '０' && c <= '９')
                {
                    // Full-width digits, which some models emit.
                    builder.Append((char)('0' + (c - '０')));
                }
                else
                {
                    // Any other character means this is not a pure index.
                    return false;
                }
            }

            return builder.Length > 0 && int.TryParse(builder.ToString(), out number) && number > 0;
        }

        protected IList<TranslationResult> AllFailed(IList<TranslationItem> items, string error)
        {
            var results = new List<TranslationResult>(items.Count);
            foreach (var item in items)
            {
                results.Add(BackendHelpers.Failure(item, error));
            }

            return results;
        }

        public override async Task<string> TestAsync(CancellationToken cancellationToken)
        {
            try
            {
                var probe = new List<TranslationItem>
                {
                    new TranslationItem { ItemId = "self-test", Text = "The journey begins." }
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
                return ex.Message;
            }
        }

        /// <summary>Escapes a string as a JSON literal.</summary>
        protected static string JsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder(value.Length + 8);
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
