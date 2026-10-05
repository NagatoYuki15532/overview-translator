using System;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Services;
using Emby.Plugin.OverviewTranslator.Translation.Backends;

namespace Emby.Plugin.OverviewTranslator.Translation
{
    /// <summary>
    /// Turns a <see cref="PluginConfiguration"/> into a usable translation engine.
    ///
    /// The factory is deliberately strict: when the selected engine is missing a key or
    /// an endpoint it throws <see cref="BackendConfigurationException"/> with a message
    /// meant to be shown to the user verbatim, instead of quietly falling back to another
    /// engine. Falling back would silently send the user's text somewhere they did not choose.
    /// </summary>
    public sealed class TranslationBackendFactory : ITranslationBackendFactory
    {
        /// <summary>
        /// Test-only hook: when non-null, <see cref="Create"/> returns whatever this produces
        /// and <see cref="ActiveBackend"/> reports the override's own kind. It lets the whole
        /// translation pipeline run in a console harness without Emby. Not used in production.
        /// </summary>
        internal static Func<IHttpClientProvider, MediaBrowser.Model.Logging.ILogger, ITranslationBackend> BackendOverrideForTesting = null;

        private readonly IHttpClientProvider _http;
        private readonly MediaBrowser.Model.Logging.ILogger _logger;

        public TranslationBackendFactory(IHttpClientProvider http, MediaBrowser.Model.Logging.ILogger logger)
        {
            if (http == null)
            {
                throw new ArgumentNullException("http");
            }

            _http = http;
            _logger = logger;
        }

        public TranslationBackend ActiveBackend
        {
            get
            {
                var overridden = BackendOverrideForTesting;
                if (overridden != null)
                {
                    var backend = overridden(_http, _logger);
                    return backend == null ? TranslationBackend.DeepSeek : backend.Kind;
                }

                var configuration = Plugin.CurrentConfiguration;
                return configuration == null ? TranslationBackend.DeepSeek : configuration.Backend;
            }
        }

        public ITranslationBackend Create(PluginConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            configuration.Normalize();

            var overridden = BackendOverrideForTesting;
            if (overridden != null)
            {
                var backend = overridden(_http, _logger);
                if (backend == null)
                {
                    throw new BackendConfigurationException("测试用后端注入返回了 null。");
                }

                return backend;
            }

            var context = new BackendContext
            {
                Configuration = configuration,
                Http = _http,
                Logger = _logger
            };

            switch (configuration.Backend)
            {
                case TranslationBackend.DeepSeek:
                    return new DeepSeekBackend(context);

                case TranslationBackend.OpenAiCompatible:
                    return new OpenAiCompatibleBackend(context);

                case TranslationBackend.GoogleFree:
                    return new GoogleFreeBackend(context);

                case TranslationBackend.BingFree:
                    return new BingFreeBackend(context);

                case TranslationBackend.MyMemoryFree:
                    return new MyMemoryFreeBackend(context);

                case TranslationBackend.LibreTranslate:
                    return new LibreTranslateBackend(context);

                default:
                    throw new BackendConfigurationException(
                        string.Format("未知的翻译引擎「{0}」，请在「简介翻译」设置页重新选择。", configuration.Backend));
            }
        }
    }
}
