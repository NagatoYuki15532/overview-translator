using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Emby.Plugin.OverviewTranslator.Services;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.OverviewTranslator.Translation
{
    /// <summary>
    /// File-backed translation memory plus per-item audit records.
    ///
    /// Why this exists: a library run translates hundreds of overviews and costs real
    /// money on the LLM backends. A re-run (or a re-scan that re-requests metadata)
    /// must not pay twice for the same sentence, and the user must be able to see what
    /// was written where without reading Emby's database.
    ///
    /// Two files in the plugin data directory:
    ///   cache.json  - keyed by content hash + engine + target language
    ///   items.json  - keyed by Emby item id, for auditing
    /// Both are plain JSON so the user can inspect or delete them.
    /// </summary>
    public sealed class JsonTranslationStore : ITranslationStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly object _gate = new object();
        private readonly string _cachePath;
        private readonly string _itemsPath;
        private readonly ILogger _logger;

        private Dictionary<string, CacheEntry> _cache;
        private Dictionary<string, ItemTranslationRecord> _items;
        private bool _cacheDirty;
        private bool _itemsDirty;

        public JsonTranslationStore(string dataFolderPath, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(dataFolderPath))
            {
                throw new ArgumentNullException("dataFolderPath");
            }

            _logger = logger;

            try
            {
                Directory.CreateDirectory(dataFolderPath);
            }
            catch (Exception ex)
            {
                // A read-only data folder must not break the whole plugin; the store
                // degrades to in-memory only.
                this.LogWarn("无法创建插件数据目录 {0}: {1}（缓存将只在内存中生效）", dataFolderPath, ex.Message);
            }

            _cachePath = Path.Combine(dataFolderPath, "cache.json");
            _itemsPath = Path.Combine(dataFolderPath, "items.json");

            _cache = this.Load<Dictionary<string, CacheEntry>>(_cachePath) ?? new Dictionary<string, CacheEntry>();
            _items = this.Load<Dictionary<string, ItemTranslationRecord>>(_itemsPath) ?? new Dictionary<string, ItemTranslationRecord>();
        }

        /// <summary>Number of cached translations. Used for the status endpoint.</summary>
        public int CacheCount
        {
            get
            {
                lock (_gate)
                {
                    return _cache.Count;
                }
            }
        }

        public string GetCached(string sourceText, TranslationBackend backend, string targetLanguage)
        {
            if (string.IsNullOrEmpty(sourceText))
            {
                return null;
            }

            var key = this.BuildKey(sourceText, backend, targetLanguage);
            lock (_gate)
            {
                CacheEntry entry;
                if (_cache.TryGetValue(key, out entry) && entry != null && !string.IsNullOrWhiteSpace(entry.Translated))
                {
                    return entry.Translated;
                }
            }

            return null;
        }

        public void PutCached(string sourceText, string translatedText, TranslationBackend backend, string targetLanguage)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrWhiteSpace(translatedText))
            {
                return;
            }

            var key = this.BuildKey(sourceText, backend, targetLanguage);
            lock (_gate)
            {
                _cache[key] = new CacheEntry
                {
                    Source = sourceText,
                    Translated = translatedText,
                    Backend = backend.ToString(),
                    Target = targetLanguage,
                    UtcTicks = DateTime.UtcNow.Ticks
                };

                _cacheDirty = true;
            }
        }

        public void RecordItem(string itemId, string sourceHash, string targetLanguage, string translatedText)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return;
            }

            lock (_gate)
            {
                _items[itemId] = new ItemTranslationRecord
                {
                    ItemId = itemId,
                    SourceHash = sourceHash,
                    TargetLanguage = targetLanguage,
                    TranslatedText = translatedText,
                    UtcTicks = DateTime.UtcNow.Ticks
                };

                _itemsDirty = true;
            }
        }

        public ItemTranslationRecord GetItemRecord(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            lock (_gate)
            {
                ItemTranslationRecord record;
                return _items.TryGetValue(itemId, out record) ? record : null;
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                if (_cacheDirty)
                {
                    this.Save(_cachePath, _cache);
                    _cacheDirty = false;
                }

                if (_itemsDirty)
                {
                    this.Save(_itemsPath, _items);
                    _itemsDirty = false;
                }
            }
        }

        /// <summary>Stable content hash so the cache survives renames and re-scans.</summary>
        public static string HashSource(string text)
        {
            if (text == null)
            {
                text = string.Empty;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private string BuildKey(string sourceText, TranslationBackend backend, string targetLanguage)
        {
            var target = string.IsNullOrWhiteSpace(targetLanguage) ? "zh-CN" : targetLanguage.Trim();
            return backend + "|" + target + "|" + HashSource(sourceText);
        }

        private T Load<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<T>(json, SerializerOptions);
            }
            catch (Exception ex)
            {
                // Corrupt cache must never stop the plugin: fall back to empty and warn.
                this.LogWarn("读取 {0} 失败，将从空缓存开始: {1}", Path.GetFileName(path), ex.Message);
                return null;
            }
        }

        private void Save<T>(string path, T value)
        {
            try
            {
                var temp = path + ".tmp";
                var json = JsonSerializer.Serialize(value, SerializerOptions);
                File.WriteAllText(temp, json, new UTF8Encoding(false));

                // Replace atomically-ish so a crash never leaves a half-written cache.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                this.LogWarn("写入 {0} 失败: {1}", Path.GetFileName(path), ex.Message);
            }
        }

        private void LogWarn(string message, params object[] args)
        {
            if (_logger != null)
            {
                _logger.Warn(message, args);
            }
        }

        internal sealed class CacheEntry
        {
            public string Source { get; set; }

            public string Translated { get; set; }

            public string Backend { get; set; }

            public string Target { get; set; }

            public long UtcTicks { get; set; }
        }
    }
}
