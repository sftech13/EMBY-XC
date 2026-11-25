using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Dto;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static Task SharedRelayDefaultsAndPolicyAsync()
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            var config = (PluginConfiguration)serializer.Deserialize(new StringReader(
                "<PluginConfiguration><EnableLiveTvDirectPlay>true</EnableLiveTvDirectPlay></PluginConfiguration>"));
            Assert(config.EnableSharedLiveTvRelay, "existing configurations without the new setting default to shared relay");
            var streams = new List<MediaBrowser.Model.Entities.MediaStream>
            {
                new MediaBrowser.Model.Entities.MediaStream { Codec = "hevc", Index = 0 },
                new MediaBrowser.Model.Entities.MediaStream { Codec = "eac3", Index = 1 }
            };
            var source = new MediaSourceInfo { SupportsDirectStream = true, SupportsTranscoding = true, MediaStreams = streams };
            LiveStreamRouting.ApplyPlaybackPolicy(source, config, "ts");
            Assert(!source.SupportsDirectPlay && source.SupportsDirectStream && source.SupportsTranscoding,
                "shared mode prevents provider-direct bypass without requiring video encoding");
            Assert(ReferenceEquals(source.MediaStreams, streams) && streams[0].Codec == "hevc",
                "routing must not substitute codecs, captions, bitrates or media indexes");
            LiveStreamRouting.ApplyPlaybackPolicy(source, config, "m3u8");
            Assert(source.SupportsDirectPlay && !LiveStreamRouting.UsesSharedRelay(config, "m3u8"),
                "explicit provider HLS retains its existing path and capabilities");
            config.EnableSharedLiveTvRelay = false;
            LiveStreamRouting.ApplyPlaybackPolicy(source, config, "ts");
            Assert(source.SupportsDirectPlay, "advanced opt-out retains provider-direct playback");
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, config);
                config = (PluginConfiguration)serializer.Deserialize(new StringReader(writer.ToString()));
                Assert(!config.EnableSharedLiveTvRelay, "an explicit opt-out persists across settings/restarts");
            }
            return Task.CompletedTask;
        }

        private static Task SharedRelayFallbackIsConservativeAsync()
        {
            var manager = AdaptiveHlsFallbackManager.Instance;
            manager.ResetForTests();
            try
            {
                Assert(typeof(AdaptiveHlsFallbackManager).GetMethod("RecordPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic) == null &&
                    typeof(XtreamServerEntryPoint).GetMethod("OnPlaybackStopped", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                    "normal playback stops no longer have a transport-change handler or evidence store");
                var config = new PluginConfiguration { EnableAdaptiveHlsFallback = true };
                var now = DateTime.UtcNow;
                for (var i = 0; i < 6; i++)
                    Assert(!manager.RecordUpstreamReadStall("xtream_live_42", 2, config, null, now.AddSeconds(i)),
                        "shared mode must not silently change a shared TS channel to unshared provider HLS");
                Assert(manager.GetEffectiveOutputFormat(42, config, null, now.AddMinutes(1), out var adaptive) == "ts" && !adaptive,
                    "existing enabled fallback settings cannot override recommended shared mode");
                config.EnableSharedLiveTvRelay = false;
                for (var i = 0; i < 3; i++) manager.RecordUpstreamReadStall("xtream_live_42", 2, config, null, now.AddSeconds(i));
                Assert(manager.GetEffectiveOutputFormat(42, config, null, now.AddMinutes(1), out adaptive) == "m3u8" && adaptive,
                    "advanced provider-direct mode still permits confirmed-stall fallback");
                config.EnableSharedLiveTvRelay = true;
                Assert(manager.GetEffectiveOutputFormat(42, config, null, now.AddMinutes(2), out adaptive) == "ts" && !adaptive,
                    "enabling shared mode takes precedence over an already active override");
                config.LiveTvOutputFormat = "m3u8";
                Assert(manager.GetEffectiveOutputFormat(42, config, null, now, out adaptive) == "m3u8" && !adaptive,
                    "an explicit HLS selection is not silently changed to MPEG-TS");
            }
            finally { manager.ResetForTests(); }
            return Task.CompletedTask;
        }

        private static Task SharedRelayUrlsAreLocalAsync()
        {
            var id = Guid.NewGuid().ToString("N");
            foreach (var api in new[] { "http://127.0.0.1:8097", "https://localhost:8921/emby/", "http://[::1]:1234/base" })
            {
                var uri = new Uri(LiveStreamRouting.BuildLocalRelayUrl(api, id, "mpegts"));
                Assert(uri.IsLoopback && uri.Port == new Uri(api).Port && uri.AbsolutePath.EndsWith("/LiveTv/LiveStreamFiles/" + id + "/stream.ts"),
                    "relay URL must use Emby's configured port/base path and opaque registered stream ID");
                Assert(uri.Query.Length == 0 && uri.UserInfo.Length == 0, "relay URLs need neither provider credentials nor an Emby API key");
            }
            foreach (var api in new[] { "http://provider.invalid:8096", "http://127.0.0.1.evil.invalid", "file:///tmp/emby", "http://user:secret@127.0.0.1", "http://localhost/?token=secret", "http://localhost/#fragment", "bad" })
            {
                var rejected = false;
                try { LiveStreamRouting.BuildLocalRelayUrl(api, id, "mpegts"); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "unsafe relay origins must fail closed, not fall back to an unshared provider URL");
            }
            return Task.CompletedTask;
        }

        private static async Task SharedRelayOpenIsDeferredAsync()
        {
            var handler = new QueueHandler();
            using (var client = new HttpClient(handler))
            {
                var source = new MediaSourceInfo { Id = "xtream_live_42", Path = "http://provider.invalid/live/user/password/42.ts", Container = "mpegts" };
                using (var live = new XtreamLiveStream(source, "tuner", client, localApiUrl: "http://127.0.0.1:18096/emby"))
                {
                    await live.Open(CancellationToken.None);
                    var path = source.Path;
                    await live.Open(CancellationToken.None);
                    Assert(path == source.Path && path.Contains(live.UniqueId) && !path.Contains("password"), "opening is idempotent and returns only the local relay");
                    Assert(handler.Requests.Count == 0 && !source.SupportsDirectPlay && !source.IsRemote && live.SupportsCopyTo,
                        "opening remains fast/deferred; metadata does not open a provider feed");
                }
                var hls = new MediaSourceInfo { Id = "xtream_live_43", Path = "http://provider.invalid/live/u/p/43.m3u8", Container = "hls" };
                using (var live = new XtreamLiveStream(hls, "tuner", client, localApiUrl: "http://localhost:18096"))
                {
                    await live.Open(CancellationToken.None);
                    Assert(hls.Path.EndsWith("43.m3u8") && !live.SupportsCopyTo, "HLS must never enter a continuous-byte relay");
                }
            }
        }

        private sealed class PushLiveStream : Stream
        {
            internal readonly Pipe Data = new Pipe();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                var result = await Data.Reader.ReadAsync(token);
                var bytes = (int)Math.Min(count, result.Buffer.Length);
                result.Buffer.Slice(0, bytes).CopyTo(buffer.AsSpan(offset, bytes));
                Data.Reader.AdvanceTo(result.Buffer.GetPosition(bytes));
                return bytes;
            }
        }

        private static async Task<byte[]> ReadLiveBytes(PipeReader reader, int length, CancellationToken token)
        {
            var data = new List<byte>();
            while (data.Count < length)
            {
                var result = await reader.ReadAsync(token);
                data.AddRange(result.Buffer.ToArray());
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
            return data.ToArray();
        }

        private static async Task SharedRelayMixedConsumersUseOneUpstreamAsync()
        {
            using (var upstream = new PushLiveStream())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(upstream) });
                using (var client = new HttpClient(handler))
                using (var live = new XtreamLiveStream(new MediaSourceInfo
                {
                    Id = "xtream_live_42", Path = "http://provider.invalid/live/u/p/42.ts", Container = "mpegts",
                    RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = "VLC/3.0.21" }
                }, "tuner", client, localApiUrl: "http://localhost:18096/emby"))
                {
                    await live.Open(timeout.Token);
                    var direct = new Pipe();
                    var transcoder = new Pipe();
                    // Native LiveStreamFiles invokes ILiveStream.CopyToAsync just like
                    // Emby's direct-stream writer. Both must use the same producer.
                    var a = live.CopyToAsync(direct.Writer, firstCancellation.Token);
                    var b = live.CopyToAsync(transcoder.Writer, timeout.Token);
                    try
                    {
                        var expected = Enumerable.Range(0, 752).Select(i => (byte)(i % 251)).ToArray();
                        await upstream.Data.Writer.WriteAsync(expected, timeout.Token);
                        Assert((await ReadLiveBytes(direct.Reader, expected.Length, timeout.Token)).SequenceEqual(expected), "native consumer preserves exact bytes");
                        Assert((await ReadLiveBytes(transcoder.Reader, expected.Length, timeout.Token)).SequenceEqual(expected), "transcoder consumer receives the same original bytes");
                        Assert(handler.Requests.Count == 1 && handler.Requests[0].RequestUri.Host == "provider.invalid" &&
                            handler.Requests[0].Headers.UserAgent.ToString() == "VLC/3.0.21",
                            "both consumers share one configured upstream request, never recursively request the local relay");
                        firstCancellation.Cancel();
                        try { await a; } catch (OperationCanceledException) { }
                        await live.Close();
                        await upstream.Data.Writer.WriteAsync(expected, timeout.Token);
                        Assert((await ReadLiveBytes(transcoder.Reader, expected.Length, timeout.Token)).SequenceEqual(expected) && handler.Requests.Count == 1,
                            "one consumer leaving/Emby Close must not terminate or reconnect the remaining response");
                    }
                    finally
                    {
                        timeout.Cancel();
                        live.Dispose();
                        try { await a; } catch (OperationCanceledException) { }
                        try { await b; } catch (OperationCanceledException) { }
                        await direct.Reader.CompleteAsync();
                        await transcoder.Reader.CompleteAsync();
                        await upstream.Data.Writer.CompleteAsync();
                    }
                }
            }
        }
    }
}
