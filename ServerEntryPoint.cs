using System;
using System.Collections.Generic;
using Emby.Plugin.OverviewTranslator.Services;
using Emby.Plugin.OverviewTranslator.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.OverviewTranslator
{
    /// <summary>
    /// Hands the plugin the two Emby services it needs and registers the scheduled task.
    ///
    /// Emby constructs every <see cref="IServerEntryPoint"/> through its own container, so the
    /// constructor parameters here are resolved by Emby. The plugin's own services are then
    /// built by <see cref="TranslationServiceHost"/>, which keeps the plugin independent of
    /// Emby's (version-specific) service-registration API.
    /// </summary>
    public sealed class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly ITaskManager _taskManager;
        private readonly MediaBrowser.Controller.Providers.IProviderManager _providerManager;

        /// <summary>Listens for new/refreshed items and translates them. Null when not started.</summary>
        private AutoTranslateOnLibraryEvents _autoTranslate;

        public ServerEntryPoint(
            ILibraryManager libraryManager,
            ILogger logger,
            ITaskManager taskManager,
            MediaBrowser.Controller.Providers.IProviderManager providerManager)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _taskManager = taskManager;
            _providerManager = providerManager;
        }

        /// <summary>The running auto-translate listener, or null. Exposed for the status endpoint.</summary>
        public static AutoTranslateOnLibraryEvents AutoTranslate { get; private set; }

        public void Run()
        {
            // The plugin's constructor deliberately does not touch Configuration or the data
            // path: BasePlugin.AssemblyFilePath is assigned by Emby only *after* construction,
            // so reading Configuration there throws and the plugin fails to load (confirmed
            // against a real Emby 4.10.1.0 instance). An entry point runs after the plugin is
            // fully initialised, which makes this the right place for both calls.
            if (Plugin.Instance != null)
            {
                TranslationServiceHost.SetDataFolder(Plugin.Instance.TranslationDataPath);
            }

            TranslationServiceHost.SetEmbyServices(_libraryManager, _logger);

            if (_logger != null)
            {
                _logger.Info("Overview Translator: 启动完成（Emby 插件容器已注入 ILibraryManager / ITaskManager）");
            }

            RegisterScheduledTask();
            StartAutoTranslate();
        }

        /// <summary>
        /// Subscribes to library events so new episodes and refreshed metadata get translated
        /// without the user pressing anything. Costs nothing while the option is off: the
        /// listener can stay subscribed and decides per event.
        /// </summary>
        private void StartAutoTranslate()
        {
            try
            {
                _autoTranslate = new AutoTranslateOnLibraryEvents(_libraryManager, _providerManager, _logger);
                AutoTranslate = _autoTranslate;
                _autoTranslate.Start();

                var configuration = Plugin.CurrentConfiguration;
                if (_logger != null)
                {
                    _logger.Info(
                        "Overview Translator: 自动翻译开关 = {0}（{1}）",
                        configuration.AutoTranslateOnNewItems ? "开" : "关",
                        configuration.AutoTranslateOnNewItems
                            ? string.Format("新入库/刷新后延迟 {0} 秒翻译", configuration.AutoTranslateDelaySeconds)
                            : "如需开启请在设置页勾选「新入库/元数据刷新时自动翻译」");
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.ErrorException("Overview Translator: 启动自动翻译监听失败", ex);
                }
            }
        }

        /// <summary>
        /// Makes sure the translation task is registered, without registering it twice.
        ///
        /// Emby discovers every public <see cref="IScheduledTask"/> implementation in a plugin
        /// assembly and registers it itself, so an unconditional AddTasks() puts the SAME
        /// instance in the list a second time (observed live: two entries with an identical id).
        /// So this only adds the task when Emby has not already provided it, and reports the
        /// configured interval either way.
        /// </summary>
        private void RegisterScheduledTask()
        {
            if (_taskManager == null)
            {
                if (_logger != null)
                {
                    _logger.Warn("Overview Translator: 未能获得 ITaskManager，计划任务未注册（可在 Emby 计划任务页手动添加）。");
                }

                return;
            }

            try
            {
                var intervalMinutes = Plugin.CurrentConfiguration.IntervalMinutes;
                var task = new TranslateOverviewTask(_logger);

                var alreadyRegistered = false;
                var existing = _taskManager.ScheduledTasks;
                if (existing != null)
                {
                    foreach (var worker in existing)
                    {
                        var scheduled = worker == null ? null : worker.ScheduledTask;
                        if (scheduled != null
                            && string.Equals(scheduled.Key, task.Key, StringComparison.Ordinal))
                        {
                            alreadyRegistered = true;
                            break;
                        }
                    }
                }

                if (alreadyRegistered)
                {
                    if (_logger != null)
                    {
                        _logger.Info(
                            "Overview Translator: 计划任务「{0}」已由 Emby 注册，未重复添加（间隔 {1} 分钟；0 表示只在手动运行时执行）",
                            task.Name,
                            intervalMinutes);
                    }

                    return;
                }

                _taskManager.AddTasks(new IScheduledTask[] { task });

                if (_logger != null)
                {
                    _logger.Info(
                        "Overview Translator: 已注册计划任务「{0}」（间隔 {1} 分钟；0 表示只在手动运行时执行）",
                        task.Name,
                        intervalMinutes);
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.ErrorException("Overview Translator: 注册计划任务失败", ex);
                }
            }
        }

        public void Dispose()
        {
            // Unhook from Emby's library events; a leaked subscription would keep this plugin
            // alive across a plugin reload.
            var listener = _autoTranslate;
            if (listener != null)
            {
                listener.Dispose();
                _autoTranslate = null;
            }

            if (ReferenceEquals(AutoTranslate, listener))
            {
                AutoTranslate = null;
            }
        }
    }
}
