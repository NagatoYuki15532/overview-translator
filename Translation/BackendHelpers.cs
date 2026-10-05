using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Emby.Plugin.OverviewTranslator.Translation
{
    /// <summary>
    /// Builds and validates the <c>Authorization: Bearer</c> value for a backend.
    ///
    /// This exists because of a real bug found in this environment: a plugin sent
    /// the literal header "Bearer " with no token, and the remote API answered
    /// 401 "invalid http Authorization header, missing scope or missing token".
    /// So: a missing token must never be turned into a malformed header.
    /// </summary>
    public static class AuthHeader
    {
        /// <summary>
        /// Returns a usable header value, or null when <paramref name="token"/> carries no token.
        /// Callers must treat null as "do not send the Authorization header at all".
        /// </summary>
        public static string BuildBearer(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var trimmed = token.Trim();

            // Callers may paste the whole header; do not double-prefix it.
            if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("Bearer ".Length).Trim();
            }
            else if (string.Equals(trimmed, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                // Exactly the scheme with no token. Note that Trim() above already removed the
                // trailing space, so the StartsWith branch above cannot catch "Bearer ".
                // Without this branch we would emit the malformed header "Bearer Bearer",
                // which every strict API rejects with 401 — the exact failure this helper exists
                // to prevent.
                return null;
            }

            if (trimmed.Length == 0)
            {
                return null;
            }

            return "Bearer " + trimmed;
        }
    }

    /// <summary>Small helpers shared by backends so failures are reported consistently.</summary>
    public static class BackendHelpers
    {
        /// <summary>Cuts a possibly huge body down to something usable in a one-line log message.</summary>
        public static string Shorten(string value, int max = 300)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var collapsed = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (collapsed.Contains("  "))
            {
                collapsed = collapsed.Replace("  ", " ");
            }

            return collapsed.Length <= max ? collapsed : collapsed.Substring(0, max) + "...";
        }

        /// <summary>Builds a "backend failed" result for one item.</summary>
        public static TranslationResult Failure(TranslationItem item, string error)
        {
            return new TranslationResult
            {
                ItemId = item == null ? null : item.ItemId,
                Error = error
            };
        }

        /// <summary>Normalises a language code to the two-letter form most providers expect.</summary>
        public static string NormalizeLanguage(string language, string fallback)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return fallback;
            }

            var value = language.Trim();
            var dash = value.IndexOf('-');
            if (dash > 0)
            {
                value = value.Substring(0, dash);
            }

            return value.ToLowerInvariant();
        }
    }
}
