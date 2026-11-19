using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Last generated metadata, not the last contents written by Emby. One small
    /// journal per movie/series is loaded on demand and saved once per session.
    /// Kept outside watched libraries; never expires along with provider caches.
    /// </summary>
    internal sealed class NfoOwnershipState : IDisposable
    {
        internal const int MaximumJournalBytes = 32 * 1024 * 1024;
        private readonly string _folder;
        private readonly string _path;
        private readonly Action<string> _warn;
        private Dictionary<string, string> _generated = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> _episodeFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _dirty;

        private sealed class Journal
        {
            public Journal() { }
            public int Version { get; set; }
            public Dictionary<string, string> Generated { get; set; }
            public Dictionary<string, string> EpisodeFingerprints { get; set; }
        }

        internal NfoOwnershipState(string root, string folder, string providerScope, Action<string> warn = null)
        {
            _folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            _warn = warn;
            if (string.IsNullOrWhiteSpace(root)) return;
            _path = Path.Combine(root, Hash(providerScope ?? string.Empty), Hash(_folder) + ".json");
            try
            {
                if (!File.Exists(_path)) return;
                using (var input = File.OpenRead(_path))
                {
                    if (input.Length > MaximumJournalBytes) throw new InvalidDataException("NFO journal exceeds size limit");
                    var journal = JsonSerializer.Deserialize<Journal>(input);
                    if (journal?.Version != 1 || journal.Generated == null)
                        throw new InvalidDataException("NFO journal has an unsupported schema");
                    _generated = new Dictionary<string, string>(journal.Generated, StringComparer.Ordinal);
                    if (journal.EpisodeFingerprints != null)
                        _episodeFingerprints = new Dictionary<string, string>(journal.EpisodeFingerprints, StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                Warn("NFO ownership journal unavailable; existing NFOs will be adopted without replacement");
            }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private string Key(string nfoPath)
        {
            var full = Path.GetFullPath(nfoPath);
            if (!full.StartsWith(_folder, StringComparison.Ordinal))
                throw new ArgumentException("NFO does not belong to this ownership session", nameof(nfoPath));
            return full.Substring(_folder.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        internal string GetPrevious(string nfoPath)
        {
            string previous;
            return _generated.TryGetValue(Key(nfoPath), out previous) ? previous : null;
        }

        internal bool MatchesEpisode(string nfoPath, string fingerprint)
        {
            var key = Key(nfoPath);
            string previous;
            return _generated.ContainsKey(key) && _episodeFingerprints.TryGetValue(key, out previous) && previous == fingerprint;
        }

        internal void Record(string nfoPath, string generated, string episodeFingerprint = null)
        {
            var key = Key(nfoPath);
            string old;
            if (episodeFingerprint != null &&
                (!_episodeFingerprints.TryGetValue(key, out old) || old != episodeFingerprint))
            {
                _episodeFingerprints[key] = episodeFingerprint;
                _dirty = true;
            }
            if (_generated.TryGetValue(key, out old) && string.Equals(old, generated, StringComparison.Ordinal)) return;
            _generated[key] = generated;
            _dirty = true;
        }

        internal void Warn(string message) => _warn?.Invoke(message);

        private static bool IsStorageError(Exception ex)
            => ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException;

        public void Dispose()
        {
            if (!_dirty || _path == null) return;
            string temporary = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, new Journal { Version = 1, Generated = _generated, EpisodeFingerprints = _episodeFingerprints });
                    if (output.Length > MaximumJournalBytes) throw new InvalidDataException("NFO journal exceeds size limit");
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                _dirty = false;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                Warn("Could not persist NFO ownership journal; library files and existing metadata remain protected");
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
