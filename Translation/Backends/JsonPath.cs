using System;
using System.Text;

namespace Emby.Plugin.OverviewTranslator.Translation.Backends
{
    /// <summary>
    /// A minimal, dependency-free JSON reader for the few fields the chat-completions
    /// backends need: <c>choices[0].message.content</c>.
    ///
    /// Why hand-rolled instead of System.Text.Json: the JSON string returned by an LLM is
    /// frequently not strictly valid (unescaped control characters, smart quotes, a truncated
    /// tail), and System.Text.Json refuses the whole payload in that case. This scanner walks
    /// the structure, tolerates trailing garbage, and returns null instead of throwing, so the
    /// caller can report a readable error and log the raw body.
    ///
    /// It is deliberately small: no generic object model, no number/bool coercion, only
    /// "find this property and give me its string or its child object".
    /// </summary>
    internal static class JsonPath
    {
        /// <summary>
        /// Returns the raw JSON text of the first object found under a property name,
        /// searching the whole document when the name is not a direct child. Null on miss.
        /// </summary>
        public static string FindFirstObject(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var index = 0;
            var keyStart = FindKey(json, propertyName, index);
            if (keyStart < 0)
            {
                return null;
            }

            var colon = SkipWhitespace(json, keyStart);
            if (colon < 0 || colon >= json.Length || json[colon] != ':')
            {
                return null;
            }

            var valueStart = SkipWhitespace(json, colon + 1);
            if (valueStart < 0 || valueStart >= json.Length)
            {
                return null;
            }

            if (json[valueStart] == '[')
            {
                // The property is an array: return its first object element's text.
                var elementStart = SkipWhitespace(json, valueStart + 1);
                if (elementStart >= 0 && elementStart < json.Length && json[elementStart] == '{')
                {
                    return ReadBalanced(json, elementStart);
                }

                return null;
            }

            if (json[valueStart] == '{')
            {
                return ReadBalanced(json, valueStart);
            }

            return null;
        }

