using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator.Translation;

namespace Emby.Plugin.OverviewTranslator.Services
{
    /// <summary>What one translate-and-save operation did.</summary>
    public sealed class ItemTranslationOutcome
    {
        public string ItemId { get; set; }

        /// <summary>True when a translation was produced (whether or not the value changed).</summary>
        public bool Translated { get; set; }

        /// <summary>True when something actually changed on the item.</summary>
        public bool Saved { get; set; }

        /// <summary>True when the item was skipped on purpose (empty overview, already translated, already cached).</summary>
        public bool Skipped { get; set; }

        public string Reason { get; set; }

        public string OriginalText { get; set; }

        public string TranslatedText { get; set; }
    }

    /// <summary>Progress counters for a batch run.</summary>
    public sealed class BatchReport
    {
        public int Examined { get; set; }

        public int Translated { get; set; }

        public int Saved { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        /// <summary>True when the item cap stopped the run early.</summary>
        public bool StoppedEarly { get; set; }

        public List<string> Errors { get; set; }

        public BatchReport()
        {
            this.Errors = new List<string>();
        }
    }

    /// <summary>
    /// High-level translation service: resolves a backend from configuration, caches results,
    /// writes translations back to Emby. This is the only type the task and the API talk to.
    /// </summary>
    public interface ITranslationService
    {
        /// <summary>The backend selected in configuration, already validated.</summary>
        TranslationBackend ActiveBackend { get; }

        /// <summary>
        /// Translates a free string with the configured backend, using the cache.
        /// Throws <see cref="InvalidOperationException"/> when configuration is unusable.
        /// </summary>
        Task<string> TranslateTextAsync(string text, string context, CancellationToken cancellationToken);

        /// <summary>
        /// Same as <see cref="TranslateTextAsync"/> but against an explicitly supplied
        /// configuration. Never throws for backend problems: returns a result whose Error
        /// explains what went wrong. This is the entry point the REST API uses, because an API
        /// call must report failures rather than surface an exception.
        /// </summary>
        Task<TranslationResult> TranslateTextWithResultAsync(
            string text,
            string context,
            Configuration.PluginConfiguration configuration,
            CancellationToken cancellationToken);

        /// <summary>
        /// Translates one Emby item's overview and saves it back to the Emby database.
        /// <paramref name="force"/> bypasses the "already translated" guard and the cache.
        /// Never throws for per-item problems; reports them in the outcome.
        /// </summary>
        Task<ItemTranslationOutcome> TranslateAndSaveItemAsync(string itemId, bool force, CancellationToken cancellationToken);

        /// <summary>
        /// Walks every eligible episode and translates the ones that need it.
        /// Reports progress through <paramref name="onProgress"/> (message, percent) when supplied.
        /// </summary>
        Task<BatchReport> TranslateLibraryAsync(Action<string, double> onProgress, CancellationToken cancellationToken);

        /// <summary>Number of items currently eligible for translation. Used by the task description and the UI.</summary>
        Task<int> CountPendingAsync(CancellationToken cancellationToken);

        /// <summary>Verifies the configured backend end to end. Returns null when it works.</summary>
        Task<string> TestBackendAsync(CancellationToken cancellationToken);
    }
}
