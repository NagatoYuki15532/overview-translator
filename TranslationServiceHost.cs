using System;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Services;
using Emby.Plugin.OverviewTranslator.Translation;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.OverviewTranslator
{
    /// <summary>
    /// Holds the plugin's single <see cref="ITranslationService"/> instance.
    ///
    /// Why a static host instead of container registration: Emby's plugin container does not
    /// expose a stable public "register this service" API (verified against
    /// MediaBrowser.Common 4.10.1.0 - <c>IApplicationHost</c> has no RegisterService member),
    /// and the contract for constructor-injecting plugin-defined services differs between
    /// Emby builds. A static host depends on nothing but the two Emby services this plugin
    /// actually needs, and both are handed over by <see cref="ServerEntryPoint"/> when Emby
    /// starts it.
    ///
    /// Initialisation is deliberately order-independent: the plugin constructor supplies the
    /// data path, the entry point supplies <see cref="ILibraryManager"/> and the logger, and
    /// the service is built by whichever of the two arrives last.
    /// </summary>
    public static class TranslationServiceHost
    {
        private static readonly object Gate = new object();

        private static string _dataFolderPath;
        private static ILibraryManager _libraryManager;
        private static ILogger _logger;
        private static ITranslationService _service;
        private static string _initializationError;

        /// <summary>
        /// Escape hatch for tests: when set, this is used instead of
        /// <see cref="Plugin.CurrentConfiguration"/>, so the service layer can run in a plain
        /// console harness without Emby. ThreadStatic so it can never leak between request
        /// threads; production never sets it.
        /// </summary>
        [ThreadStatic]
        private static PluginConfiguration _configurationOverride;

        /// <summary>
        /// The live configuration. All plugin code reads it from here rather than touching
        /// <see cref="Plugin.CurrentConfiguration"/> directly, which keeps exactly one place
        /// that knows how a test can substitute it.
        /// </summary>
        public static PluginConfiguration Configuration
        {
            get
            {
                var overridden = _configurationOverride;
                if (overridden != null)
                {
                    overridden.Normalize();
                    return overridden;
                }

                var fromPlugin = Plugin.CurrentConfiguration;
                fromPlugin.Normalize();
                return fromPlugin;
            }
        }

        /// <summary>
        /// Test-only: substitutes the configuration for the current thread. Passing null
        /// restores normal behaviour. Never called by production code.
        /// </summary>
        public static void SetConfigurationForTesting(PluginConfiguration configuration)
        {
            _configurationOverride = configuration;
        }

        /// <summary>
        /// The translation service. Throws with an actionable message when Emby has not
        /// finished initialising it yet, which is what a very early API call would see.
        /// </summary>
        public static ITranslationService Service
        {
            get
            {
                lock (Gate)
                {
                    if (_service == null)
                    {
                        throw new InvalidOperationException(string.IsNullOrEmpty(_initializationError)
                            ? "简介翻译插件尚未初始化完成，请稍后重试（或查看 Emby 日志中的 Overview Translator 记录）。"
                            : "简介翻译插件初始化失败: " + _initializationError);
                    }

                    return _service;
                }
            }
        }

        /// <summary>True once <see cref="Service"/> can be used.</summary>
        public static bool IsReady
        {
            get
            {
                lock (Gate)
                {
                    return _service != null;
                }
            }
        }

        /// <summary>Supplies the plugin data folder. Called from the plugin constructor.</summary>
        public static void SetDataFolder(string dataFolderPath)
        {
            lock (Gate)
            {
                _dataFolderPath = dataFolderPath;
                TryBuild();
            }
        }

        /// <summary>Supplies the Emby services the plugin needs. Called from the entry point.</summary>
        public static void SetEmbyServices(ILibraryManager libraryManager, ILogger logger)
        {
            lock (Gate)
            {
                if (libraryManager != null)
                {
                    _libraryManager = libraryManager;
                }

                if (logger != null)
                {
                    _logger = logger;
                }

                TryBuild();
            }
        }

        /// <summary>Builds the service once every dependency has arrived. Caller holds the lock.</summary>
        private static void TryBuild()
        {
            if (_service != null || _libraryManager == null)
            {
                return;
            }

            try
            {
                var http = new HttpClientProvider();
                var store = new JsonTranslationStore(_dataFolderPath, _logger);
                var writer = new MetadataWriter(_libraryManager, _logger);
                var enumerator = new EligibleItemEnumerator(_libraryManager);
                var factory = new TranslationBackendFactory(http, _logger);

                _service = new TranslationService(writer, enumerator, factory, store, _logger);
                _initializationError = null;

                if (_logger != null)
                {
                    _logger.Info("Overview Translator: 服务已就绪，翻译引擎 {0}，目标语言 {1}",
                        _service.ActiveBackend,
                        Plugin.CurrentConfiguration.TargetLanguage);
                }
            }
            catch (Exception ex)
            {
                _initializationError = ex.Message;
                if (_logger != null)
                {
                    _logger.ErrorException("Overview Translator: 初始化失败", ex);
                }
            }
        }
    }
}
