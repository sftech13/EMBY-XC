using System;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class ContentNameCleaner
    {
        private static readonly char[] LineSeparators = new[] { '\n', '\r' };

        // Matches one or more ┃XX┃ / │XX│ / |XX| prefix tags at the start of a string.
        // Handles: ┃UK┃, ┃UK ┃, ┃ UK┃, │EN│, |FR|, with optional whitespace around them.
        // U+2503 = ┃ (heavy vertical), U+2502 = │ (light vertical), | = pipe
        private static readonly Regex BoxPrefixRegex = new Regex(
            @"^(\s*[\u2503\u2502|][^\u2503\u2502|]+[\u2503\u2502|]\s*)+",
            RegexOptions.Compiled);

        // Matches ┃XX┃ / │XX│ / |XX| tags anywhere in the string (not just prefix).
        private static readonly Regex BoxTagAnywhereRegex = new Regex(
            @"\s*[\u2503\u2502|][^\u2503\u2502|]+[\u2503\u2502|]\s*",
            RegexOptions.Compiled);

        // Matches exactly 2-letter uppercase country-code prefix labels like "EN - ", "US - ", "FR - ".
        // Deliberately limited to 2-letter alpha only to avoid false-positives on show acronyms
        // (FBI, CSI, NCIS, etc.) that providers may format as "FBI - Most Wanted".
        // Applied once per call (single prefix strip).
        private static readonly Regex DashPrefixRegex = new Regex(
            @"^[A-Z]{2}\s+-\s+",
            RegexOptions.Compiled);

        // Strips unambiguous quality/resolution prefix tags at the start of a title,
        // with or without a following dash separator.
        // e.g. "4K 28 Years Later" → "28 Years Later"
        //      "FHD - Inception"   → "Inception"
        //      "UHD Movie Name"    → "Movie Name"
        private static readonly Regex QualityPrefixRegex = new Regex(
            @"^(4K|FHD|UHD|HDR|SDR|HQ)\s*[-–]?\s+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MultipleSpacesRegex = new Regex(
            @"\s{2,}",
            RegexOptions.Compiled);

        /// <summary>
        /// Cleans content names (movie/series titles) by removing country-code prefix
        /// tags like ┃UK┃, │EN│, |FR| and user-specified additional terms.
        /// </summary>
        public static string CleanContentName(string name, string userRemoveTerms = null, bool enableCleaning = true)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            if (!enableCleaning) return name.Trim();

            string result = name;

            // Remove ┃XX┃ style prefix tags (country codes, labels)
            result = BoxPrefixRegex.Replace(result, string.Empty);

            // Also remove any remaining ┃XX┃ tags in the middle/end of the string
            result = BoxTagAnywhereRegex.Replace(result, " ");

            // Remove plain dash-style prefix labels like "EN - ", "US - "
            result = DashPrefixRegex.Replace(result, string.Empty);

            // Remove leading quality/resolution tags like "4K ", "FHD - ", "UHD "
            result = QualityPrefixRegex.Replace(result, string.Empty);

            // Remove user-specified terms (one per line) as whole-word matches so that
            // "4K" strips "4K 28 Years Later" but not "4Kresolution".
            if (!string.IsNullOrWhiteSpace(userRemoveTerms))
            {
                var lines = userRemoveTerms.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var term = line.Trim();
                    if (string.IsNullOrEmpty(term)) continue;

                    // Build a pattern that matches the term only when surrounded by
                    // non-alphanumeric characters (or string start/end).
                    var pattern = @"(?<![A-Za-z0-9])" + Regex.Escape(term) + @"(?![A-Za-z0-9])";
                    result = Regex.Replace(result, pattern, " ", RegexOptions.IgnoreCase);
                }
            }

            result = MultipleSpacesRegex.Replace(result, " ");
            result = result.Trim();

            return string.IsNullOrWhiteSpace(result) ? name.Trim() : result;
        }
    }
}
