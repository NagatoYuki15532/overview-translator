using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Services;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.OverviewTranslator.Tasks
{
    /// <summary>
    /// The batch job that appears under Emby's scheduled tasks.
    ///
    /// It translates every episode whose overview is not yet in the target language, up to
    /// the configured per-run cap. It is safe to run repeatedly: already translated items are
    /// served from the local translation memory and cost nothing.
    /// </summary>
    public sealed class TranslateOverviewTask : IScheduledTask
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Only the logger is injected. The translation service is taken from
        /// <see cref="TranslationServiceHost"/> so Emby does not have to resolve a
        /// plugin-defined type from its own container.
        /// </summary>
        public TranslateOverviewTask(ILogger logger)
        {
            _logger = logger;
        }

        private static ITranslationService Service
        {
            get { return TranslationServiceHost.Service; }
        }

        public string Name
        {
            get { return "翻译剧集简介"; }
        }

        public string Key
        {
            get { return "OverviewTranslatorTranslateTask"; }
        }

        public string Description
        {
            get
            {
                try
                {
                    var pending = Service.CountPendingAsync(CancellationToken.None).GetAwaiter().GetResult();
                    return string.Format(
                        "把所有还没有中文简介的剧集简介翻译成 {0}，当前待翻译 {1} 个。已翻译过的条目会直接复用本地缓存，不会重复消耗额度。",
                        Plugin.CurrentConfiguration.TargetLanguage,
                        pending);
                }
                catch (Exception)
                {
                    return "把所有还没有中文简介的剧集简介翻译成目标语言。";
                }
            }
        }

        public string Category
        {
            get { return "简介翻译"; }
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            Action<string, double> onProgress = null;
            if (progress != null)
            {
                onProgress = (message, percent) =>
                {
                    try
                    {
                        progress.Report(percent);
                    }
                    catch (Exception)
                    {
                        // Reporting failures must never abort a run.
                    }

                    if (_logger != null && !string.IsNullOrWhiteSpace(message))
                    {
                        _logger.Info("[简介翻译] {0}", message);
                    }
                };
            }

            var report = await Service.TranslateLibraryAsync(onProgress, cancellationToken)
                .ConfigureAwait(false);

            if (_logger != null)
            {
                _logger.Info(
                    "[简介翻译] 任务结束：检查 {0}，翻译 {1}，写回 {2}，跳过 {3}，失败 {4}{5}",
                    report.Examined,
                    report.Translated,
                    report.Saved,
                    report.Skipped,
                    report.Failed,
                    report.StoppedEarly ? "（已到达单次上限，剩余留到下次）" : string.Empty);
            }

            if (progress != null)
            {
                progress.Report(100);
            }
        }

        /// <summary>
        /// The default trigger comes from configuration, so a configured interval works whether
        /// Emby auto-discovers this task or <see cref="ServerEntryPoint"/> adds it explicitly.
        /// An interval of 0 means "manual runs only", which is the default.
        ///
        /// Emby stores triggers when it first registers the task, so changing the interval needs
        /// a restart (or an edit in the scheduled-tasks page) to take effect.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var triggers = new List<TaskTriggerInfo>();

            try
            {
                var minutes = Plugin.CurrentConfiguration.IntervalMinutes;
                if (minutes > 0)
                {
                    triggers.Add(new TaskTriggerInfo
                    {
                        Type = TaskTriggerInfo.TriggerInterval,
                        IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
                    });
                }
            }
            catch (Exception)
            {
                // Before the plugin is fully initialised there is no configuration to read;
                // "no trigger" is the safe default.
            }

            return triggers;
        }
    }
}