        /// <summary>
        /// Returns the raw JSON text of a direct property of the given object.
        /// Null when the property is missing or is not an object/array.
        /// </summary>
        public static string FindProperty(string jsonObject, string propertyName)
        {
            if (string.IsNullOrEmpty(jsonObject) || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            var keyStart = FindKey(jsonObject, propertyName, 0);
            if (keyStart < 0)
            {
                return null;
            }

            var colon = SkipWhitespace(jsonObject, keyStart);
            if (colon < 0 || colon >= jsonObject.Length || jsonObject[colon] != ':')
            {
                return null;
            }

            var valueStart = SkipWhitespace(jsonObject, colon + 1);
            if (valueStart < 0 || valueStart >= jsonObject.Length)
            {
                return null;
            }

            var c = jsonObject[valueStart];
            if (c == '{' || c == '[')
            {
                return ReadBalanced(jsonObject, valueStart);
            }

            return null;
        }

        /// <summary>
        /// Returns the decoded string value of a direct property. Returns null when the
        /// property is missing; returns an empty string for an explicit <c>""</c> or
        /// <c>null</c> literal only when <paramref name="treatNullAsEmpty"/> is set.
        /// </summary>
        public static string FindString(string jsonObject, string propertyName)
        {
            if (string.IsNullOrEmpty(jsonObject) || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            var keyStart = FindKey(jsonObject, propertyName, 0);
            if (keyStart < 0)
            {
                return null;
            }

            var colon = SkipWhitespace(jsonObject, keyStart);
            if (colon < 0 || colon >= jsonObject.Length || jsonObject[colon] != ':')
            {
                return null;
            }

            var valueStart = SkipWhitespace(jsonObject, colon + 1);
            if (valueStart < 0 || valueStart >= jsonObject.Length)
            {
                return null;
            }

            if (jsonObject[valueStart] != '"')
            {
                // Numbers/booleans/null are not what we look for here.
                return null;
            }

            return ReadString(jsonObject, valueStart, out _);
        }

        /// <summary>
        /// Same as <see cref="FindString"/> but also returns the index just past the closing
        /// quote, so a caller can keep scanning.
        /// </summary>
        private static string ReadString(string json, int quoteIndex, out int endIndex)
        {
            endIndex = quoteIndex + 1;
            if (quoteIndex < 0 || quoteIndex >= json.Length || json[quoteIndex] != '"')
            {
                return null;
            }

            var builder = new StringBuilder();
            var i = quoteIndex + 1;

            while (i < json.Length)
            {
                var c = json[i];

                if (c == '\\')
                {
                    if (i + 1 >= json.Length)
                    {
                        break;
                    }

                    var esc = json[i + 1];
                    switch (esc)
                    {
                        case '"': builder.Append('"'); i += 2; break;
                        case '\\': builder.Append('\\'); i += 2; break;
                        case '/': builder.Append('/'); i += 2; break;
                        case 'b': builder.Append('\b'); i += 2; break;
                        case 'f': builder.Append('\f'); i += 2; break;
                        case 'n': builder.Append('\n'); i += 2; break;
                        case 'r': builder.Append('\r'); i += 2; break;
                        case 't': builder.Append('\t'); i += 2; break;
                        case 'u':
                            if (i + 6 <= json.Length)
                            {
                                int code;
                                if (int.TryParse(
                                        json.Substring(i + 2, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out code))
                                {
                                    builder.Append((char)code);
                                    i += 6;
                                }
                                else
                                {
                                    i += 2;
                                }
                            }
                            else
                            {
                                i += 2;
                            }

                            break;
                        default:
                            // Unknown escape: keep the character as-is rather than failing.
                            builder.Append(esc);
                            i += 2;
                            break;
                    }

                    continue;
                }

                if (c == '"')
                {
                    endIndex = i + 1;
                    return builder.ToString();
                }

                builder.Append(c);
                i++;
            }

            // Unterminated string: an LLM sometimes truncates its own answer. Return what
            // was read so the caller still gets a usable translation.
            endIndex = json.Length;
            return builder.ToString();
        }

        /// <summary>
        /// Returns the index of the ':' that follows the named key, searching from
        /// <paramref name="from"/>. Handles the key appearing nested or repeatedly.
        /// </summary>
        private static int FindKey(string json, string propertyName, int from)
        {
            var needle = "\"" + propertyName + "\"";
            var search = from;

            while (search >= 0 && search < json.Length)
            {
                var hit = json.IndexOf(needle, search, StringComparison.Ordinal);
                if (hit < 0)
                {
                    return -1;
                }

                // Make sure this occurrence is a key and not a string value: the character
                // after the closing quote (ignoring whitespace) must be ':'.
                var after = hit + needle.Length;
                var next = after;
                while (next < json.Length && (json[next] == ' ' || json[next] == '\t' || json[next] == '\r' || json[next] == '\n'))
                {
                    next++;
                }

                if (next < json.Length && json[next] == ':')
                {
                    return next;
                }

                search = hit + needle.Length;
            }

            return -1;
        }

        /// <summary>Skips whitespace and returns the index of the first non-space character.</summary>
        private static int SkipWhitespace(string json, int index)
        {
            var i = index;
            while (i >= 0 && i < json.Length)
            {
                var c = json[i];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                {
                    return i;
                }

                i++;
            }

            return i;
        }

        /// <summary>
        /// Returns the text of the balanced {..} or [..] value starting at
        /// <paramref name="start"/>, respecting string literals and escapes. Unterminated
        /// input returns everything to the end of the document.
        /// </summary>
        private static string ReadBalanced(string json, int start)
        {
            var open = json[start];
            var close = open == '{' ? '}' : ']';
            var depth = 0;
            var inString = false;
            var i = start;

            while (i < json.Length)
            {
                var c = json[i];

                if (inString)
                {
                    if (c == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                    }

                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    i++;
                    continue;
                }

                if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return json.Substring(start, i - start + 1);
                    }
                }

                i++;
            }

            return json.Substring(start);
        }
    }
}
