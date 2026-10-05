using System;
using System.Net.Http;
using Emby.Plugin.OverviewTranslator.Services;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// DeepSeek 官方 chat/completions 后端。提示词、请求体 JSON、响应解析与批量协议
    /// 全部由 <see cref="ChatCompletionsBackendBase"/> 负责，这里只提供端点、鉴权与默认值。
    /// </summary>
    public sealed class DeepSeekBackend : ChatCompletionsBackendBase
    {
        /// <summary><see cref="Configuration.LlmSettings.BaseUrl"/> 留空时使用的 DeepSeek 官方地址。</summary>
        private const string DefaultBaseUrl = "https://api.deepseek.com/v1";

        /// <summary><see cref="Configuration.LlmSettings.Model"/> 留空时使用的模型。</summary>
        private const string DefaultModel = "deepseek-chat";

        /// <summary>
        /// DeepSeek 是需要计费的云服务，没有 API Key 一定调用不通，所以在这里就把配置问题
        /// 抛给用户，而不是等任务跑起来、翻日志才发现。
        /// </summary>
        public DeepSeekBackend(BackendContext context)
            : base(context)
        {
            if (string.IsNullOrWhiteSpace(this.Settings.ApiKey))
            {
                throw new BackendConfigurationException(
                    "DeepSeek 后端缺少 API Key，请在「简介翻译」设置页填写。");
            }
        }

        public override TranslationBackend Kind
        {
            get { return TranslationBackend.DeepSeek; }
        }

        /// <summary>
        /// DeepSeek 的 chat completions 地址：BaseUrl（带不带尾部斜杠都可以，已含该路径也可以）+ /chat/completions。
        /// </summary>
        protected override string GetEndpoint()
        {
            return ChatCompletionsBackendHelpers.BuildEndpoint(this.Settings.BaseUrl, DefaultBaseUrl);
        }

        /// <summary>只有确实拿到令牌时才写 Authorization 头，空令牌必须完全不发该头。</summary>
        protected override void ApplyAuth(HttpRequestMessage message)
        {
            ChatCompletionsBackendHelpers.ApplyBearer(message, this.Settings.ApiKey);
        }

        /// <summary>Model 未配置时退回 deepseek-chat。不写回配置对象，避免影响持久化结果。</summary>
        protected override string GetModel()
        {
            var model = this.Settings.Model;
            return string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        }
    }

    /// <summary>
    /// chat/completions 家族共用的端点拼接与鉴权工具。
    ///
    /// 之所以放在这里而不是基类：基类由其他成员维护，本任务只能继承。两个子类共用
    /// 同一份实现，可以避免端点拼接规则被各写一遍后走样（例如把
    /// <c>.../chat/completions/chat/completions</c> 发出去）。
    /// </summary>
    internal static class ChatCompletionsBackendHelpers
    {
        /// <summary>chat completions 的固定路径部分。</summary>
        private const string ChatCompletionsPath = "/chat/completions";

        /// <summary>
        /// 把用户填写的 BaseUrl 规范成完整的 chat completions URL。
        /// 支持：留空（用默认值）、带尾部斜杠、不带尾部斜杠、已经包含 /chat/completions。
        /// </summary>
        internal static string BuildEndpoint(string baseUrl, string defaultBaseUrl)
        {
            var value = (baseUrl ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                value = defaultBaseUrl;
            }

            // 统一去掉尾部斜杠，路径由本方法自己拼，避免出现 "//chat/completions"。
            value = value.TrimEnd('/');
            if (value.Length == 0)
            {
                value = defaultBaseUrl.TrimEnd('/');
            }

            // 用户可能把完整地址粘进来（含 /chat/completions），也可能它本来就以该路径结尾。
            // 这种情况绝不能再拼一次，否则远端只会回 404。
            var marker = value.IndexOf(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var remainder = value.Substring(marker + ChatCompletionsPath.Length);

                // 路径之后只允许跟查询串，例如 .../chat/completions?api-version=2024-02-01。
                if (remainder.Length == 0 || remainder[0] == '/' || remainder[0] == '?')
                {
                    return value;
                }
            }

            return value + ChatCompletionsPath;
        }

        /// <summary>
        /// 用 <see cref="AuthHeader.BuildBearer"/> 的结果设置 Authorization 头。
        /// 返回 null 表示没有可用令牌：此时必须一个字节都不发，既不能写空头，
        /// 也不能写 "Bearer "（本机曾因此收到 401 "missing token"）。
        /// </summary>
        internal static void ApplyBearer(HttpRequestMessage message, string apiKey)
        {
            if (message == null)
            {
                return;
            }

            // 用户可能只把 scheme 粘进了 API Key 框（"Bearer " / "bearer"）。
            // AuthHeader.BuildBearer 先 Trim 再判断前缀，会把这种输入当成一个叫
            // "Bearer" 的 token，产出 "Bearer Bearer" —— 那同样不是一个可用令牌。
            // 这里按“缺 token”处理：一个头都不发，而不是发一个必然 401 的头。
            // （helper 里才是根因的修法，但 helper 由其他成员负责，这里只能自保。）
            var candidate = (apiKey ?? string.Empty).Trim();
            if (candidate.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var header = AuthHeader.BuildBearer(apiKey);
            if (header == null)
            {
                return;
            }

            // 用不校验的写法，避免用户粘贴的奇怪 key 在构造请求阶段就抛异常；
            // 真的不合法时由远端返回明确错误，而不是整个任务崩掉。
            message.Headers.TryAddWithoutValidation("Authorization", header);
        }
    }
}
