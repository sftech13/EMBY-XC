using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static readonly DateTime OldNfoTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        private static EpisodeInfo OwnershipEpisode(string plot = "Provider plot") => new EpisodeInfo
        {
            Id = 123, Title = "Pilot", Season = 1, EpisodeNum = 1,
            Info = new EpisodeMediaInfo
            {
                Plot = plot, ReleaseDate = "2026-01-02", DurationSecs = 1800,
                VideoCodec = "h264", AudioCodec = "aac", Width = 1920, Height = 1080,
            },
        };

        private static void EnrichNfo(string path, bool changePlot = false)
        {
            var doc = XElement.Load(path);
            doc.Add(new XElement("uniqueid", new XAttribute("type", "tvdb"), new XAttribute("default", "true"), "98765"));
            doc.Add(new XElement("thumb", new XAttribute("aspect", "poster"), "https://art.invalid/poster.jpg"));
            doc.Add(new XElement("dateadded", "2026-11-19 01:23:45"));
            doc.Add(new XElement("custom", new XAttribute("flag", "yes"), new XElement("data", "keep")));
            if (changePlot) doc.SetElementValue("plot", "Emby custom plot");
            var streams = doc.Element("fileinfo")?.Element("streamdetails");
            if (streams != null)
            {
                streams.Element("video")?.SetElementValue("width", "3840");
                streams.Add(new XElement("subtitle", new XElement("codec", "srt"), new XElement("language", "eng")));
            }
            doc.Save(path);
        }

        private static Task NfoAdoptionPreservesExistingFilesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var library = Path.Combine(root, "Media");
                var state = Path.Combine(root, "State");
                Directory.CreateDirectory(library);
                foreach (var name in new[] {"Hard Quiz", "Escape to the Country", "Watch What Happens Live"})
                {
                    var path = Path.Combine(library, name + ".nfo");
                    NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode());
                    EnrichNfo(path);
                    File.SetLastWriteTimeUtc(path, OldNfoTime);
                    var before = File.ReadAllBytes(path);
                    using (var owner = new NfoOwnershipState(state, library, name))
                        Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner),
                            "first encounter must adopt enriched metadata without a bulk rewrite");
                    using (var owner = new NfoOwnershipState(state, library, name))
                        Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner), "baseline must survive restart");
                    Assert(before.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime,
                        "adoption and reuse must preserve exact original bytes and timestamp");
                }
                Assert(Directory.GetFiles(library, "*", SearchOption.AllDirectories).Length == 3,
                    "ownership state must not create sidecars in watched libraries");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoThreeWayMergePreservesEnrichmentAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var library = Path.Combine(root, "Media");
                var path = Path.Combine(library, "Pilot.nfo");
                var state = Path.Combine(root, "State");
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner);
                EnrichNfo(path);
                var before = XElement.Load(path);
                before.Element("plot").SetAttributeValue("custom", "preserved");
                before.Save(path);
                var episode = OwnershipEpisode("Genuinely changed provider plot");
                episode.Info.VideoCodec = "hevc";
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, episode, owner), "a still-owned plot must update");
                var after = XElement.Load(path);
                Assert(after.Element("plot")?.Value == episode.Info.Plot && (string)after.Element("plot")?.Attribute("custom") == "preserved",
                    "owned scalar updates must keep Emby-added attributes");
                foreach (var tag in new[] {"uniqueid", "thumb", "dateadded", "custom", "fileinfo"})
                    Assert(XNode.DeepEquals(before.Element(tag), after.Element(tag)), "Emby field must survive: " + tag);
                File.SetLastWriteTimeUtc(path, OldNfoTime);
                var bytes = File.ReadAllBytes(path);
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, episode, owner), "repeat write must be a no-op");
                Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime,
                    "a merged NFO must remain stable across later syncs");

                EnrichNfo(path, true);
                episode.Info.Plot = "Another provider edit";
                episode.Info.Rating = "9.2";
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, episode, owner), "a new provider field can be added");
                after = XElement.Load(path);
                Assert(after.Element("plot")?.Value == "Emby custom plot" && after.Element("rating")?.Value == "9.2",
                    "a conflict must preserve the external value while unrelated new metadata is merged");

                var movie = Path.Combine(library, "Movie.nfo");
                using (var owner = new NfoOwnershipState(state, library, "movie"))
                    NfoWriter.WriteMovieNfo(movie, "Movie", new VodDetailInfo { Plot = "old" }, 1955, owner);
                EnrichNfo(movie);
                using (var owner = new NfoOwnershipState(state, library, "movie"))
                    NfoWriter.WriteMovieNfo(movie, "Movie", new VodDetailInfo { Plot = "new", TmdbId = "123" }, 1955, owner);
                var ids = XElement.Load(movie).Elements("uniqueid").ToArray();
                Assert(ids.Length == 2 && ids.Count(id => (string)id.Attribute("default") == "true") == 1,
                    "adding a provider ID must not remove TVDB or create conflicting default IDs");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoExternalChangesNeverCauseChurnAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var library = Path.Combine(root, "Media");
                var path = Path.Combine(library, "Pilot.nfo");
                var state = Path.Combine(root, "State");
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner);
                var doc = XElement.Load(path);
                doc.ReplaceNodes(doc.Elements().Reverse().Select(e => new XElement(e)));
                doc.Element("plot").Value = "External plot";
                doc.Element("aired").Remove();
                File.WriteAllText(path, doc.ToString(SaveOptions.DisableFormatting));
                File.SetLastWriteTimeUtc(path, OldNfoTime);
                var bytes = File.ReadAllBytes(path);
                var journal = Directory.GetFiles(state, "*.json", SearchOption.AllDirectories).Single();
                File.SetLastWriteTimeUtc(journal, OldNfoTime);
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner), "external changes alone cannot trigger a rewrite");
                Assert(File.GetLastWriteTimeUtc(journal) == OldNfoTime, "a no-op session must not rewrite the journal either");
                var changed = OwnershipEpisode("Provider changed too");
                changed.Info.ReleaseDate = "2026-01-03";
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, changed, owner), "external scalar edit/deletion must win conflicts");
                Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime,
                    "formatting/order, external edits and intentional removals must remain byte-for-byte intact");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoOwnedCodecChangesAreNotLostAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var library = Path.Combine(root, "Media");
                var path = Path.Combine(library, "Pilot.nfo");
                var state = Path.Combine(root, "State");
                var episode = OwnershipEpisode();
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, episode, owner);
                episode.Info.VideoCodec = "hevc";
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, episode, owner), "a genuine provider codec change should update a still-owned media block");
                Assert(XElement.Load(path).Element("fileinfo")?.Element("streamdetails")?.Element("video")?.Element("codec")?.Value == "hevc",
                    "still-owned provider codec must be updated");
                EnrichNfo(path);
                var measured = XElement.Load(path).Element("fileinfo").ToString();
                episode.Info.VideoCodec = "h264";
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, episode, owner), "provider metadata cannot override an externally enriched media block");
                Assert(XElement.Load(path).Element("fileinfo").ToString() == measured, "measured media and subtitle tracks must remain intact");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoLocksMalformedAndMissingAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var library = Path.Combine(root, "Media");
                var path = Path.Combine(library, "Pilot.nfo");
                var state = Path.Combine(root, "State");
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner);
                foreach (var content in new[] {"<broken", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><episodedetails><plot>&e;</plot></episodedetails>", "<movie><title>Wrong root</title></movie>", "<episodedetails><lockdata>true</lockdata><plot>Locked</plot></episodedetails>"})
                {
                    File.WriteAllText(path, content);
                    using (var owner = new NfoOwnershipState(state, library, "provider"))
                        Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode("changed"), owner), "unsafe/locked NFO must not be replaced");
                    Assert(File.ReadAllText(path) == content, "preserve malformed, DTD, wrong-root and locked files");
                }
                File.Delete(path);
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner), "a cached baseline must never suppress restoration of a missing NFO");
                Assert(XElement.Load(path).Element("plot")?.Value == "Provider plot", "restored metadata must be complete");
                var lockedField = XElement.Load(path);
                lockedField.Add(new XElement("lockedfields", "Overview"));
                lockedField.Save(path);
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode("changed"), owner),
                        "an explicitly locked field must remain protected even when its value matches the old provider value");
                Assert(XElement.Load(path).Element("plot")?.Value == "Provider plot", "field-level locks must be respected");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoJournalFailuresAreConservativeAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var library = Path.Combine(root, "Media");
                var path = Path.Combine(library, "Pilot.nfo");
                var state = Path.Combine(root, "State");
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner);
                EnrichNfo(path);
                var original = File.ReadAllBytes(path);
                var journal = Directory.GetFiles(state, "*.json", SearchOption.AllDirectories).Single();
                foreach (var bad in new[] {"{", "{\"Version\":99,\"Generated\":{}}"})
                {
                    File.WriteAllText(journal, bad);
                    using (var owner = new NfoOwnershipState(state, library, "provider"))
                        Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode("new"), owner), "corrupt journal must adopt, not replace");
                    Assert(original.SequenceEqual(File.ReadAllBytes(path)), "journal corruption cannot cause NFO churn");
                }
                using (var oversized = File.OpenWrite(journal)) oversized.SetLength(NfoOwnershipState.MaximumJournalBytes + 1L);
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode("new"), owner), "oversized journals must be rejected without replacing NFOs");
                var blocked = Path.Combine(root, "NotADirectory");
                File.WriteAllText(blocked, "blocked");
                for (var run = 0; run < 2; run++)
                using (var owner = new NfoOwnershipState(blocked, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode("new"), owner), "journal I/O failure is conservative and nonfatal");
                using (var owner = new NfoOwnershipState(state, library, "different-provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode("different source"), owner), "a different provider scope cannot claim the old baseline");
                Assert(original.SequenceEqual(File.ReadAllBytes(path)), "unavailable state or provider changes cannot destroy enrichment");
                File.Delete(path);
                using (var owner = new NfoOwnershipState(blocked, library, "provider"))
                    Assert(NfoWriter.WriteEpisodeNfo(path, "Pilot", 1, 1, OwnershipEpisode(), owner), "state I/O failure cannot prevent creation of a missing NFO");
                Assert(!Directory.GetFiles(state, "*.tmp", SearchOption.AllDirectories).Any(), "journal saves must clean temporary files");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoCommitPreservesConcurrentSavesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var path = Path.Combine(root, "Movie.nfo");
                File.WriteAllText(path, "Emby saved this");
                Assert(!NfoWriter.WriteAtomically(path, "replacement", root, null), "a concurrent new file must never be replaced");
                Assert(!NfoWriter.WriteAtomically(path, "replacement", root, "older contents"), "an existing file changed since reading must defer the update");
                Assert(File.ReadAllText(path) == "Emby saved this", "both guards must preserve Emby's save");
                File.Delete(path);
                Assert(!NfoWriter.WriteAtomically(path, "replacement", root, "older contents"), "a concurrent removal must defer rather than recreate");
                Assert(!File.Exists(path), "the failed update must not recreate a concurrently removed file");
                Assert(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "deferred commits must clean their temporary files");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static async Task SeriesNfoChangesArePerEpisodeAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var documentary in new[] {false, true})
                {
                    var media = Path.Combine(root, documentary.ToString(), "Media");
                    var state = Path.Combine(root, documentary.ToString(), "State");
                    var config = CreateProviderIdConfig(media);
                    config.SmartSkipExisting = true;
                    config.EnableNfoFiles = true;
                    config.EnableLocalMediaFilter = true;
                    var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail);
                    foreach (var e in detail.Episodes["1"]) e.Info = new EpisodeMediaInfo { Plot = "Initial plot" };
                    Func<Task<StrmSyncService>> run = async () =>
                    {
                        var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(
                            JsonResponse(CacheSeriesCatalog), JsonResponse(JsonSerializer.Serialize(detail,
                                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault })))), nfoOwnershipPath: state);
                        service.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                        await service.SyncSeriesAsync(config, CancellationToken.None, isDocuSeries: documentary);
                        var history = service.GetSyncHistory().Single();
                        Assert(history.Success, "series sync must succeed: " + history.FailureReason + " " +
                            string.Join("; ", service.FailedItems.Select(item => item.ErrorMessage)));
                        return service;
                    };
                    await run();
                    var episodes = Directory.GetFiles(media, "*.nfo", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "tvshow.nfo").OrderBy(p => p).ToArray();
                    Assert(episodes.Length == 2, "fixture must materialize two episodes");
                    foreach (var path in episodes) { EnrichNfo(path); File.SetLastWriteTimeUtc(path, OldNfoTime); }
                    var unchanged = File.ReadAllBytes(episodes[1]);
                    var strms = Directory.GetFiles(media, "*.strm", SearchOption.AllDirectories);
                    foreach (var path in strms) File.SetLastWriteTimeUtc(path, OldNfoTime);
                    detail.Episodes["1"][0].Info.Plot = "Only episode one changed";
                    await run();
                    Assert(XElement.Load(episodes[0]).Element("plot")?.Value == "Only episode one changed", "the changed episode must update");
                    Assert(XElement.Load(episodes[0]).Element("uniqueid")?.Value == "98765", "the changed episode must keep TVDB enrichment");
                    Assert(unchanged.SequenceEqual(File.ReadAllBytes(episodes[1])) && File.GetLastWriteTimeUtc(episodes[1]) == OldNfoTime,
                        "a sibling's provider change must not read/replace the unchanged enriched NFO");
                    detail.Episodes["1"].Add(new EpisodeInfo { Id = 3000000004, Season = 1, EpisodeNum = 3, Title = "Three", ContainerExtension = "mkv" });
                    var one = File.ReadAllBytes(episodes[0]);
                    File.SetLastWriteTimeUtc(episodes[0], OldNfoTime);
                    await run();
                    Assert(Directory.GetFiles(media, "*.strm", SearchOption.AllDirectories).Length == 3, "a new episode must still be added");
                    Assert(one.SequenceEqual(File.ReadAllBytes(episodes[0])) && unchanged.SequenceEqual(File.ReadAllBytes(episodes[1])) &&
                        episodes.All(p => File.GetLastWriteTimeUtc(p) == OldNfoTime), "adding one episode cannot churn old NFOs");
                    Assert(strms.All(p => File.GetLastWriteTimeUtc(p) == OldNfoTime), "metadata changes must not rewrite existing STRMs");
                    File.Delete(episodes[1]);
                    await run();
                    Assert(File.Exists(episodes[1]), "missing NFO restoration must work with unchanged series and episode fingerprints");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task EpisodeFingerprintCoversWrittenFieldsAsync()
        {
            foreach (var edit in new Action<EpisodeInfo>[]
            {
                e => e.Info.Director = "Director", e => e.Info.Cast = "Actor", e => e.Info.YoutubeTrailer = "trailer",
                e => e.Info.Width = 3840, e => e.Info.Height = 2160, e => e.Info.RFrameRate = "50/1",
                e => e.Info.AudioCodec = "eac3", e => e.Info.Channels = 6, e => e.Info.MovieImage = "artwork",
                e => e.Info.ReleaseDate = "2026-01-03", e => e.Info.DurationSecs = 3600,
            })
            {
                var episode = OwnershipEpisode();
                var before = NfoWriter.EpisodeMetadataFingerprint("Pilot", 1, 1, episode);
                var episodes = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EpisodeInfo>>
                { ["1"] = new System.Collections.Generic.List<EpisodeInfo> { episode } };
                var config = new PluginConfiguration { EnableNfoFiles = true };
                var seriesBefore = StrmSyncService.ComputeSeriesSyncFingerprint(episodes, config);
                edit(episode);
                Assert(before != NfoWriter.EpisodeMetadataFingerprint("Pilot", 1, 1, episode), "all rendered provider fields must invalidate the episode baseline");
                Assert(seriesBefore != StrmSyncService.ComputeSeriesSyncFingerprint(episodes, config), "whole-series fast skip must notice that episode's metadata change");
            }
            return Task.CompletedTask;
        }

        private static async Task SeriesNfoAdoptionDoesNotScheduleScanAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var media = Path.Combine(root, "Media");
                var config = CreateProviderIdConfig(media);
                config.EnableNfoFiles = true;
                config.SmartSkipExisting = false; // Force every NFO to be considered, as in a changed series.
                var first = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(
                    JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))));
                await first.SyncSeriesAsync(config, CancellationToken.None);
                var files = Directory.GetFiles(media, "*", SearchOption.AllDirectories);
                foreach (var path in files)
                {
                    if (Path.GetExtension(path) == ".nfo") EnrichNfo(path);
                    File.SetLastWriteTimeUtc(path, OldNfoTime);
                }
                var snapshots = files.ToDictionary(path => path, File.ReadAllBytes);
                var second = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(
                    JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), nfoOwnershipPath: Path.Combine(root, "State"));
                await second.SyncSeriesAsync(config, CancellationToken.None);
                Assert(second.GetSyncHistory().Single().Success, "adoption sync must succeed");
                Assert(files.All(path => snapshots[path].SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime),
                    "an adoption-only run cannot touch show/episode NFOs or STRMs");
                var pending = typeof(StrmSyncService).GetField("_libraryScanPending",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert(pending != null && !(bool)pending.GetValue(second), "ownership journal changes alone must never queue an Emby scan");
                Assert(Directory.GetFiles(Path.Combine(root, "State"), "*.json", SearchOption.AllDirectories).Length == 1,
                    "one journal should cover the entire series, not one sidecar per episode");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
