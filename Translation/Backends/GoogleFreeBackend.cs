using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// Google 免密钥翻译后端：<c>translate.googleapis.com/translate_a/single?client=gtx</c>。
    ///
    /// 语言差异：<c>sl=auto</c> 是 Google 支持的自动识别取值，所以 source 为 auto 时原样发送；
    /// tl 用 <see cref="BackendHelpers.NormalizeLanguage"/> 归一化成两字母代码
    /// （zh-CN -&gt; zh，Google 把 zh 当作简体中文）。
    ///
    /// 响应是嵌套数组：<c>[[["译文1","原文1",null,null,10],["译文2","原文2",...]],null,"en",...]</c>。
    /// 长文本会被切成多段，必须把每一段的第 0 个元素按顺序拼起来才是完整译文；
    /// 只取第一段会丢掉后面的内容。末段的译文可能是 null（语言探测占位），解析时跳过。
    /// </summary>
    public sealed class GoogleFreeBackend : BackendBase
    {
        private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

        private readonly FreeBackendSettings _settings;

        public GoogleFreeBackend(BackendContext context)
            : base(context)
        {
            this._settings = context.Configuration.Free ?? new FreeBackendSettings();
        }

        public override TranslationBackend Kind
        {
            get { return TranslationBackend.GoogleFree; }
        }

        /// <summary>免费机器翻译接口没有可靠的批量编号协议，固定逐条翻译。</summary>
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

            // 一个客户端覆盖整次调用；代理与超时统一由 IHttpClientProvider 决定。
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
                        // HttpClient 的超时同样表现为 OperationCanceledException，但那只意味着
                        // 这一条失败，不能当成调用方取消而中断整批。
                        this.LogError("{0}: 请求超时（{1} 秒）", this.Kind, seconds);
                        results.Add(BackendHelpers.Failure(item, "请求超时"));
                    }
                    catch (OperationCanceledException)
                    {
                        // 调用方取消了整次操作：按契约向上传播。
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

        /// <summary>source 为 auto 时用 Google 的 auto；否则归一化成两字母代码。</summary>
        private static string ResolveSource(TranslationRequest request)
        {
            return BackendHelpers.NormalizeLanguage(request == null ? null : request.SourceLanguage, "auto");
        }

        /// <summary>目标语言归一化成两字母代码；配置缺失时退化为中文。</summary>
        private static string ResolveTarget(TranslationRequest request)
        {
            return BackendHelpers.NormalizeLanguage(request == null ? null : request.TargetLanguage, "zh");
        }

        private async Task<string> TranslateOneAsync(
            HttpClient client,
            string text,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            var url = BuildUrl(text, source, target);

            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false))
            {
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(string.Format(
                        "Google 返回 {0} {1}：{2}",
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        BackendHelpers.Shorten(payload)));
                }

                return ParseResponse(payload);
            }
        }

        /// <summary>拼出一次单条翻译的完整 URL，并对每个参数做 URL 编码。</summary>
        private static string BuildUrl(string text, string source, string target)
        {
            var builder = new StringBuilder(Endpoint.Length + text.Length * 3 + 64);
            builder.Append(Endpoint);
            builder.Append("?client=gtx&sl=").Append(Uri.EscapeDataString(source));
            builder.Append("&tl=").Append(Uri.EscapeDataString(target));
            builder.Append("&dt=t&q=").Append(Uri.EscapeDataString(text));
            return builder.ToString();
        }

        /// <summary>
        /// 解析 translate_a/single 的嵌套数组，把全部分段的译文拼起来。
        /// 结构与 JSON 转义交给 System.Text.Json，避免自写解析在转义上出错。
        /// </summary>
        private static string ParseResponse(string payload)
        {
            using (var document = JsonDocument.Parse(payload))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("Google 响应不是预期的嵌套数组");
                }

                var segments = root[0];
                if (segments.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("Google 响应缺少分段数组，可能触发了限流或被拦截");
                }

                var builder = new StringBuilder(payload.Length / 3 + 16);
                foreach (var segment in segments.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var piece = segment[0];
                    if (piece.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(piece.GetString());
                    }

                    // null 分段（语言探测占位）直接跳过。
                }

                if (builder.Length == 0)
                {
                    throw new InvalidOperationException("Google 响应里没有任何译文分段");
                }

                return builder.ToString();
            }
        }
    }
}
