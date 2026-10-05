using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Plugin.OverviewTranslator.Translation
{
    /// <summary>
    /// Which translation engine produces the target text.
    /// The value is persisted by name, so never rename an existing member.
    /// </summary>
    public enum TranslationBackend
    {
        /// <summary>DeepSeek chat completions (api.deepseek.com).</summary>
        DeepSeek = 0,

        /// <summary>Any OpenAI-compatible /chat/completions endpoint (OpenAI, Zhipu, Qwen, Ollama, one-api, ...).</summary>
        OpenAiCompatible = 1,

        /// <summary>Google's unofficial translate endpoint (translate.googleapis.com). No API key.</summary>
        GoogleFree = 2,

        /// <summary>Bing/Microsoft translator via the unofficial edge endpoint. No API key.</summary>
        BingFree = 3,

        /// <summary>MyMemory translation memory API. No API key.</summary>
        MyMemoryFree = 4,

        /// <summary>A locally hosted LibreTranslate instance.</summary>
        LibreTranslate = 5
    }

    /// <summary>One overview to translate.</summary>
    public sealed class TranslationItem
    {
        /// <summary>Stable identity of the Emby item. Used for cache keys and reporting only.</summary>
        public string ItemId { get; set; }

        /// <summary>Source text. Never null; callers must skip empty strings themselves.</summary>
        public string Text { get; set; }

        /// <summary>Optional hint, e.g. the series name. Backends may use it as prompt context.</summary>
        public string Context { get; set; }
    }

    /// <summary>Result for one <see cref="TranslationItem"/>.</summary>
    public sealed class TranslationResult
    {
        /// <summary>Echoes <see cref="TranslationItem.ItemId"/>.</summary>
        public string ItemId { get; set; }

        /// <summary>Translated text, or null when the backend failed for this item.</summary>
        public string TranslatedText { get; set; }

        /// <summary>Null on success, otherwise a short human-readable reason.</summary>
        public string Error { get; set; }

        public bool Succeeded
        {
            get { return string.IsNullOrWhiteSpace(this.Error) && !string.IsNullOrWhiteSpace(this.TranslatedText); }
        }
    }

    /// <summary>Per-call backend options.</summary>
    public sealed class TranslationRequest
    {
        /// <summary>BCP-47-ish source language code, or "auto".</summary>
        public string SourceLanguage { get; set; }

        /// <summary>BCP-47-ish target language code, e.g. "zh-CN".</summary>
        public string TargetLanguage { get; set; }

        /// <summary>Free-form extra instruction appended to the LLM prompt. May be null.</summary>
        public string ExtraPrompt { get; set; }
    }

    /// <summary>
    /// A translation engine. Implementations must be stateless and safe to call concurrently;
    /// the service resolves one instance per call.
    /// </summary>
    public interface ITranslationBackend
    {
        /// <summary>Stable identifier used in logs and the config UI.</summary>
        TranslationBackend Kind { get; }

        /// <summary>
        /// False when the backend must receive exactly one item per call.
        /// When true the service may pass several items in one request to save money and time.
        /// </summary>
        bool SupportsBatch { get; }

        /// <summary>
        /// Translates every item. Must never throw for per-item failures: report them through
        /// <see cref="TranslationResult.Error"/> instead. Throwing aborts the whole call.
        /// The returned list must contain exactly one entry per input item, in input order.
        /// </summary>
        Task<IList<TranslationResult>> TranslateAsync(
            IList<TranslationItem> items,
            TranslationRequest request,
            CancellationToken cancellationToken);

        /// <summary>
        /// Cheap connectivity/credential check used by the "test backend" button in the UI.
        /// Must not throw. Returns null when the backend works, otherwise the reason it does not.
        /// </summary>
        Task<string> TestAsync(CancellationToken cancellationToken);
    }
}
