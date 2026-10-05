using System.Net.Http;
using Emby.Plugin.OverviewTranslator.Services;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// 任意 OpenAI 兼容后端的通用实现：OpenAI、智谱、通义、one-api，以及本机的
    /// Ollama / vLLM / LM Studio 等。端点拼接与鉴权复用
    /// <see cref="ChatCompletionsBackendHelpers"/>，与 DeepSeek 完全一致。
    /// </summary>
    public sealed class OpenAiCompatibleBackend : ChatCompletionsBackendBase
    {
        /// <summary><see cref="Configuration.LlmSettings.BaseUrl"/> 留空时使用的默认地址。</summary>
        private const string DefaultBaseUrl = "https://api.openai.com/v1";

        /// <summary>
        /// 这里刻意不校验 API Key，空 key 也允许构造成功。理由：
        /// 本机部署的 Ollama / vLLM / LM Studio 通常根本不校验 Authorization，
        /// 强迫用户随便填一个假 key 只会让「测试后端」变成假失败。
        /// 空 key 时 <see cref="ApplyAuth"/> 完全不发 Authorization 头，因此也不会
        /// 出现 "Bearer " 这种让远端判成缺 token 的空头；真正需要 key 的云端服务
        /// 会在调用时返回 401，并通过 <see cref="BackendBase.TestAsync"/> 显示给用户。
        /// </summary>
        public OpenAiCompatibleBackend(BackendContext context)
            : base(context)
        {
            if (string.IsNullOrWhiteSpace(this.Settings.Model))
            {
                this.LogInfo("{0}: 未配置模型名，将按空模型名请求，远端可能返回 400。", this.Kind);
            }
        }

        public override TranslationBackend Kind
        {
            get { return TranslationBackend.OpenAiCompatible; }
        }

        /// <summary>
        /// 兼容端点的 chat completions 地址：BaseUrl（带不带尾部斜杠都可以，已含该路径也可以）+ /chat/completions。
        /// </summary>
        protected override string GetEndpoint()
        {
            return ChatCompletionsBackendHelpers.BuildEndpoint(this.Settings.BaseUrl, DefaultBaseUrl);
        }

        /// <summary>有 key 就发，没有 key 就一个头都不发（本地引擎常见情况）。</summary>
        protected override void ApplyAuth(HttpRequestMessage message)
        {
            ChatCompletionsBackendHelpers.ApplyBearer(message, this.Settings.ApiKey);
        }

        /// <summary>
        /// 这里不给默认模型：兼容端点的模型名由具体部署决定（ollama 是 qwen2.5、one-api 是
        /// 渠道里配置的名字），猜一个反而会误导。空值交给远端报错，错误会带在结果里。
        /// </summary>
        protected override string GetModel()
        {
            var model = this.Settings.Model;
            return model == null ? string.Empty : model.Trim();
        }
    }
}
