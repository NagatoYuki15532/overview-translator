using System;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Translation;

namespace Emby.Plugin.OverviewTranslator.Services
{
    /// <summary>Creates the translation engine described by configuration.</summary>
    public interface ITranslationBackendFactory
    {
        /// <summary>The engine selected in the supplied configuration.</summary>
        TranslationBackend ActiveBackend { get; }

        /// <summary>
        /// Validates configuration and returns the engine. Throws
        /// <see cref="BackendConfigurationException"/> when the selected engine is unusable
        /// (for example an LLM backend with no API key configured).
        /// </summary>
        ITranslationBackend Create(PluginConfiguration configuration);
    }

    /// <summary>Configuration cannot produce a working backend. The message is user-facing.</summary>
    public class BackendConfigurationException : Exception
    {
        public BackendConfigurationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>The cache and the stats store, kept next to the plugin's configuration.</summary>
    public interface ITranslationStore
    {
        /// <summary>Looks up a cached translation. Returns null on miss.</summary>
        string GetCached(string sourceText, TranslationBackend backend, string targetLanguage);

        /// <summary>Stores a translation. Implementations must be safe to call from several threads.</summary>
        void PutCached(string sourceText, string translatedText, TranslationBackend backend, string targetLanguage);

        /// <summary>Remembers the translation written to an Emby item so it can be re-applied or audited.</summary>
        void RecordItem(string itemId, string sourceHash, string targetLanguage, string translatedText);

        /// <summary>Returns the stored record for an item, or null.</summary>
        ItemTranslationRecord GetItemRecord(string itemId);

        /// <summary>Persists any pending changes. Called at the end of a batch run.</summary>
        void Flush();
    }

    /// <summary>One item's translation history entry.</summary>
    public sealed class ItemTranslationRecord
    {
        public string ItemId { get; set; }

        public string SourceHash { get; set; }

        public string TargetLanguage { get; set; }

        public string TranslatedText { get; set; }

        public long UtcTicks { get; set; }
    }
}
