using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Emby.Xtream.Plugin.Service
{
    internal static class SyncReporting
    {
        internal static double GetTaskPercent(SyncProgress progress)
        {
            if (progress.IsPreparing)
                return 10 + 50 * Fraction(Volatile.Read(ref progress.Prepared), Volatile.Read(ref progress.PreparationTotal));
            var fraction = Fraction(Volatile.Read(ref progress.Completed), Volatile.Read(ref progress.Total));
            if (progress.PreparationTotal > 0) return 60 + 40 * fraction;
            if (progress.Phase == "Classifying VOD genres") return 10 * fraction;
            return 100 * fraction;
        }

        private static double Fraction(int completed, int total)
        {
            return total <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)completed / total));
        }

        internal static string DescribeFailure(Exception exception, string phase, PluginConfiguration config)
        {
            var messages = new List<string>();
            for (var current = exception; current != null && messages.Count < 4; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                    messages.Add(current.Message);
            }
            return SanitizeFailureReason((string.IsNullOrWhiteSpace(phase) ? string.Empty : phase + ": ") +
                string.Join(" Cause: ", messages), config);
        }

        internal static string GetFailureReason(SyncProgress progress, PluginConfiguration config)
        {
            var reason = !string.IsNullOrWhiteSpace(progress.FailureReason) ? progress.FailureReason : progress.AbortReason;
            if (string.IsNullOrWhiteSpace(reason)) reason = "Sync did not complete. Check the server log for details.";
            return SanitizeFailureReason(reason, config);
        }

        internal static string SanitizeFailureReason(string reason, PluginConfiguration config)
        {
            var text = reason ?? string.Empty;
            text = Regex.Replace(text, @"https?://[^\s<>""']+", "[URL redacted]", RegexOptions.IgnoreCase);
            foreach (var secret in new[] { config?.Username, config?.Password })
            {
                if (!string.IsNullOrEmpty(secret))
                    text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(secret) + @"(?![\p{L}\p{N}])", "[redacted]");
            }
            text = Regex.Replace(text, @"\b(username|password|X-Emby-Token|api_key|access_token|token)\s*[=:]\s*[^&\s;]+",
                "$1=[redacted]", RegexOptions.IgnoreCase);
            Uri provider;
            if (Uri.TryCreate(config?.BaseUrl, UriKind.Absolute, out provider) && !string.IsNullOrWhiteSpace(provider.Host))
                text = Regex.Replace(text, Regex.Escape(provider.Host), "[provider]", RegexOptions.IgnoreCase);
            text = LogSanitizer.SanitizeLine(text, string.Empty, string.Empty);
            return text.Length <= 3000 ? text : text.Substring(0, 3000) + "...";
        }
    }
}
