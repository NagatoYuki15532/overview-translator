using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.OverviewTranslator
{
    /// <summary>
    /// Translates newly added or freshly refreshed items automatically.
    ///
    /// Which events are used, and why:
    ///
    ///  * <c>IProviderManager.RefreshCompleted</c> — the reliable trigger. It fires after Emby has
    ///    finished writing metadata, so the overview is actually present.
    ///  * <c>ILibraryManager.ItemAdded</c> — catches a brand new episode so it is queued even if
    ///    nothing refreshes it.
    ///
    /// <c>ILibraryManager.ItemUpdated</c> was the obvious-looking choice and turned out to be
    /// useless here: a refresh that finds nothing new does not update the item, so it never
    /// raises the event (measured: "RefreshItem Start/Complete" in 1 ms with no ItemUpdated and
    /// therefore no translation).
    ///
    /// Why the work is queued instead of done in the handler:
    ///
    ///  * Batching. A library scan completes dozens of refreshes in a row; debouncing collapses
    ///    them into one run instead of one translation per item.
    ///  * Thread safety. The events fire on Emby's threads; translation does network I/O and a
    ///    database write. The handler only enqueues; the work runs on a background task. An
    ///    exception escaping a subscriber would propagate into Emby's refresh pipeline, so the
    ///    handler catches everything.
    ///  * Duplicates. Both events can fire for the same item; a set keyed by item id collapses it.
    ///
    /// All actual work goes through <see cref="ITranslationService"/>, so the translation memory
    /// and the "already translated" guard apply exactly as they do for a manual run.
    /// </summary>
    public sealed class AutoTranslateOnLibraryEvents : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly ILogger _logger;

        /// <summary>Item ids waiting to be translated. A set, so repeated events collapse.</summary>
        private readonly ConcurrentDictionary<string, byte> _pending =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private readonly object _timerGate = new object();

        private Timer _timer;
        private bool _running;
        private bool _disposed;
        private long _translatedTotal;

        public AutoTranslateOnLibraryEvents(ILibraryManager libraryManager, IProviderManager providerManager, ILogger logger)
        {
            if (libraryManager == null)
            {
                throw new ArgumentNullException("libraryManager");
            }

            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _logger = logger;
        }

        /// <summary>How many items this component has translated since Emby started.</summary>
        public long TranslatedTotal
        {
            get { return Interlocked.Read(ref _translatedTotal); }
        }

        /// <summary>How many item ids are currently queued.</summary>
        public int PendingCount
        {
            get { return _pending.Count; }
        }

        /// <summary>Starts listening. Safe to call once from the entry point.</summary>
        public void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("AutoTranslateOnLibraryEvents");
            }

            // RefreshCompleted is the trigger that actually fires; ItemAdded covers an episode
            // that appears without a refresh. ItemUpdated is deliberately NOT used (see the
            // class comment: a no-op refresh never raises it).
            _libraryManager.ItemAdded += this.OnItemAdded;

            if (_providerManager != null)
            {
                _providerManager.RefreshCompleted += this.OnRefreshCompleted;
            }

            this.Log("已订阅媒体库事件（新入库 + 元数据刷新完成），自动翻译将在防抖延迟后执行");
        }

        /// <summary>Queues a newly added item.</summary>
        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            this.Queue(e == null ? null : e.Item);
        }

        /// <summary>Queues the item a finished metadata refresh produced.</summary>
        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            var info = e == null ? null : e.Argument;
            this.Queue(info == null ? null : info.Item);
        }

        /// <summary>
        /// Decides whether an item should be translated, and queues it. Must never throw and must
        /// never block: it only adds an id and arms the debounce timer.
        /// </summary>
        private void Queue(BaseItem item)
        {
            try
            {
                if (_disposed || item == null)
                {
                    return;
                }

                var configuration = TranslationServiceHost.Configuration;
                if (configuration == null || !configuration.Enabled || !configuration.AutoTranslateOnNewItems)
                {
                    return;
                }

                if (!IsEligible(item, configuration))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(item.Overview))
                {
                    // No overview yet. When a provider writes one, RefreshCompleted fires again
                    // and the item is queued then.
                    return;
                }

                if (MetadataWriter.IsAlreadyInTargetLanguage(item.Overview, configuration.TargetLanguage))
                {
                    return;
                }

                _pending[item.Id.ToString()] = 1;
                this.Arm();
            }
            catch (Exception ex)
            {
                // Never let an exception escape into Emby's refresh pipeline.
                this.LogError("处理媒体库事件时出错（已忽略）：" + ex.Message);
            }
        }

        /// <summary>True when the item is one this plugin translates.</summary>
        private static bool IsEligible(BaseItem item, PluginConfiguration configuration)
        {
            var allowed = configuration.ItemTypes;
            if (allowed == null || allowed.Count == 0)
            {
                return false;
            }

            var typeName = item.GetType().Name;
            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Restarts the debounce timer so a burst of events results in one run.</summary>
        private void Arm()
        {
            TimeSpan delay;
            try
            {
                var seconds = TranslationServiceHost.Configuration.AutoTranslateDelaySeconds;
                if (seconds < 5)
                {
                    // A near-zero delay would read overviews the provider has not written yet.
                    seconds = 5;
                }
                else if (seconds > 86400)
                {
                    seconds = 86400;
                }

                delay = TimeSpan.FromSeconds(seconds);
            }
            catch (Exception)
            {
                delay = TimeSpan.FromMinutes(5);
            }

            lock (_timerGate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_timer == null)
                {
                    _timer = new Timer(this.OnDebounceElapsed, null, delay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _timer.Change(delay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>Debounce fired: take everything queued and process it on a background task.</summary>
        private void OnDebounceElapsed(object state)
        {
            if (_disposed || _running)
            {
                // A run is already in flight; its own completion re-arms if more arrived.
                return;
            }

            _running = true;

            Task.Run(async () =>
            {
                try
                {
                    await this.DrainAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.LogError("自动翻译执行失败：" + ex.Message);
                }
                finally
                {
                    _running = false;

                    if (!_pending.IsEmpty && !_disposed)
                    {
                        // Items arrived while we were working: debounce again rather than spin.
                        this.Arm();
                    }
                }
            });
        }

        /// <summary>Translates every queued item, one at a time.</summary>
        private async Task DrainAsync()
        {
            if (!TranslationServiceHost.IsReady)
            {
                // Emby has not finished starting the plugin services. Put everything back and
                // try again shortly instead of dropping the items.
                this.Log("翻译服务尚未就绪，自动翻译稍后重试");
                this.Arm();
                return;
            }

            var service = TranslationServiceHost.Service;
            var translated = 0;
            var failed = 0;
            var skipped = 0;

            foreach (var key in _pending.Keys)
            {
                byte ignored;
                if (!_pending.TryRemove(key, out ignored))
                {
                    continue;
                }

                if (_disposed)
                {
                    return;
                }

                try
                {
                    var outcome = await service
                        .TranslateAndSaveItemAsync(key, false, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (outcome.Saved)
                    {
                        translated++;
                        Interlocked.Increment(ref _translatedTotal);
                        this.Log(string.Format(
                            "自动翻译完成：{0}（第 {1} 集）",
                            string.IsNullOrWhiteSpace(outcome.TranslatedText) ? key : Summarize(outcome.TranslatedText),
                            key));
                    }
                    else if (outcome.Skipped)
                    {
                        skipped++;
                    }
                    else
                    {
                        failed++;
                        this.Log(string.Format("自动翻译未成功（{0}）：{1}", key, outcome.Reason));
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    this.LogError(string.Format("自动翻译条目 {0} 时出错：{1}", key, ex.Message));
                }

                // Small pause so a large burst cannot hammer the translation API.
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }

            this.Log(string.Format(
                "自动翻译本轮结束：成功 {0}，跳过 {1}，失败 {2}，累计成功 {3}",
                translated,
                skipped,
                failed,
                Interlocked.Read(ref _translatedTotal)));
        }

        private static string Summarize(string text)
        {
            var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= 24 ? oneLine : oneLine.Substring(0, 24) + "…";
        }

        private void Log(string message)
        {
            if (_logger != null)
            {
                _logger.Info("Overview Translator: " + message);
            }
        }

        private void LogError(string message)
        {
            if (_logger != null)
            {
                _logger.Error("Overview Translator: " + message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _libraryManager.ItemAdded -= this.OnItemAdded;

                if (_providerManager != null)
                {
                    _providerManager.RefreshCompleted -= this.OnRefreshCompleted;
                }
            }
            catch (Exception)
            {
                // Unsubscribing must never throw during shutdown.
            }

            lock (_timerGate)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }

            _pending.Clear();
        }
    }
}
