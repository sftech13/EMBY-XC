using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static string MultiEpisodeBlock(string root, int number, int season = 1)
        {
            var path = Path.Combine(root, "source-" + season + "-" + number + ".nfo");
            NfoWriter.WriteEpisodeNfo(path, "Episode " + number, season, number, OwnershipEpisode());
            EnrichNfo(path);
            var metadata = XElement.Load(path);
            metadata.Add(new XComment(" keep this episode " + number), new XElement("note", new XCData("<episodedetails> is text, not another root")));
            metadata.SetAttributeValue("custom", "literal > value");
            return metadata.ToString(SaveOptions.DisableFormatting);
        }

        private static Task NfoMultiEpisodeAdoptionAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var count in new[] { 2, 3, 4 })
                foreach (var newline in new[] { "\n", "\r\n", "\r" })
                {
                    var library = Path.Combine(root, "library-" + count + "-" + newline.Length + "-" + (int)newline[0]);
                    Directory.CreateDirectory(library);
                    var path = Path.Combine(library, "Combined.nfo");
                    var blocks = Enumerable.Range(0, count).Select(n => MultiEpisodeBlock(root, n, 0)).Reverse().ToArray();
                    File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" + newline +
                        "<!-- Emby multi-episode; café -->" + newline + string.Join(newline, blocks) + newline, new UTF8Encoding(true));
                    File.SetLastWriteTimeUtc(path, OldNfoTime);
                    var original = File.ReadAllBytes(path);
                    var state = Path.Combine(root, "state");
                    var warnings = new List<string>();
                    for (var run = 0; run < 2; run++)
                    using (var owner = new NfoOwnershipState(state, library, "provider", warnings.Add))
                        for (var n = 0; n < count; n++)
                        {
                            Assert(!NfoWriter.WriteEpisodeNfo(path, "Episode " + n, 0, n, OwnershipEpisode(), owner),
                                "valid sibling roots must be adopted without a write");
                            Assert(owner.MatchesEpisode(path, NfoWriter.EpisodeMetadataFingerprint("Episode " + n, 0, n, OwnershipEpisode()),
                                NfoMetadataDocument.EpisodeIdentity(0, n)), "every episode must retain its own fingerprint");
                        }
                    Assert(warnings.Count == 0, "valid multi-episode XML is not malformed: " + string.Join("; ", warnings));
                    Assert(original.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime,
                        "adoption/restart must preserve all bytes, BOM, declaration, order and timestamp");
                    Assert(Directory.GetFiles(library).Length == 1, "state must remain outside watched libraries");
                }
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoMultiEpisodeMergesAreIsolatedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var newline in new[] { "\n", "\r\n", "\r" })
                {
                    var library = Path.Combine(root, "library-" + newline.Length + "-" + (int)newline[0]);
                    Directory.CreateDirectory(library);
                    var path = Path.Combine(library, "Combined.nfo");
                    var blocks = Enumerable.Range(1, 4).Select(n => MultiEpisodeBlock(root, n)).ToArray();
                    var prefix = "<?xml version=\"1.0\"?>" + newline + "<!-- before -->" + newline;
                    var suffix = newline + "<!-- after -->" + newline;
                    File.WriteAllText(path, prefix + string.Join(newline, blocks) + suffix);
                    var state = Path.Combine(root, "state");
                    using (var owner = new NfoOwnershipState(state, library, "provider"))
                        for (var n = 1; n <= 4; n++) NfoWriter.WriteEpisodeNfo(path, "Episode " + n, 1, n, OwnershipEpisode(), owner);
                    // Update in non-file order and reload the journal every time.
                    foreach (var n in new[] { 3, 1, 4, 2 })
                    {
                        var changed = OwnershipEpisode("Updated provider plot " + n);
                        using (var owner = new NfoOwnershipState(state, library, "provider"))
                            Assert(NfoWriter.WriteEpisodeNfo(path, "Episode " + n, 1, n, changed, owner), "still-owned matching block must update");
                        var result = File.ReadAllText(path);
                        Assert(result.StartsWith(prefix, StringComparison.Ordinal) && result.EndsWith(suffix, StringComparison.Ordinal),
                            "declaration, surrounding comments and whitespace must be exact");
                        for (var sibling = 1; sibling <= 4; sibling++)
                            if (sibling != n) Assert(result.Contains(blocks[sibling - 1], StringComparison.Ordinal), "sibling bytes changed: " + sibling);
                        var parsed = NfoMetadataDocument.Parse(result);
                        var desired = new XElement("episodedetails", new XElement("season", 1), new XElement("episode", n));
                        var target = parsed.Select(desired);
                        Assert(target.Element("plot")?.Value == changed.Info.Plot, "correct plot must change");
                        Assert(target.Element("uniqueid")?.Value == "98765" && target.Element("fileinfo")?.Element("streamdetails")?.Element("video")?.Element("width")?.Value == "3840",
                            "IDs, artwork and measured streams must survive");
                        // Obtain the updated block independently, not by assuming block order in the writer.
                        blocks[n - 1] = target.ToString(SaveOptions.DisableFormatting);
                        File.SetLastWriteTimeUtc(path, OldNfoTime);
                        var bytes = File.ReadAllBytes(path);
                        using (var owner = new NfoOwnershipState(state, library, "provider"))
                            Assert(!NfoWriter.WriteEpisodeNfo(path, "Episode " + n, 1, n, changed, owner), "updated fingerprint must persist across restart");
                        Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime, "repeat must not churn");
                    }
                }
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoMultiEpisodeUnsafeInputsAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var first = MultiEpisodeBlock(root, 1);
                var second = MultiEpisodeBlock(root, 2);
                var invalid = new[]
                {
                    first + first, first + "<movie/>", first + "<episodedetails/>", first + second + "garbage",
                    first + second.Substring(0, second.Length - 5), first + "<?xml version=\"1.0\"?>" + second,
                    "<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///not-to-be-read'>]>" + first + second,
                    first + second.Replace("Provider plot", "&secret;"),
                    first + second.Replace("<season>1</season>", "<season>invalid</season>"),
                    first + second.Replace("<episode>2</episode>", "<episode>2</episode><episode>3</episode>"),
                    "<?xml version=\"broken\"?>" + first + second,
                };
                for (var i = 0; i < invalid.Length; i++)
                {
                    var library = Path.Combine(root, "invalid-" + i);
                    Directory.CreateDirectory(library);
                    var path = Path.Combine(library, "Combined.nfo");
                    File.WriteAllText(path, invalid[i]);
                    File.SetLastWriteTimeUtc(path, OldNfoTime);
                    var bytes = File.ReadAllBytes(path);
                    var warnings = new List<string>();
                    using (var owner = new NfoOwnershipState(Path.Combine(root, "state"), library, "provider", warnings.Add))
                    {
                        Assert(!NfoWriter.WriteEpisodeNfo(path, "Episode 1", 1, 1, OwnershipEpisode(), owner), "unsafe input must be preserved: " + i);
                        Assert(owner.GetPrevious(path, NfoMetadataDocument.EpisodeIdentity(1, 1)) == null, "unsafe input must not be claimed as owned");
                    }
                    Assert(warnings.Count > 0 && bytes.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime,
                        "unsafe input must warn without changing bytes/time: " + i);
                }
                var missing = Path.Combine(root, "MissingEpisode.nfo");
                File.WriteAllText(missing, first + second);
                using (var owner = new NfoOwnershipState(null, root, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(missing, "Episode 3", 1, 3, OwnershipEpisode(), owner), "missing matching block must not be appended or overwrite a sibling");
                Assert(File.ReadAllText(missing) == first + second, "missing identity must preserve all roots");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task NfoMultiEpisodeLegacyAndLocksAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var library = Path.Combine(root, "library");
                Directory.CreateDirectory(library);
                var path = Path.Combine(library, "Combined.nfo");
                var state = Path.Combine(root, "state");
                var first = MultiEpisodeBlock(root, 1);
                var second = MultiEpisodeBlock(root, 2);
                File.WriteAllText(path, first + "\n" + second);
                // Real pre-fix journal shape: exactly one baseline per filename.
                var legacySource = Path.Combine(root, "legacy.nfo");
                NfoWriter.WriteEpisodeNfo(legacySource, "Episode 1", 1, 1, OwnershipEpisode());
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    owner.Record(path, XElement.Load(legacySource).ToString(SaveOptions.DisableFormatting),
                        NfoWriter.EpisodeMetadataFingerprint("Episode 1", 1, 1, OwnershipEpisode()));
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Episode 2", 1, 2, OwnershipEpisode("Different episode baseline"), owner),
                        "legacy baseline for another episode must adopt, not merge");
                Assert(File.ReadAllText(path) == first + "\n" + second, "legacy migration cannot rewrite existing blocks");
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(NfoWriter.WriteEpisodeNfo(path, "Episode 1", 1, 1, OwnershipEpisode("Legacy owned plot changed"), owner),
                        "matching legacy baseline must still support a genuine update");
                // Emby/manual changes, locks and intentional deletions win within the selected block.
                var current = NfoMetadataDocument.Parse(File.ReadAllText(path));
                var target = current.Select(new XElement("episodedetails", new XElement("season", 1), new XElement("episode", 2)));
                target.SetElementValue("plot", "Manual plot");
                target.Element("aired").Remove();
                target.SetElementValue("lockedfields", "Name");
                File.WriteAllText(path, current.Replace(target));
                var before = File.ReadAllBytes(path);
                var changed = OwnershipEpisode("Next provider plot");
                changed.Info.ReleaseDate = "2026-02-01";
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "New provider title", 1, 2, changed, owner), "external edits, deleted fields and locked title must survive");
                Assert(before.SequenceEqual(File.ReadAllBytes(path)), "conflicting update must be a byte-exact no-op");
                File.WriteAllText(path, first.Replace("<lockdata>false</lockdata>", "<lockdata>true</lockdata>") + "\n" + second);
                before = File.ReadAllBytes(path);
                using (var owner = new NfoOwnershipState(state, library, "provider"))
                    Assert(!NfoWriter.WriteEpisodeNfo(path, "Episode 1", 1, 1, OwnershipEpisode("Cannot update a locked episode"), owner), "selected episode lock must be honored");
                Assert(before.SequenceEqual(File.ReadAllBytes(path)), "locked multi-episode file must remain intact");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }
    }
}
