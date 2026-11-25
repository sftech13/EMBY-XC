using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Emby.Xtream.Plugin.Service
{
    // Deletion requires both a managed path and a matching provider playback URL.
    // Do not infer ownership from a title, filename extension or host prefix alone.
    internal static class OwnedContentFiles
    {
        internal static bool IsSafePath(string path, string root)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!full.StartsWith(boundary, comparison)) return false;
                for (var current = full; current != null; current = Path.GetDirectoryName(current))
                {
                    if ((File.Exists(current) || Directory.Exists(current)) &&
                        (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        internal static bool IsOwnedStrm(string path, string root, PluginConfiguration config)
        {
            if (config == null || !IsSafePath(path, root) || !string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return false;
                var content = File.ReadAllText(path).Trim();
                if (content.IndexOfAny(new[] { '\r', '\n' }) >= 0) return false;
                Uri source, provider;
                if (!Uri.TryCreate(content, UriKind.Absolute, out source) ||
                    !Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out provider) ||
                    (source.Scheme != "http" && source.Scheme != "https") || source.UserInfo.Length != 0 ||
                    source.Scheme != provider.Scheme || !string.Equals(source.Host, provider.Host, StringComparison.OrdinalIgnoreCase) || source.Port != provider.Port) return false;
                var prefix = provider.AbsolutePath.TrimEnd('/') + "/";
                if (!source.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
                var parts = source.AbsolutePath.Substring(prefix.Length).TrimStart('/').Split('/');
                if (parts.Length != 4 || (parts[0] != "movie" && parts[0] != "series") ||
                    Uri.UnescapeDataString(parts[1]) != (config.Username ?? string.Empty) ||
                    Uri.UnescapeDataString(parts[2]) != (config.Password ?? string.Empty)) return false;
                var dot = parts[3].LastIndexOf('.');
                long id;
                return dot > 0 && dot < parts[3].Length - 1 && long.TryParse(parts[3].Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
            }
            catch { return false; }
        }

        internal static bool DeleteStrm(string path, string root, PluginConfiguration config)
        {
            if (!IsOwnedStrm(path, root, config)) return false;
            File.Delete(path);
            var sidecar = Path.ChangeExtension(path, ".nfo");
            if (IsSafePath(sidecar, root) && File.Exists(sidecar)) File.Delete(sidecar);
            return true;
        }

        internal static List<string> EnumerateFiles(string folder, string root)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var path in Directory.GetFileSystemEntries(current))
                {
                    if (!IsSafePath(path, root)) continue;
                    if (Directory.Exists(path)) pending.Push(path);
                    else result.Add(path);
                }
            }
            return result;
        }

        internal static int DeleteOwnedTree(string folder, string root, PluginConfiguration config)
        {
            if (!IsSafePath(Path.Combine(folder, ".ownership-check"), root) || !Directory.Exists(folder)) return 0;
            var files = EnumerateFiles(folder, root);
            var owned = files.Where(path => IsOwnedStrm(path, root, config)).ToList();
            var count = 0;
            foreach (var path in owned) if (DeleteStrm(path, root, config)) count++;
            // A show sidecar is associated only with a tree that contained owned
            // episodes and now has no streams/local media left beneath it.
            if (owned.Count > 0)
            foreach (var nfo in files.Where(path => string.Equals(Path.GetFileName(path), "tvshow.nfo", StringComparison.OrdinalIgnoreCase)))
            {
                var remaining = EnumerateFiles(Path.GetDirectoryName(nfo), root);
                if (remaining.All(path => string.Equals(Path.GetExtension(path), ".nfo", StringComparison.OrdinalIgnoreCase))) File.Delete(nfo);
            }
            PruneEmpty(folder, root);
            return count;
        }

        private static void PruneEmpty(string folder, string root)
        {
            foreach (var child in Directory.GetDirectories(folder))
                if (IsSafePath(child, root)) PruneEmpty(child, root);
            if (IsSafePath(folder, root) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
        }
    }
}
