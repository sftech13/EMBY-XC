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
    // This is a disposable metadata cache, never an authority for catalog absence
    // or episode deletion. Entries are read one at a time, not retained in memory.
    internal sealed class SeriesDetailDiskCache
    {
        internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
        internal const int MaximumEntryBytes = 16 * 1024 * 1024;
        private readonly string _root;
        private readonly long _maximumBytes;
        private static readonly JsonSerializerOptions CacheJsonOptions = new JsonSerializerOptions
        {
            // get_series_info.info often omits series_id. Do not invent an
            // explicit zero that the strict provider-ID converter would reject.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };

        internal SeriesDetailDiskCache(string root, long maximumBytes = 2L * 1024 * 1024 * 1024)
        {
            _root = root;
            _maximumBytes = maximumBytes;
        }

        private sealed class Entry
        {
            public Entry() { }
            public int Version { get; set; }
            public long SeriesId { get; set; }
            public string CatalogFingerprint { get; set; }
            public long FetchedUtcTicks { get; set; }
            public SeriesDetailInfo Detail { get; set; }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
        }

        internal string GetPath(PluginConfiguration config, long seriesId)
        {
            // Credential/endpoint changes cannot reuse another account's details.
            // Only the digest, not the credentials, is written to the filesystem.
            var scope = Hash(JsonSerializer.Serialize(new[]
            {
                (config.BaseUrl ?? string.Empty).TrimEnd('/'), config.Username, config.Password,
            }));
            return Path.Combine(_root, scope, seriesId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        private static bool HasReliableTimestamp(SeriesInfo series)
        {
            long timestamp;
            return series != null && series.SeriesId > 0 &&
                long.TryParse(series.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp) && timestamp > 0;
        }

        private static bool IsUsable(SeriesDetailInfo detail)
        {
            XtreamResponseParser.ValidateEpisodeIds(detail);
            return detail?.Episodes != null && detail.Episodes.Values.Any(season => season.Count > 0);
        }

        internal bool TryRead(PluginConfiguration config, SeriesInfo series, DateTime utcNow, out SeriesDetailInfo detail)
        {
            detail = null;
            if (!HasReliableTimestamp(series)) return false;
            try
            {
                using (var stream = File.OpenRead(GetPath(config, series.SeriesId)))
                {
                    if (stream.Length <= 0 || stream.Length > MaximumEntryBytes) return false;
                    var entry = JsonSerializer.Deserialize<Entry>(stream, CacheJsonOptions);
                    if (entry == null || entry.Version != 1 || entry.SeriesId != series.SeriesId ||
                        !string.Equals(entry.CatalogFingerprint, Hash(JsonSerializer.Serialize(series)), StringComparison.Ordinal) ||
                        entry.FetchedUtcTicks <= 0 || entry.FetchedUtcTicks > utcNow.Ticks ||
                        utcNow.Ticks - entry.FetchedUtcTicks >= MaximumAge.Ticks || !IsUsable(entry.Detail))
                        return false;
                    detail = entry.Detail;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException)
            {
                return false; // Corrupt/unavailable cache always falls back to fresh detail.
            }
        }

        internal bool TryWrite(PluginConfiguration config, SeriesInfo series, SeriesDetailInfo detail, DateTime utcNow)
        {
            string temporary = null;
            try
            {
                if (!HasReliableTimestamp(series) || !IsUsable(detail)) return false;
                var path = GetPath(config, series.SeriesId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new Entry
                    {
                        Version = 1, SeriesId = series.SeriesId,
                        CatalogFingerprint = Hash(JsonSerializer.Serialize(series)),
                        FetchedUtcTicks = utcNow.Ticks, Detail = detail,
                    }, CacheJsonOptions);
                    if (stream.Length > MaximumEntryBytes || stream.Length > _maximumBytes) return false;
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException)
            {
                return false; // Cache I/O must never fail an otherwise valid sync.
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

        internal void Invalidate(PluginConfiguration config, long seriesId)
        {
            try { File.Delete(GetPath(config, seriesId)); }
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
                    var expired = file.LastWriteTimeUtc < utcNow.AddDays(-14);
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
