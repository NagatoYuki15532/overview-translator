using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Plugin.OverviewTranslator.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Emby.Plugin.OverviewTranslator.Services
{
    /// <summary>
    /// Finds the Emby items a translation run should consider.
    ///
    /// Scope for v1 is episodes only. Series-level overviews come from the metadata
    /// provider and are usually already localised, while episode overviews are the ones
    /// that arrive in the source language — that is the gap this plugin fills.
    /// </summary>
    public sealed class EligibleItemEnumerator
    {
        private readonly ILibraryManager _libraryManager;

        public EligibleItemEnumerator(ILibraryManager libraryManager)
        {
            if (libraryManager == null)
            {
                throw new ArgumentNullException("libraryManager");
            }

            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Returns every candidate item, newest first so a capped run translates the most
        /// recently added episodes before the back catalogue.
        /// </summary>
        public IReadOnlyList<BaseItem> Enumerate(PluginConfiguration configuration)
        {
            configuration.Normalize();

            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false
            };

            IEnumerable<BaseItem> items;
            try
            {
                items = _libraryManager.GetItemList(query);
            }
            catch (Exception)
            {
                // A query without a SetVirtualFolder can fail on some layouts; retry scoped
                // to video items, which is what we actually want.
                query.IncludeItemTypes = new[] { "Episode" };
                items = _libraryManager.GetItemList(query);
            }

            var allowedTypes = new HashSet<string>(
                (configuration.ItemTypes ?? new List<string> { "Episode" }),
                StringComparer.OrdinalIgnoreCase);

            var libraries = ParseLibraries(configuration.Scope == null ? null : configuration.Scope.IncludeLibraries);

            var list = new List<BaseItem>();
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                // The real runtime type check, rather than the item-type string: Emby
                // reports a type name that does not always round-trip.
                var typeName = item.GetType().Name;
                if (!allowedTypes.Contains(typeName))
                {
                    continue;
                }

                if (libraries.Count > 0)
                {
                    var path = item.Path ?? string.Empty;
                    var inScope = libraries.Any(name => path.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!inScope)
                    {
                        continue;
                    }
                }

                list.Add(item);
            }

            return list
                .OrderByDescending(i => i.DateCreated)
                .ToList();
        }

        /// <summary>Splits the comma-separated library filter, ignoring blanks.</summary>
        private static List<string> ParseLibraries(string includeLibraries)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(includeLibraries))
            {
                return result;
            }

            foreach (var part in includeLibraries.Split(',', ';'))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result;
        }
    }
}
