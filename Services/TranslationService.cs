using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.OverviewTranslator.Services
{
    /// <summary>
    /// The single entry point the scheduled task and the REST API use.
    ///
    /// Responsibilities, in order: decide what still needs translating, ask the configured
    /// backend (through the cache), write the result into Emby, and record what happened.
    /// Everything else in the plugin is either a backend or a thin shell around this.
    /// </summary>
    public sealed class TranslationService : ITranslationService
    {
        private readonly MetadataWriter _writer;
        private readonly EligibleItemEnumerator _enumerator;
        private readonly ITranslationBackendFactory _factory;
        private readonly ITranslationStore _store;
        private readonly ILogger _logger;

        /// <summary>Shared by the task and the API so two runs cannot overlap.</summary>
        private static readonly SemaphoreSlim RunGate = new SemaphoreSlim(1, 1);

        /// <summary>Detailed result of the last batch run, for the status endpoint.</summary>
        private static BatchReport _lastReport;

        private static DateTime _lastRunUtc;

        /// <summary>Owns the "translate one string, with cache" path. Kept separate so it can be
        /// exercised without a MetadataWriter (and therefore without Emby present).</summary>
        private readonly TextTranslator _textTranslator;

        public TranslationService(
            MetadataWriter writer,
            EligibleItemEnumerator enumerator,
            ITranslationBackendFactory factory,
            ITranslationStore store,
            ILogger logger)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            if (enumerator == null)
            {
                throw new ArgumentNullException("enumerator");
            }

            if (factory == null)
            {
                throw new ArgumentNullException("factory");
            }

            if (store == null)
            {
                throw new ArgumentNullException("store");
            }

            _writer = writer;
            _enumerator = enumerator;
            _factory = factory;
            _store = store;
            _logger = logger;
            _textTranslator = new TextTranslator(factory, store, logger);
        }

        public TranslationBackend ActiveBackend
        {
            get { return _factory.ActiveBackend; }
        }

        /// <summary>The most recent batch report, or null when no run has happened yet.</summary>
        public static BatchReport LastReport
        {
            get { return _lastReport; }
        }

        public static DateTime LastRunUtc
        {
            get { return _lastRunUtc; }
        }

        public async Task<string> TranslateTextAsync(string text, string context, CancellationToken cancellationToken)
        {
            var configuration = TranslationServiceHost.Configuration;

            var result = await _textTranslator.TranslateAsync(text, context, configuration, force: false, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Error);
            }

            return result.TranslatedText;
        }

        public Task<TranslationResult> TranslateTextWithResultAsync(
            string text,
            string context,
            PluginConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (configuration == null)
            {
                return Task.FromResult(new TranslationResult
                {
                    Error = "插件配置尚未加载完成，请稍后重试。"
                });
            }

            configuration.Normalize();
            return _textTranslator.TranslateAsync(text, context, configuration, force: false, cancellationToken);
        }

        public async Task<ItemTranslationOutcome> TranslateAndSaveItemAsync(
            string itemId,
            bool force,
            CancellationToken cancellationToken)
        {
            var outcome = new ItemTranslationOutcome { ItemId = itemId };

            var configuration = TranslationServiceHost.Configuration;
            if (configuration == null)
            {
                outcome.Reason = "插件配置尚未加载完成";
                return outcome;
            }

            configuration.Normalize();

            var item = _writer.GetItem(itemId);
            if (item == null)
            {
                outcome.Reason = "条目不存在或已被移除";
                return outcome;
            }

            var current = item.Overview ?? string.Empty;
            outcome.OriginalText = current;

            if (string.IsNullOrWhiteSpace(current))
            {
                outcome.Skipped = true;
                outcome.Reason = "该条目没有简介，无需翻译";
                return outcome;
            }

            if (MetadataWriter.IsAlreadyInTargetLanguage(current, configuration.TargetLanguage))
            {
                outcome.Skipped = true;
                outcome.Reason = "简介已经是目标语言";
                return outcome;
            }

            // Already translated from this exact source text: re-apply without paying again.
            if (!force)
            {
                var record = _store.GetItemRecord(itemId);
                if (record != null && string.Equals(record.SourceHash, JsonTranslationStore.HashSource(current), StringComparison.Ordinal))
                {
                    outcome.Translated = true;
                    outcome.TranslatedText = record.TranslatedText;
                    outcome.Saved = _writer.WriteOverview(item, record.TranslatedText);
                    outcome.Reason = outcome.Saved ? "使用已缓存译文" : "缓存译文写回失败";
                    return outcome;
                }
            }

            var translation = await _textTranslator.TranslateAsync(
                current,
                MetadataWriter.GetSeriesTitle(item),
                configuration,
                force,
                cancellationToken).ConfigureAwait(false);

            if (!translation.Succeeded)
            {
                if (translation.Error == null)
                {
                    // TranslateOneAsync reports unusable configuration this way.
                    outcome.Skipped = true;
                    outcome.Reason = "插件未启用或未配置翻译引擎";
                    return outcome;
                }

                outcome.Reason = translation.Error;
                return outcome;
            }

            outcome.Translated = true;
            outcome.TranslatedText = translation.TranslatedText;
            outcome.Saved = _writer.WriteOverview(item, translation.TranslatedText);

            if (outcome.Saved)
            {
                _store.RecordItem(itemId, JsonTranslationStore.HashSource(current), configuration.TargetLanguage, translation.TranslatedText);
                _store.Flush();
            }
            else
            {
                outcome.Reason = "译文写入 Emby 失败";
            }

            return outcome;
        }

        public async Task<BatchReport> TranslateLibraryAsync(Action<string, double> onProgress, CancellationToken cancellationToken)
        {
            await RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunBatchAsync(onProgress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                RunGate.Release();
            }
        }

        public Task<int> CountPendingAsync(CancellationToken cancellationToken)
        {
            var configuration = TranslationServiceHost.Configuration;
            if (configuration == null)
            {
                return Task.FromResult(0);
            }

            configuration.Normalize();

            var count = 0;
            foreach (var item in _enumerator.Enumerate(configuration))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var overview = item.Overview;
                if (string.IsNullOrWhiteSpace(overview))
                {
                    continue;
                }

                if (MetadataWriter.IsAlreadyInTargetLanguage(overview, configuration.TargetLanguage))
                {
                    continue;
                }

                count++;
            }

            return Task.FromResult(count);
        }

        public async Task<string> TestBackendAsync(CancellationToken cancellationToken)
        {
            var configuration = TranslationServiceHost.Configuration;
            if (configuration == null)
            {
                return "插件配置尚未加载完成";
            }

            try
            {
                configuration.Normalize();
                var backend = _factory.Create(configuration);
                return await backend.TestAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (BackendConfigurationException ex)
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private async Task<BatchReport> RunBatchAsync(Action<string, double> onProgress, CancellationToken cancellationToken)
        {
            var report = new BatchReport();
            var configuration = TranslationServiceHost.Configuration;

            if (configuration == null)
            {
                report.Errors.Add("插件配置尚未加载完成");
                return report;
            }

            configuration.Normalize();

            if (!configuration.Enabled)
            {
                this.Report(onProgress, "插件未启用，已跳过", 100);
                report.Errors.Add("插件未启用（Enabled=false）");
                return report;
            }

            ITranslationBackend backend;
            try
            {
                backend = _factory.Create(configuration);
            }
            catch (BackendConfigurationException ex)
            {
                report.Errors.Add(ex.Message);
                this.LogWarn("批量翻译未能启动: {0}", ex.Message);
                return report;
            }

            var allItems = _enumerator.Enumerate(configuration);
            var candidates = new List<Candidate>(allItems.Count);

            this.Report(onProgress, "正在检查待翻译条目…", 2);

            foreach (var item in allItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report.Examined++;

                var overview = item.Overview;
                if (string.IsNullOrWhiteSpace(overview))
                {
                    report.Skipped++;
                    continue;
                }

                if (configuration.Scope.SkipAlreadyTranslated
                    && MetadataWriter.IsAlreadyInTargetLanguage(overview, configuration.TargetLanguage))
                {
                    report.Skipped++;
                    continue;
                }

                var cached = _store.GetItemRecord(item.Id.ToString());
                var sameSource = cached != null
                    && string.Equals(cached.SourceHash, JsonTranslationStore.HashSource(overview), StringComparison.Ordinal);

                candidates.Add(new Candidate
                {
                    Item = item,
                    Overview = overview,
                    AlreadyTranslated = sameSource
                });
            }

            var maxItems = configuration.Scope.MaxItemsPerRun;
            if (maxItems > 0 && candidates.Count > maxItems)
            {
                report.StoppedEarly = true;
                candidates = candidates.Take(maxItems).ToList();
            }

            var total = candidates.Count;
            this.Report(
                onProgress,
                string.Format("共 {0} 个条目待处理，其中检查了 {1} 个", total, report.Examined),
                5);

            this.LogInfo("开始批量翻译: 检查 {0} 个条目，待处理 {1} 个，引擎 {2}",
                report.Examined, total, configuration.Backend);

            var batchSize = backend.SupportsBatch ? Math.Max(1, configuration.Llm.BatchSize) : 1;
            var delayMs = Math.Max(0, configuration.Scope.DelayBetweenItemsMs);
            var retryCount = Math.Max(0, configuration.Scope.RetryCount);
            var done = 0;

            for (var offset = 0; offset < candidates.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var slice = candidates.GetRange(offset, Math.Min(batchSize, candidates.Count - offset));
                var pending = slice.Where(c => !c.AlreadyTranslated).ToList();

                // Nothing to pay for in this slice: just re-apply what we already know.
                foreach (var already in slice.Where(c => c.AlreadyTranslated))
                {
                    var record = _store.GetItemRecord(already.Item.Id.ToString());
                    if (record != null && _writer.WriteOverview(already.Item, record.TranslatedText))
                    {
                        report.Translated++;
                        report.Saved++;
                    }
                    else
                    {
                        report.Failed++;
                    }

                    done++;
                }

                if (pending.Count > 0)
                {
                    var request = new TranslationRequest
                    {
                        SourceLanguage = configuration.SourceLanguage,
                        TargetLanguage = configuration.TargetLanguage,
                        ExtraPrompt = configuration.Llm.ExtraPrompt
                    };

                    var itemsToTranslate = pending
                        .Select(c => new TranslationItem
                        {
                            ItemId = c.Item.Id.ToString(),
                            Text = c.Overview,
                            Context = MetadataWriter.GetSeriesTitle(c.Item)
                        })
                        .ToList();

                    IList<TranslationResult> results = null;
                    for (var attempt = 0; attempt <= retryCount; attempt++)
                    {
                        try
                        {
                            results = await backend.TranslateAsync(itemsToTranslate, request, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            this.LogError("翻译请求失败（第 {0} 次）: {1}", attempt + 1, ex.Message);
                            results = null;
                        }

                        if (results != null && results.Any(r => r != null && r.Succeeded))
                        {
                            break;
                        }

                        if (attempt < retryCount)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                        }
                    }

                    for (var i = 0; i < pending.Count; i++)
                    {
                        var candidate = pending[i];
                        var result = results != null && i < results.Count ? results[i] : null;

                        if (result == null || !result.Succeeded)
                        {
                            var reason = result == null ? "后端没有返回结果" : result.Error;
                            report.Failed++;
                            if (report.Errors.Count < 20)
                            {
                                report.Errors.Add(string.Format("{0}: {1}", candidate.Item.Name, reason));
                            }

                            done++;
                            continue;
                        }

                        if (!TextTranslator.IsEffectiveTranslation(candidate.Overview, result.TranslatedText))
                        {
                            // The backend echoed the input. Never write that back: it would leave
                            // the source language in place, count as a success, and pollute both
                            // the translation memory and the item record.
                            report.Failed++;
                            if (report.Errors.Count < 20)
                            {
                                report.Errors.Add(string.Format(
                                    "{0}: 译文与原文相同，判定为未翻译（检查源语言设置或更换引擎）",
                                    candidate.Item.Name));
                            }

                            done++;
                            continue;
                        }

                        var written = _writer.WriteOverview(candidate.Item, result.TranslatedText);
                        if (written)
                        {
                            report.Translated++;
                            report.Saved++;
                            _store.RecordItem(
                                candidate.Item.Id.ToString(),
                                JsonTranslationStore.HashSource(candidate.Overview),
                                configuration.TargetLanguage,
                                result.TranslatedText);
                        }
                        else
                        {
                            report.Failed++;
                            if (report.Errors.Count < 20)
                            {
                                report.Errors.Add(string.Format("{0}: 译文写入 Emby 失败", candidate.Item.Name));
                            }
                        }

                        done++;
                    }
                }

                _store.Flush();

                if (delayMs > 0 && offset + batchSize < candidates.Count)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                var percent = total == 0 ? 100 : 5 + (95.0 * done / total);
                this.Report(
                    onProgress,
                    string.Format("已处理 {0}/{1}（成功 {2}，失败 {3}）", done, total, report.Saved, report.Failed),
                    percent);
            }

            _lastReport = report;
            _lastRunUtc = DateTime.UtcNow;

            this.LogInfo("批量翻译结束: 检查 {0}，翻译 {1}，写回 {2}，跳过 {3}，失败 {4}",
                report.Examined, report.Translated, report.Saved, report.Skipped, report.Failed);

            this.Report(onProgress, "翻译完成", 100);
            return report;
        }

        /// <summary>Reports progress, swallowing UI-side failures so a run never dies because of them.</summary>
        private void Report(Action<string, double> onProgress, string message, double percent)
        {
            if (onProgress == null)
            {
                return;
            }

            try
            {
                onProgress(message, Math.Max(0, Math.Min(100, percent)));
            }
            catch (Exception)
            {
                // Ignore: reporting is best-effort.
            }
        }

        private void LogInfo(string message, params object[] args)
        {
            if (_logger != null)
            {
                _logger.Info(message, args);
            }
        }

        private void LogWarn(string message, params object[] args)
        {
            if (_logger != null)
            {
                _logger.Warn(message, args);
            }
        }

        private void LogError(string message, params object[] args)
        {
            if (_logger != null)
            {
                _logger.Error(message, args);
            }
        }

        /// <summary>One item queued for translation in a batch run.</summary>
        private sealed class Candidate
        {
            public BaseItem Item { get; set; }

            public string Overview { get; set; }

            public bool AlreadyTranslated { get; set; }
        }
    }
}
