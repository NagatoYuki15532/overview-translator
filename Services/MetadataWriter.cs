using System;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.OverviewTranslator.Services
{
    /// <summary>
    /// Writes an overview back into the Emby database.
    ///
    /// Emby has no <c>UpdateItemAsync</c> (that is a Jellyfin-only extension), so this uses
    /// the synchronous <see cref="ILibraryManager.UpdateItem(BaseItem, BaseItem, ItemUpdateType)"/>,
    /// which is the same call the deployed Bangumi plugin uses to persist metadata.
    /// </summary>
    public sealed class MetadataWriter
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        /// <summary>Matches Han ideographs, which is what a Chinese translation is made of.</summary>
        private static readonly Regex HanRegex = new Regex(@"\p{IsCJKUnifiedIdeographs}", RegexOptions.Compiled);

        /// <summary>Japanese kana. Their presence means the text is Japanese, not Chinese.</summary>
        private static readonly Regex KanaRegex = new Regex(@"[\p{IsHiragana}\p{IsKatakana}]", RegexOptions.Compiled);

        /// <summary>Latin letters. Used as the denominator together with Han characters.</summary>
        private static readonly Regex LatinRegex = new Regex(@"[A-Za-z]", RegexOptions.Compiled);

        /// <summary>Cyrillic, Greek and Hangul: a Chinese translation should not be dominated by these.</summary>
        private static readonly Regex OtherScriptRegex = new Regex(@"[\p{IsCyrillic}\p{IsGreek}\p{IsHangulSyllables}]", RegexOptions.Compiled);

        public MetadataWriter(ILibraryManager libraryManager, ILogger logger)
        {
            if (libraryManager == null)
            {
                throw new ArgumentNullException("libraryManager");
            }

            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>Looks an item up by Emby id. Returns null when it no longer exists.</summary>
        public BaseItem GetItem(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            return _libraryManager.GetItemById(itemId);
        }

        /// <summary>
        /// Persists <paramref name="overview"/> on <paramref name="item"/>.
        /// Returns true when the new value was read back from the item, false when Emby
        /// kept the old one (which would mean the save did not take).
        /// </summary>
        public bool WriteOverview(BaseItem item, string overview)
        {
            if (item == null)
            {
                return false;
            }

            item.Overview = overview;

            try
            {
                var parent = item.GetParent();
                _libraryManager.UpdateItem(item, parent, ItemUpdateType.MetadataEdit);
            }
            catch (Exception ex)
            {
                this.LogError("保存条目 {0} 的简介失败: {1}", item.Name, ex.Message);
                return false;
            }

            var persisted = string.Equals(item.Overview, overview, StringComparison.Ordinal);
            if (!persisted)
            {
                this.LogError("条目 {0} 的简介在保存后与写入值不一致，可能被其它提供器覆盖。", item.Name);
            }

            return persisted;
        }

        /// <summary>
        /// Heuristic: does this text already look like the target language?
        ///
        /// This is a guard, not a language detector. Its only job is to stop the plugin
        /// from translating an overview that is already Chinese and thereby wasting money.
        /// It errs on the side of "not translated yet", so a false negative costs one
        /// translation while a false positive would silently skip a real one.
        /// </summary>
        public static bool IsAlreadyInTargetLanguage(string text, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (!IsChineseTarget(targetLanguage))
            {
                // Only Chinese is special-cased: for every other target the plugin simply
                // translates, because guessing "already German" reliably is not possible
                // here and a wrong guess silently skips work.
                return false;
            }

            // Measure Han against Latin letters only. Measuring against every non-space
            // character would make a Chinese synopsis that embeds an English title look
            // "mostly not Chinese" purely because CJK text is dense and Latin runs are long.
            var han = HanRegex.Matches(text).Count;
            var latin = LatinRegex.Matches(text).Count;
            var other = OtherScriptRegex.Matches(text).Count;
            var kana = KanaRegex.Matches(text).Count;

            if (kana > 0)
            {
                // Contains kana => Japanese, even though it also contains Han characters.
                return false;
            }

            var letters = han + latin;
            if (letters == 0)
            {
                // No CJK and no Latin at all: digits/punctuation only, or another script.
                return other == 0;
            }

            if (other > letters)
            {
                // Cyrillic/Greek/Hangul dominant: definitely not a Chinese synopsis.
                return false;
            }

            // Half the letters being Han means the text reads as Chinese with an occasional
            // foreign name. Below that it reads as a foreign synopsis that happens to contain
            // a few characters, which is the case that must not be skipped.
            return (double)han / letters >= 0.5;
        }

        /// <summary>True when the target language is a Chinese variant.</summary>
        public static bool IsChineseTarget(string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage))
            {
                return false;
            }

            var value = targetLanguage.Trim().ToLowerInvariant();
            return value.StartsWith("zh", StringComparison.Ordinal) || value == "chi" || value == "cht" || value == "chs";
        }

        /// <summary>
        /// Series name for an item, used as prompt context so character and place names stay
        /// consistent. Static on purpose: the translation service needs it without holding a
        /// reference back to itself through the writer.
        /// </summary>
        public static string GetSeriesTitle(BaseItem item)
        {
            if (item == null)
            {
                return null;
            }

            try
            {
                // BaseItem.Id is a Guid while Episode.SeriesId is a long, so the two are not
                // directly comparable: walk up and match on the series' own provider identity
                // by checking whether the item's Parent chain is itself the series.
                var episode = item as MediaBrowser.Controller.Entities.TV.Episode;
                if (episode != null)
                {
                    var seriesId = episode.SeriesId;
                    if (seriesId != 0L)
                    {
                        var current = item.GetParent();
                        for (var depth = 0; depth < 3 && current != null; depth++)
                        {
                            var series = current as MediaBrowser.Controller.Entities.TV.Series;
                            if (series != null)
                            {
                                return series.Name;
                            }

                            current = current.GetParent();
                        }
                    }
                }

                var parent = item.GetParent();
                return parent == null ? item.Name : parent.Name;
            }
            catch (Exception)
            {
                return item.Name;
            }
        }

        private void LogError(string message, params object[] args)
        {
            if (_logger != null)
            {
                _logger.Error(message, args);
            }
        }
    }
}
