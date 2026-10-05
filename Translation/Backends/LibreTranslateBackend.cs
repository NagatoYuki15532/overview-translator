using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Services;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// 自建 LibreTranslate 后端：<c>POST {Free.LibreTranslateUrl}/translate</c>，
    /// body 为 <c>{"q":..,"source":..,"target":..,"format":"text"}</c>，配置了 api_key 时再加
    /// <c>"api_key"</c>（公共实例通常需要它）。
    ///
    /// 语言差异：LibreTranslate 支持 <c>source=auto</c>，所以 auto 原样发送；
    /// target 用 <see cref="BackendHelpers.NormalizeLanguage"/> 归一化成两字母代码（zh-CN -&gt; zh）。
    ///
    /// 这是自建服务，地址必须由用户填写：地址为空或不是 http/https 时抛
    /// <see cref="BackendConfigurationException"/>（整体不可用，属于配置错误），
    /// 而不是每条都失败一遍。单条的失败仍然只走 <see cref="TranslationResult.Error"/>。
    /// </summary>
    public sealed class LibreTranslateBackend : BackendBase
    {
        /// <summary>让请求体里的中文保持可读（这是发往 JSON 接口的正文，不涉及 HTML 输出）。</summary>
        private static readonly JsonSerializerOptions BodyOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly FreeBackendSettings _settings;

        public LibreTranslateBackend(BackendContext context)
            : base(context)
        {
            this._settings = context.Configuration.Free ?? new FreeBackendSettings();
        }

        public override TranslationBackend Kind
        {
            get { return TranslationBackend.LibreTranslate; }
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

            // 配置问题在这一步就暴露：要么整批可用，要么整批报配置错误。
            var endpoint = this.ResolveEndpoint();

            var source = ResolveSource(request);
            var target = ResolveTarget(request);
            var apiKey = (this._settings.LibreTranslateApiKey ?? string.Empty).Trim();
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
                        var translated = await TranslateOneAsync(client, endpoint, item.Text, source, target, apiKey, cancellationToken)
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
            catch (BackendConfigurationException ex)
            {
                // 契约要求 TestAsync 不抛异常：设置页要能直接显示"地址没填"。
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>目标语言归一化成两字母代码；LibreTranslate 用 zh 表示中文。</summary>
        private static string ResolveSource(TranslationRequest request)
        {
            return BackendHelpers.NormalizeLanguage(request == null ? null : request.SourceLanguage, "auto");
        }

        private static string ResolveTarget(TranslationRequest request)
        {
            return BackendHelpers.NormalizeLanguage(request == null ? null : request.TargetLanguage, "zh");
        }

        /// <summary>校验并拼出 /translate 的完整地址；配置不可用时抛 BackendConfigurationException。</summary>
        private string ResolveEndpoint()
        {
            var url = (this._settings.LibreTranslateUrl ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                throw new BackendConfigurationException(
                    "LibreTranslate 是自建服务，必须先在「简介翻译」设置页填写服务地址（例如 http://127.0.0.1:5000）。");
            }

            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                throw new BackendConfigurationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "LibreTranslate 服务地址「{0}」不是有效的 http/https 地址，请检查是否漏了 http:// 前缀。",
                    BackendHelpers.Shorten(url, 120)));
            }

            // 用户常把结尾斜杠一起粘进来，避免拼出 //translate。
            return url.TrimEnd('/') + "/translate";
        }

        private async Task<string> TranslateOneAsync(
            HttpClient client,
            string endpoint,
            string text,
            string source,
            string target,
            string apiKey,
            CancellationToken cancellationToken)
        {
            using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                message.Content = new StringContent(BuildBody(text, source, target, apiKey), Encoding.UTF8, "application/json");

                using (var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        // LibreTranslate 的错误信息在 {"error":"..."} 里，尽量把它带给用户。
                        var detail = ExtractError(payload);
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.InvariantCulture,
                            "LibreTranslate 返回 {0} {1}：{2}",
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            BackendHelpers.Shorten(detail ?? payload)));
                    }

                    return ParseResponse(payload);
                }
            }
        }

        private static string BuildBody(string text, string source, string target, string apiKey)
        {
            var fields = new List<string>(5)
            {
                "\"q\":" + JsonSerializer.Serialize(text ?? string.Empty, BodyOptions),
                "\"source\":" + JsonSerializer.Serialize(source, BodyOptions),
                "\"target\":" + JsonSerializer.Serialize(target, BodyOptions),
                "\"format\":\"text\""
            };

            // 只有真的填了 key 才发送 api_key，绝不发空字符串。
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                fields.Add("\"api_key\":" + JsonSerializer.Serialize(apiKey, BodyOptions));
            }

            return "{" + string.Join(",", fields) + "}";
        }

        private static string ParseResponse(string payload)
        {
            using (var document = JsonDocument.Parse(payload))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("LibreTranslate 响应不是 JSON 对象");
                }

                JsonElement error;
                if (root.TryGetProperty("error", out error))
                {
                    var message = error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText();
                    throw new InvalidOperationException("LibreTranslate 返回错误：" + BackendHelpers.Shorten(message, 200));
                }

                JsonElement translated;
                if (!root.TryGetProperty("translatedText", out translated) || translated.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("LibreTranslate 响应缺少 translatedText");
                }

                var value = translated.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException("LibreTranslate 返回了空译文");
                }

                return value;
            }
        }

        /// <summary>从错误响应里取出 {"error":"..."} 的文本，取不到就返回 null。</summary>
        private static string ExtractError(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                using (var document = JsonDocument.Parse(payload))
                {
                    JsonElement error;
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("error", out error))
                    {
                        return error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText();
                    }
                }
            }
            catch (JsonException)
            {
                // 不是 JSON 就用原始正文。
            }

            return null;
        }
    }
}
