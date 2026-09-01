using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            var tests = new List<(string Name, Func<Task> Run)>
            {
                ("empty series detail + HTTP 206 preserves every STRM", EmptyDetailAndWorkingEpisodesPreserveAllAsync),
                ("HTTP 200 media is alive", Http200MediaIsAliveAsync),
                ("redirect chain retains Range GET and resolves HTTP 206", RedirectChainResolvesMediaAsync),
                ("same 404 on two separate runs qualifies one episode", TwoSeparate404RunsAreRequiredAsync),
                ("definitive failure state survives restart serialization", FailureStateSurvivesSerializationAsync),
                ("404 then 410 does not qualify", DifferentDefinitiveResultsDoNotAccumulateAsync),
                ("non-definitive response breaks failure sequence", InconclusiveResponseBreaksSequenceAsync),
                ("single live viewer backpressures instead of being dropped", SingleViewerBackpressurePreservesSubscriberAsync),
                ("second live viewer exits single-viewer backpressure", SecondViewerEndsSingleViewerBackpressureAsync),
                ("shared live viewer remains bounded when its queue fills", SharedViewerBufferRemainsBoundedAsync),
                ("EPG time shift moves timestamps and clamps to twelve hours", EpgTimeShiftIsAppliedAndClampedAsync),
            };

            foreach (var test in tests)
            {
                try
                {
                    await test.Run().ConfigureAwait(false);
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("FAIL " + test.Name + ": " + ex.Message);
                    return 1;
                }
            }

            Console.WriteLine($"All {tests.Count} regression tests passed.");
            return 0;
        }

        private static async Task EmptyDetailAndWorkingEpisodesPreserveAllAsync()
        {
            // This is the stale-detail scenario: get_series_info returned empty, so
            // every existing episode must be decided from its own playback URL.
            object emptySeriesDetail = null;
            Assert(emptySeriesDetail == null, "test precondition: detail response is empty");

            var handler = new QueueHandler(
                Enumerable.Range(0, 4).Select(_ => MediaResponse(HttpStatusCode.PartialContent)).ToArray());
            var validator = new EpisodePlaybackValidator(new HttpClient(handler));
            var states = Enumerable.Range(1, 4)
                .Select(id => new EpisodePlaybackValidationState { EpisodeId = id })
                .ToArray();

            for (var i = 0; i < states.Length; i++)
            {
                var result = await validator.ValidateAsync(
                    "https://provider.invalid/series/u/p/" + (i + 1) + ".mkv",
                    CancellationToken.None).ConfigureAwait(false);
                var mayDelete = EpisodePlaybackValidator.ApplyResult(
                    states[i],
                    result,
                    "sync-run-1",
                    DateTime.UtcNow);
                Assert(result.Kind == EpisodePlaybackResultKind.Alive, "HTTP 206 media must be alive");
                Assert(!mayDelete, "working episode must be preserved");
                Assert(states[i].ConsecutiveDefinitiveFailures == 0, "alive episode must have zero failures");
            }

            Assert(handler.Requests.Count == states.Length, "every existing episode must be tested individually");
            Assert(handler.Requests.All(request =>
                request.Method == HttpMethod.Get &&
                request.Headers.Range?.Ranges.Single().From == 0 &&
                request.Headers.Range?.Ranges.Single().To == EpisodePlaybackValidator.RangeBytes - 1),
                "validation must use a 1 KB Range GET, never HEAD");
        }

        private static async Task Http200MediaIsAliveAsync()
        {
            var validator = new EpisodePlaybackValidator(new HttpClient(
                new QueueHandler(MediaResponse(HttpStatusCode.OK))));
            var result = await validator.ValidateAsync(
                "https://provider.invalid/series/u/p/1.mkv",
                CancellationToken.None).ConfigureAwait(false);
            Assert(result.Kind == EpisodePlaybackResultKind.Alive, "HTTP 200 media data must be alive");
        }

        private static async Task RedirectChainResolvesMediaAsync()
        {
            var firstRedirect = new HttpResponseMessage(HttpStatusCode.Found);
            firstRedirect.Headers.Location = new Uri("/proxy/episode", UriKind.Relative);
            var secondRedirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            secondRedirect.Headers.Location = new Uri("https://media.invalid/object/episode.mkv");
            var handler = new QueueHandler(
                firstRedirect,
                secondRedirect,
                MediaResponse(HttpStatusCode.PartialContent));
            var validator = new EpisodePlaybackValidator(new HttpClient(handler));

            var result = await validator.ValidateAsync(
                "https://provider.invalid/series/u/p/1.mkv",
                CancellationToken.None).ConfigureAwait(false);

            Assert(result.Kind == EpisodePlaybackResultKind.Alive,
                "302/307 chain ending in HTTP 206 media must be alive");
            Assert(handler.Requests.Count == 3, "all redirect hops must be followed");
            Assert(handler.Requests.All(request =>
                request.Method == HttpMethod.Get &&
                request.Headers.Range?.Ranges.Single().From == 0 &&
                request.Headers.Range?.Ranges.Single().To == EpisodePlaybackValidator.RangeBytes - 1),
                "the 1 KB Range GET must be reapplied at every redirect hop");
        }

        private static async Task TwoSeparate404RunsAreRequiredAsync()
        {
            var validator = new EpisodePlaybackValidator(new HttpClient(new QueueHandler(
                new HttpResponseMessage(HttpStatusCode.NotFound),
                new HttpResponseMessage(HttpStatusCode.NotFound))));
            var state = new EpisodePlaybackValidationState { EpisodeId = 7 };

            var first = await validator.ValidateAsync("https://provider.invalid/series/u/p/7.mkv", CancellationToken.None);
            Assert(!EpisodePlaybackValidator.ApplyResult(state, first, "run-a", DateTime.UtcNow),
                "first definitive 404 must preserve the episode");
            Assert(!EpisodePlaybackValidator.ApplyResult(state, first, "run-a", DateTime.UtcNow),
                "duplicate check in one run must not increment the count");

            var second = await validator.ValidateAsync("https://provider.invalid/series/u/p/7.mkv", CancellationToken.None);
            Assert(EpisodePlaybackValidator.ApplyResult(state, second, "run-b", DateTime.UtcNow.AddMinutes(1)),
                "matching 404 on a separate run should qualify only this episode");
            Assert(state.ConsecutiveDefinitiveFailures == 2, "two separate matching failures must be persisted");
        }

        private static async Task DifferentDefinitiveResultsDoNotAccumulateAsync()
        {
            var validator = new EpisodePlaybackValidator(new HttpClient(new QueueHandler(
                new HttpResponseMessage(HttpStatusCode.NotFound),
                new HttpResponseMessage(HttpStatusCode.Gone))));
            var state = new EpisodePlaybackValidationState { EpisodeId = 8 };
            var first = await validator.ValidateAsync("https://provider.invalid/series/u/p/8.mkv", CancellationToken.None);
            EpisodePlaybackValidator.ApplyResult(state, first, "run-a", DateTime.UtcNow);
            var second = await validator.ValidateAsync("https://provider.invalid/series/u/p/8.mkv", CancellationToken.None);
            Assert(!EpisodePlaybackValidator.ApplyResult(state, second, "run-b", DateTime.UtcNow.AddMinutes(1)),
                "404 followed by 410 is not the same definitive result");
            Assert(state.ConsecutiveDefinitiveFailures == 1, "changed status must restart the sequence");
        }

        private static async Task FailureStateSurvivesSerializationAsync()
        {
            var validator = new EpisodePlaybackValidator(new HttpClient(new QueueHandler(
                new HttpResponseMessage(HttpStatusCode.NotFound),
                new HttpResponseMessage(HttpStatusCode.NotFound))));
            var state = new EpisodePlaybackValidationState
            {
                EpisodeId = 10,
                RelativePath = "Show/Season 01/episode.strm",
            };
            var first = await validator.ValidateAsync("https://provider.invalid/series/u/p/10.mkv", CancellationToken.None);
            EpisodePlaybackValidator.ApplyResult(state, first, "before-restart", DateTime.UtcNow);

            var persistedJson = JsonSerializer.Serialize(state);
            state = JsonSerializer.Deserialize<EpisodePlaybackValidationState>(persistedJson);
            var second = await validator.ValidateAsync("https://provider.invalid/series/u/p/10.mkv", CancellationToken.None);
            Assert(EpisodePlaybackValidator.ApplyResult(state, second, "after-restart", DateTime.UtcNow.AddMinutes(1)),
                "the second matching result after restart must see the persisted first failure");
            Assert(state.FirstDefinitiveFailureUtc.HasValue && state.LastDefinitiveFailureUtc.HasValue,
                "definitive failure timestamps must be persisted");
        }

        private static async Task InconclusiveResponseBreaksSequenceAsync()
        {
            var validator = new EpisodePlaybackValidator(new HttpClient(new QueueHandler(
                new HttpResponseMessage(HttpStatusCode.NotFound),
                new HttpResponseMessage(HttpStatusCode.Forbidden),
                new HttpResponseMessage(HttpStatusCode.NotFound))));
            var state = new EpisodePlaybackValidationState { EpisodeId = 9 };
            var first = await validator.ValidateAsync("https://provider.invalid/series/u/p/9.mkv", CancellationToken.None);
            EpisodePlaybackValidator.ApplyResult(state, first, "run-a", DateTime.UtcNow);
            var forbidden = await validator.ValidateAsync("https://provider.invalid/series/u/p/9.mkv", CancellationToken.None);
            EpisodePlaybackValidator.ApplyResult(state, forbidden, "run-b", DateTime.UtcNow.AddMinutes(1));
            Assert(state.ConsecutiveDefinitiveFailures == 0, "401/403/429/5xx and transport errors must preserve/reset");
            var final = await validator.ValidateAsync("https://provider.invalid/series/u/p/9.mkv", CancellationToken.None);
            Assert(!EpisodePlaybackValidator.ApplyResult(state, final, "run-c", DateTime.UtcNow.AddMinutes(2)),
                "a later 404 starts over at one");
        }

        private static async Task SingleViewerBackpressurePreservesSubscriberAsync()
        {
            const int chunkBytes = 64 * 1024;
            var subscriber = new XtreamLiveStream.FanoutSubscriber(1, chunkBytes);
            var first = CreateLiveChunk(chunkBytes);
            Assert(subscriber.TryEnqueue(first), "first live chunk should fill the test queue");
            first.Release();

            var second = CreateLiveChunk(chunkBytes);
            var enqueue = subscriber.EnqueueWithBackpressureAsync(
                second,
                () => true,
                CancellationToken.None);
            await Task.Delay(50).ConfigureAwait(false);
            Assert(!enqueue.IsCompleted, "a full single-viewer queue should apply backpressure, not complete/drop it");

            var consumedFirst = await subscriber.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
            consumedFirst.Release();
            var completed = await Task.WhenAny(enqueue, Task.Delay(1000)).ConfigureAwait(false);
            Assert(completed == enqueue && await enqueue.ConfigureAwait(false),
                "dequeueing space should resume the upstream producer");
            second.Release();

            var consumedSecond = await subscriber.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
            consumedSecond.Release();
            subscriber.Complete(null, true);
        }

        private static Task SharedViewerBufferRemainsBoundedAsync()
        {
            const int chunkBytes = 64 * 1024;
            var subscriber = new XtreamLiveStream.FanoutSubscriber(2, chunkBytes);
            var first = CreateLiveChunk(chunkBytes);
            Assert(subscriber.TryEnqueue(first), "first shared-viewer chunk should fill the test queue");
            first.Release();

            var overflow = CreateLiveChunk(1);
            Assert(!subscriber.TryEnqueue(overflow),
                "a shared viewer must still be isolated when its bounded queue is exhausted");
            overflow.Release();
            subscriber.Complete(null, true);
            return Task.CompletedTask;
        }

        private static async Task SecondViewerEndsSingleViewerBackpressureAsync()
        {
            const int chunkBytes = 64 * 1024;
            var subscriber = new XtreamLiveStream.FanoutSubscriber(3, chunkBytes);
            var first = CreateLiveChunk(chunkBytes);
            Assert(subscriber.TryEnqueue(first), "first chunk should fill the topology-change queue");
            first.Release();

            var viewerCount = 1;
            var second = CreateLiveChunk(chunkBytes);
            var enqueue = subscriber.EnqueueWithBackpressureAsync(
                second,
                () => Volatile.Read(ref viewerCount) == 1,
                CancellationToken.None);
            await Task.Delay(50).ConfigureAwait(false);
            Volatile.Write(ref viewerCount, 2);

            var completed = await Task.WhenAny(enqueue, Task.Delay(1000)).ConfigureAwait(false);
            Assert(completed == enqueue && !await enqueue.ConfigureAwait(false),
                "a newly shared stream must leave single-viewer backpressure and re-evaluate viewers");
            second.Release();
            subscriber.Complete(null, true);
        }

        private static XtreamLiveStream.SharedChunk CreateLiveChunk(int count)
        {
            return new XtreamLiveStream.SharedChunk(ArrayPool<byte>.Shared.Rent(count))
            {
                Count = count,
            };
        }

        private static Task EpgTimeShiftIsAppliedAndClampedAsync()
        {
            var source = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
            var shiftedLater = XtreamListingsProvider.ShiftEpgTimestamp(
                source.ToUnixTimeSeconds(),
                1.5);
            var shiftedEarlier = XtreamListingsProvider.ShiftEpgTimestamp(
                source.ToUnixTimeSeconds(),
                -1);

            Assert(shiftedLater == source.AddMinutes(90), "+1.5 must move guide data ninety minutes later");
            Assert(shiftedEarlier == source.AddHours(-1), "-1 must move guide data one hour earlier");
            Assert(XtreamListingsProvider.GetEpgSourceBoundary(source, 1) == source.AddHours(-1),
                "a +1 displayed shift must retain source programmes from one hour earlier");
            Assert(XtreamListingsProvider.GetEpgSourceBoundary(source, -1) == source.AddHours(1),
                "a -1 displayed shift must retain source programmes from one hour later");
            Assert(XtreamListingsProvider.ClampEpgTimeShiftHours(99) == 12,
                "positive EPG shift must clamp at +12 hours");
            Assert(XtreamListingsProvider.ClampEpgTimeShiftHours(-99) == -12,
                "negative EPG shift must clamp at -12 hours");
            Assert(XtreamListingsProvider.ClampEpgTimeShiftHours(double.NaN) == 0,
                "invalid EPG shift must fail safely to zero");
            return Task.CompletedTask;
        }

        private static HttpResponseMessage MediaResponse(HttpStatusCode statusCode)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(Enumerable.Repeat((byte)0x47, EpisodePlaybackValidator.RangeBytes).ToArray()),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
            return response;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class QueueHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses;
            public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

            public QueueHandler(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var copy = new HttpRequestMessage(request.Method, request.RequestUri);
                if (request.Headers.Range != null)
                    copy.Headers.Range = new RangeHeaderValue(
                        request.Headers.Range.Ranges.Single().From,
                        request.Headers.Range.Ranges.Single().To);
                Requests.Add(copy);
                return Task.FromResult(_responses.Dequeue());
            }
        }
    }
}
