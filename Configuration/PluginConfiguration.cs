using System;
using System.Collections.Generic;
using Emby.Plugin.OverviewTranslator.Translation;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugin.OverviewTranslator.Configuration
{
    /// <summary>Settings for the LLM-backed engines (DeepSeek and any OpenAI-compatible endpoint).</summary>
    public sealed class LlmSettings
    {
        /// <summary>Base URL without the trailing /chat/completions. Examples:
        /// https://api.deepseek.com/v1 , https://api.openai.com/v1 , http://localhost:11434/v1</summary>
        public string BaseUrl { get; set; }

        /// <summary>API key. May be a dummy value for local engines that ignore it.</summary>
        public string ApiKey { get; set; }

        /// <summary>Model name, e.g. deepseek-chat.</summary>
        public string Model { get; set; }

        /// <summary>Per-request HTTP timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>How many overviews to put in a single request when the engine supports it.</summary>
        public int BatchSize { get; set; }

        /// <summary>Sampling temperature. DeepSeek's own translation guide recommends 1.3.</summary>
        public double Temperature { get; set; }

        /// <summary>Maximum tokens for one response.</summary>
        public int MaxTokens { get; set; }

        /// <summary>Extra instruction appended to the built-in prompt.</summary>
        public string ExtraPrompt { get; set; }

        public LlmSettings()
        {
            this.BaseUrl = string.Empty;
            this.ApiKey = string.Empty;
            this.Model = string.Empty;
            this.TimeoutSeconds = 120;
            this.BatchSize = 10;
            this.Temperature = 1.3;
            this.MaxTokens = 8192;
            this.ExtraPrompt = string.Empty;
        }
    }

    /// <summary>Settings for the keyless engines.</summary>
    public sealed class FreeBackendSettings
    {
        /// <summary>Base URL of a LibreTranslate instance when <see cref="TranslationBackend.LibreTranslate"/> is selected.</summary>
        public string LibreTranslateUrl { get; set; }

        /// <summary>Optional API key for LibreTranslate.</summary>
        public string LibreTranslateApiKey { get; set; }

        /// <summary>Per-request HTTP timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; }

        public FreeBackendSettings()
        {
            this.LibreTranslateUrl = string.Empty;
            this.LibreTranslateApiKey = string.Empty;
            this.TimeoutSeconds = 30;
        }
    }

    /// <summary>Outbound HTTP / proxy settings.</summary>
    public sealed class NetworkSettings
    {
        /// <summary>Route translation requests through a proxy. Required for Google/Bing in some regions.</summary>
        public bool UseProxy { get; set; }

        public string ProxyHost { get; set; }

        public int ProxyPort { get; set; }

        public NetworkSettings()
        {
            // Off by default: most users do not need a proxy. The default host/port match
            // the most common local proxy setup so enabling it is a one-click change.
            this.UseProxy = false;
            this.ProxyHost = "127.0.0.1";
            this.ProxyPort = 7890;
        }
    }

    /// <summary>What to translate and how to write it back.</summary>
    public sealed class ScopeSettings
    {
        /// <summary>Cap on how many items one task run may translate. 0 means unlimited.</summary>
        public int MaxItemsPerRun { get; set; }

        /// <summary>When true, write the translation back to Emby only if the item's overview is not already in the target language.</summary>
        public bool SkipAlreadyTranslated { get; set; }

        /// <summary>Seconds to wait between items. Keeps free endpoints from rate-limiting us.</summary>
        public int DelayBetweenItemsMs { get; set; }

        /// <summary>Retry count for one item before giving up.</summary>
        public int RetryCount { get; set; }

        /// <summary>Only these library names are translated when non-empty (exact match).</summary>
        public string IncludeLibraries { get; set; }

        /// <summary>Comma-separated field to keep: reserved for future fields. Only Overview is supported today.</summary>
        public string Fields { get; set; }

        public ScopeSettings()
        {
            this.MaxItemsPerRun = 500;
            this.SkipAlreadyTranslated = true;
            this.DelayBetweenItemsMs = 200;
            this.RetryCount = 2;
            this.IncludeLibraries = string.Empty;
            this.Fields = "Overview";
        }
    }

    /// <summary>
    /// Persisted plugin configuration. Emby serialises this class to
    /// <c>programdata/configurations/Emby.Plugin.OverviewTranslator.xml</c>.
    /// Property names are part of the on-disk contract: rename with care.
    /// </summary>
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Master switch. When false, nothing translates and the task exits immediately.</summary>
        public bool Enabled { get; set; }

        /// <summary>Selected engine.</summary>
        public TranslationBackend Backend { get; set; }

        /// <summary>Source language code, or "auto".</summary>
        public string SourceLanguage { get; set; }

        /// <summary>Target language code, e.g. "zh-CN".</summary>
        public string TargetLanguage { get; set; }

        public LlmSettings Llm { get; set; }

        public FreeBackendSettings Free { get; set; }

        public NetworkSettings Network { get; set; }

        public ScopeSettings Scope { get; set; }

        /// <summary>Minutes between automatic runs of the translation task. 0 disables the trigger.</summary>
        public int IntervalMinutes { get; set; }

        /// <summary>
        /// Translate automatically when items are added to the library or refreshed.
        ///
        /// Off by default: it costs money per translated item and can surprise a user who only
        /// wanted the scheduled run. When on, events are debounced (see
        /// <see cref="AutoTranslateDelaySeconds"/>) because Emby fires them *before* metadata
        /// providers have necessarily finished writing the overview.
        /// </summary>
        public bool AutoTranslateOnNewItems { get; set; }

        /// <summary>
        /// How long to wait after the last library event before translating, in seconds.
        ///
        /// This is the fix for the ordering problem: a metadata refresh raises the event while
        /// providers are still filling in the overview, so translating immediately would read an
        /// empty or stale value. A refresh that has to reach out to Bangumi/TMDB and then write
        /// the result can take a while, so the default is deliberately generous — the only cost
        /// of waiting longer is that a new episode gets its translation a few minutes later,
        /// while the cost of waiting too little is a run that finds nothing to do.
        /// </summary>
        public int AutoTranslateDelaySeconds { get; set; }

        /// <summary>
        /// Whether the optional client-side "translate" button may appear on episode pages.
        ///
        /// Off by default, and deliberately labelled as a client-side helper: Emby 4.10 does not
        /// inject plugin scripts into the web app (measured: /web/index.html contains no plugin
        /// script reference at all, not even for the official Bangumi plugin), so the plugin
        /// cannot add a button by itself. A browser userscript does the injection and honours
        /// this flag.
        /// </summary>
        public bool EnableWebButton { get; set; }

        /// <summary>Paths of item types eligible for translation. Kept as a list so the UI can grow later.</summary>
        public List<string> ItemTypes { get; set; }

        public PluginConfiguration()
        {
            this.Enabled = true;
            this.Backend = TranslationBackend.DeepSeek;
            this.SourceLanguage = "auto";
            this.TargetLanguage = "zh-CN";
            this.Llm = new LlmSettings
            {
                BaseUrl = "https://api.deepseek.com/v1",
                Model = "deepseek-chat"
            };
            this.Free = new FreeBackendSettings();
            this.Network = new NetworkSettings();
            this.Scope = new ScopeSettings();
            this.IntervalMinutes = 0;
            this.AutoTranslateOnNewItems = false;
            // 5 minutes: long enough for a slow Bangumi/TMDB refresh to finish writing the
            // overview, short enough that a new episode picks up Chinese quickly.
            this.AutoTranslateDelaySeconds = 300;
            // Off: the button needs a browser userscript to exist at all, so an "on" default would
            // just make the settings page claim something the plugin cannot deliver by itself.
            this.EnableWebButton = false;
            this.ItemTypes = new List<string> { "Episode" };
        }

        /// <summary>Fills in anything a hand-edited or older config file left null.</summary>
        public void Normalize()
        {
            if (this.Llm == null)
            {
                this.Llm = new LlmSettings();
            }

            if (this.Free == null)
            {
                this.Free = new FreeBackendSettings();
            }

            if (this.Network == null)
            {
                this.Network = new NetworkSettings();
            }

            if (this.Scope == null)
            {
                this.Scope = new ScopeSettings();
            }

            this.ItemTypes = NormalizeItemTypes(this.ItemTypes);

            if (string.IsNullOrWhiteSpace(this.SourceLanguage))
            {
                this.SourceLanguage = "auto";
            }

            if (string.IsNullOrWhiteSpace(this.TargetLanguage))
            {
                this.TargetLanguage = "zh-CN";
            }
        }

        /// <summary>
        /// Cleans up the item-type list: trims, drops blanks and de-duplicates.
        ///
        /// Duplicates are not merely untidy — a client whose JSON serialiser nests arrays can send
        /// the same type repeatedly, and without this the repetition is what gets written to the
        /// config file on every save (observed live as "Episode,Episode,Episode"). Normalizing here
        /// rather than only at the API boundary means a hand-edited or legacy config file is
        /// repaired on load too, whatever wrote it.
        /// </summary>
        private static List<string> NormalizeItemTypes(List<string> itemTypes)
        {
            var result = new List<string>();
            if (itemTypes == null)
            {
                return new List<string> { "Episode" };
            }

            foreach (var candidate in itemTypes)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var value = candidate.Trim();

                var duplicate = false;
                foreach (var existing in result)
                {
                    if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    result.Add(value);
                }
            }

            return result.Count == 0 ? new List<string> { "Episode" } : result;
        }
    }
}
