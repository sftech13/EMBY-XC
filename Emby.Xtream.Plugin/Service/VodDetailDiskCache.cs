using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    internal sealed class VodDetailDiskCache
    {
        internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);
        internal const int MaximumEntryBytes = 2 * 1024 * 1024;
        private readonly string _root;
        private readonly long _maximumBytes;
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };

        internal VodDetailDiskCache(string root, long maximumBytes = 1024L * 1024 * 1024)
        {
            _root = root;
            _maximumBytes = maximumBytes;
        }

        private sealed class Entry
        {
            public Entry() { }
            public int Version { get; set; }
            public long StreamId { get; set; }
            public string CatalogFingerprint { get; set; }
            public long FetchedUtcTicks { get; set; }
            public VodDetailInfo Detail { get; set; }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
        }

        internal string GetPath(PluginConfiguration config, long streamId)
        {
            var scope = Hash(JsonSerializer.Serialize(new[]
            {
                (config.BaseUrl ?? string.Empty).TrimEnd('/'), config.Username, config.Password,
            }));
            return Path.Combine(_root, scope, streamId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        private static string Fingerprint(VodStreamInfo stream)
        {
            // Category ownership and output library are intentionally excluded:
            // cross-listed Movies/Documentaries refer to the same provider detail.
            return Hash(JsonSerializer.Serialize(new
            {
                stream.StreamId, stream.Name, stream.ContainerExtension, stream.StreamIcon,
                stream.Rating, stream.TmdbId,
                Added = stream.Added?.ToString("R", CultureInfo.InvariantCulture),
            }));
        }

        internal static bool IsUsable(VodDetailInfo detail)
        {
            if (detail == null) return false;
            return new[] { detail.Name, detail.TmdbId, detail.ImdbId, detail.Genre, detail.Plot, detail.Cast,
                detail.Director, detail.ReleaseDate, detail.Duration, detail.Rating, detail.ImageUrl,
                detail.BackdropUrl, detail.Trailer, detail.MediaInfo?.Video?.CodecName,
                detail.MediaInfo?.Audio?.CodecName, detail.MediaInfo?.VideoCodec, detail.MediaInfo?.AudioCodec }
                .Any(value => !string.IsNullOrWhiteSpace(value)) || detail.DurationSecs > 0 ||
                detail.MediaInfo?.Width > 0 || detail.MediaInfo?.Video?.Width > 0;
        }

        internal bool HasEntry(PluginConfiguration config, long streamId) => File.Exists(GetPath(config, streamId));

        internal bool TryRead(PluginConfiguration config, VodStreamInfo stream, DateTime utcNow, out VodDetailInfo detail)
        {
            detail = null;
            if (stream == null || stream.StreamId <= 0) return false;
            try
            {
                var path = GetPath(config, stream.StreamId);
                if (!File.Exists(path)) return false;
                using (var input = File.OpenRead(path))
                {
                    if (input.Length <= 0 || input.Length > MaximumEntryBytes) return false;
                    var entry = JsonSerializer.Deserialize<Entry>(input, Options);
                    if (entry == null || entry.Version != 1 || entry.StreamId != stream.StreamId ||
                        entry.FetchedUtcTicks <= 0 || entry.FetchedUtcTicks > utcNow.Ticks ||
                        utcNow.Ticks - entry.FetchedUtcTicks >= MaximumAge.Ticks ||
                        !string.Equals(entry.CatalogFingerprint, Fingerprint(stream), StringComparison.Ordinal) ||
                        !IsUsable(entry.Detail)) return false;
                    detail = entry.Detail;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException)
            {
                return false;
            }
        }

        internal bool TryWrite(PluginConfiguration config, VodStreamInfo stream, VodDetailInfo detail, DateTime utcNow)
        {
            string temporary = null;
            try
            {
                if (stream == null || stream.StreamId <= 0 || !IsUsable(detail)) return false;
                var path = GetPath(config, stream.StreamId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, new Entry
                    {
                        Version = 1, StreamId = stream.StreamId, CatalogFingerprint = Fingerprint(stream),
                        FetchedUtcTicks = utcNow.Ticks, Detail = detail,
                    }, Options);
                    if (output.Length > MaximumEntryBytes || output.Length > _maximumBytes) return false;
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException)
            {
                return false;
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

        internal void Invalidate(PluginConfiguration config, long streamId)
        {
            try { File.Delete(GetPath(config, streamId)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal int Trim(DateTime utcNow, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_root)) return 0;
            var removed = 0;
            try
            {
                var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToArray();
                var total = files.Sum(file => file.Length);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expired = file.LastWriteTimeUtc < utcNow.AddDays(-7);
                    var abandoned = file.Extension == ".tmp" && file.LastWriteTimeUtc < utcNow.AddDays(-1);
                    if (!expired && !abandoned && total <= _maximumBytes) continue;
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                    removed++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return removed;
        }
    }
}
