using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Translation;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.OverviewTranslator.Services
{
    /// <summary>
    /// Translates a single string through the configured backend, consulting the translation
    /// memory first.
    ///
    /// This lives apart from <see cref="TranslationService"/> on purpose: it needs no
    /// <see cref="MetadataWriter"/> and therefore no Emby, which keeps the network + cache path
    /// testable in a plain console harness and keeps the item/bookkeeping concerns out of the
    /// request path used by the REST API.
    /// </summary>
    public sealed class TextTranslator
    {
        private readonly ITranslationBackendFactory _factory;
        private readonly ITranslationStore _store;
        private readonly ILogger _logger;

        public TextTranslator(ITranslationBackendFactory factory, ITranslationStore store, ILogger logger)
        {
            if (factory == null)
            {
                throw new ArgumentNullException("factory");
            }

            if (store == null)
            {
                throw new ArgumentNullException("store");
            }

            _factory = factory;
            _store = store;
            _logger = logger;
        }

        /// <summary>
        /// Translates <paramref name="text"/>. Never throws for backend problems: the failure
        /// is reported through <see cref="TranslationResult.Error"/>. A result whose Error is
        /// null AND whose text is null means the plugin is disabled (callers treat that as
        /// "skipped").
        /// </summary>
        public async Task<TranslationResult> TranslateAsync(
            string text,
            string context,
            PluginConfiguration configuration,
            bool force,
            CancellationToken cancellationToken)
        {
            if (configuration == null)
            {
                return new TranslationResult { Error = "插件配置尚未加载完成。" };
            }

            if (!configuration.Enabled)
            {
                return new TranslationResult();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new TranslationResult { Error = "没有可翻译的文本。" };
            }

            var target = string.IsNullOrWhiteSpace(configuration.TargetLanguage) ? "zh-CN" : configuration.TargetLanguage;

            if (!force)
            {
                var cached = _store.GetCached(text, configuration.Backend, target);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    this.LogDebug("命中翻译缓存（长度 {0}）", cached.Length);
                    return new TranslationResult { TranslatedText = cached };
                }
            }

            ITranslationBackend backend;
            try
            {
                backend = _factory.Create(configuration);
            }
            catch (BackendConfigurationException ex)
            {
                return new TranslationResult { Error = ex.Message };
            }

            var item = new TranslationItem
            {
                ItemId = "adhoc",
                Text = text,
                Context = context
            };

            var request = new TranslationRequest
            {
                SourceLanguage = configuration.SourceLanguage,
                TargetLanguage = target,
                ExtraPrompt = configuration.Llm.ExtraPrompt
            };

            var results = await backend.TranslateAsync(new[] { item }, request, cancellationToken).ConfigureAwait(false);
            var result = results != null && results.Count > 0 ? results[0] : null;

            if (result == null)
            {
                return new TranslationResult { Error = "翻译后端没有返回结果" };
            }

            if (result.Succeeded && !IsEffectiveTranslation(text, result.TranslatedText))
            {
                // A backend that echoes the input has NOT translated anything. Accepting that as
                // success would mark the item as done (and poison the cache) while the user still
                // sees the source language. MyMemory does exactly this when the source and target
                // languages are wrong, e.g. langpair=en|zh on Japanese text.
                this.LogWarn("翻译结果与原文相同，判定为未翻译（引擎 {0}，源语言 {1}）",
                    configuration.Backend,
                    configuration.SourceLanguage);

                return new TranslationResult
                {
                    ItemId = item.ItemId,
                    Error = "翻译引擎返回的内容与原文相同，未产生译文。请检查「源语言」设置是否正确（有些引擎不支持 auto），或改用其它引擎。"
                };
            }

            if (result.Succeeded)
            {
                _store.PutCached(text, result.TranslatedText, configuration.Backend, target);
            }

            return result;
        }

        /// <summary>
        /// True when the translation actually differs from the source in a meaningful way.
        /// Compares after removing markup and collapsing whitespace, so a backend that merely
        /// re-wrapped the text is still recognised as "not translated".
        /// </summary>
        public static bool IsEffectiveTranslation(string source, string translated)
        {
            if (string.IsNullOrWhiteSpace(translated))
            {
                return false;
            }

            var a = NormalizeForComparison(source);
            var b = NormalizeForComparison(translated);

            if (a.Length == 0)
            {
                return true;
            }

            return !string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string NormalizeForComparison(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // Drop the markup Emby overviews often contain, then collapse all whitespace.
            var withoutTags = System.Text.RegularExpressions.Regex.Replace(value, "<[^>]*>", " ");
            var builder = new System.Text.StringBuilder(withoutTags.Length);
            var lastWasSpace = false;

            foreach (var c in withoutTags)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                builder.Append(c);
                lastWasSpace = false;
            }

            return builder.ToString().Trim();
        }

        private void LogDebug(string message, params object[] args)
        {
            if (_logger != null)
            {
                _logger.Debug(message, args);
            }
        }

        private void LogWarn(string message, params object[] args)
        {
            if (_logger != null)
            {
                _logger.Warn(message, args);
            }
        }
    }
}
