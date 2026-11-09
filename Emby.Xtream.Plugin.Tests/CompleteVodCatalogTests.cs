using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static Task CompleteVodSelectionPreservesMembershipAsync()
        {
            var catalog = XtreamResponseParser.DeserializeVodList(
                "[{\"stream_id\":1,\"name\":\"Cross listed\",\"category_id\":99,\"category_ids\":[99,\"83\",88],\"tmdb_id\":\"123\",\"container_extension\":\"mkv\"}," +
                "{\"stream_id\":2,\"name\":\"Excluded\",\"category_id\":99,\"category_ids\":[99]}," +
                "{\"stream_id\":3,\"name\":\"Second selection\",\"category_id\":83,\"category_ids\":[83]}," +
                "{\"stream_id\":1,\"name\":\"Cross listed duplicate\",\"category_id\":83,\"category_ids\":[83]}]", null);
            List<VodStreamInfo> selected;
            string reason;
            Assert(CompleteVodCatalogFilter.TrySelect(catalog, new[] {88, 83}, out selected, out reason),
                "valid numeric/string membership arrays must allow local filtering");
            Assert(selected.Select(item => item.StreamId).SequenceEqual(new long[] {1,3}) && selected[0].CategoryId == 88,
                "selection must exclude unchecked categories, deduplicate IDs, and retain the first selected category as owner");
            Assert(selected[0].TmdbId == "123" && selected[0].ContainerExtension == "mkv" && catalog[0].CategoryId == 99,
                "category assignment must retain metadata without mutating the original complete response");
            Assert(CompleteVodCatalogFilter.TrySelect(catalog, new[] {83,88}, out selected, out reason) &&
                   selected.Single(item => item.StreamId == 1).CategoryId == 83,
                "reversing selection priority must match the legacy per-category ownership rule");
            Assert(!CompleteVodCatalogFilter.TrySelect(catalog, new[] {777}, out selected, out reason) && selected == null,
                "an absent selected category must fall back rather than publish a misleading empty selection");

            foreach (var members in new[] {"null", "[]", "true", "\"83\"", "[0]", "[83,null]", "[83,2147483648]", "[83,{}]"})
            {
                var ambiguous = XtreamResponseParser.DeserializeVodList(
                    "[{\"stream_id\":1,\"category_id\":83,\"category_ids\":" + members + "}]", null);
                Assert(!CompleteVodCatalogFilter.TrySelect(ambiguous, new[] {83}, out selected, out reason),
                    "ambiguous category membership must require the original category endpoint: " + members);
            }
            var inconsistent = XtreamResponseParser.DeserializeVodList(
                "[{\"stream_id\":1,\"category_id\":99,\"category_ids\":[83]}]", null);
            Assert(!CompleteVodCatalogFilter.TrySelect(inconsistent, new[] {83}, out selected, out reason),
                "contradictory primary/category-list membership must not be trusted");
            var noMembership = XtreamResponseParser.DeserializeVodList("[{\"stream_id\":1}]", null);
            Assert(CompleteVodCatalogFilter.TrySelect(noMembership, new int[0], out selected, out reason) && selected.Count == 1,
                "the existing explicit all-catalog selection must remain supported");
            return Task.CompletedTask;
        }

        private static async Task CompleteVodFetchIsFreshAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                config.PreferCompleteVodCatalog = true;
                var handler = new QueueHandler(
                    JsonResponse("[{\"stream_id\":3000000001,\"name\":\"Chosen Movie\",\"category_id\":99,\"category_ids\":[83,99]}]"),
                    JsonResponse("[{\"stream_id\":3000000002,\"name\":\"Chosen Movie\",\"category_id\":83,\"category_ids\":[83]}]"));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                await service.SyncMoviesAsync(config, CancellationToken.None);
                var path = Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Single();
                Assert(File.ReadAllText(path).Contains("/3000000001."), "the first fresh complete response must produce the selected movie");
                await service.SyncMoviesAsync(config, CancellationToken.None);
                Assert(handler.Requests.Count == 2 && handler.Requests.All(request =>
                    request.RequestUri.Query.Contains("action=get_vod_streams") && !request.RequestUri.Query.Contains("category_id=")),
                    "each enabled sync must make one fresh complete-catalog request, never category requests or a retained old snapshot");
                Assert(File.ReadAllText(path).Contains("/3000000002."),
                    "the next sync must see changed provider data rather than stale catalog content");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task CompleteVodGenreRoutingIsPreservedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                const string json = "[{\"stream_id\":1,\"name\":\"Drama Movie\",\"category_id\":83,\"category_ids\":[83]}," +
                    "{\"stream_id\":2,\"name\":\"Documentary Movie\",\"category_id\":88,\"category_ids\":[88]}]";
                var source = CreateProviderIdConfig(root);
                source.PreferCompleteVodCatalog = true;
                source.EnableGenreBasedLibraryRouting = true;
                source.SyncDocumentaries = true;
                source.SelectedVodCategoryIds = new[] {83,88};
                source.VodGenreCacheJson = "{\"1\":{\"Name\":\"Drama Movie\",\"Genre\":\"Drama\"},\"2\":{\"Name\":\"Documentary Movie\",\"Genre\":\"Documentary\"}}";
                var doc = (PluginConfiguration)typeof(SyncDocumentariesTask)
                    .GetMethod("BuildDocumentaryConfig", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] {source});
                var handler = new QueueHandler(JsonResponse(json), JsonResponse(json));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                await service.SyncMoviesAsync(source, CancellationToken.None);
                await service.SyncMoviesAsync(doc, CancellationToken.None, isDocumentaries:true);
                Assert(Directory.GetFiles(Path.Combine(root,"Movies"), "*.strm", SearchOption.AllDirectories).Single().Contains("Drama Movie") &&
                       Directory.GetFiles(Path.Combine(root,"Documentaries"), "*.strm", SearchOption.AllDirectories).Single().Contains("Documentary Movie"),
                    "the shared source selection must still route each genre into exactly its own library");
                Assert(handler.Requests.Count == 2 && handler.Requests.All(request => !request.RequestUri.Query.Contains("category_id=")),
                    "scheduled documentary configuration must retain complete-catalog mode without fetching details for cached genres");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task CompleteVodFallbackIsCompatibleAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var mode in new[] {"disabled", "missing-membership", "absent-selected-category", "unsupported-endpoint", "fallback-fails"})
                {
                    var folder = Path.Combine(root, mode);
                    var config = CreateProviderIdConfig(folder);
                    config.PreferCompleteVodCatalog = mode != "disabled";
                    var existing = Path.Combine(folder, "Movies", "Existing", "Existing.strm");
                    Directory.CreateDirectory(Path.GetDirectoryName(existing));
                    File.WriteAllText(existing, "https://provider.invalid/movie/u/p/1.mp4");
                    config.LastMovieSyncTimestamp = 100;
                    var responses = new List<HttpResponseMessage>();
                    if (mode == "unsupported-endpoint") responses.Add(new HttpResponseMessage(HttpStatusCode.NotFound));
                    else if (mode == "absent-selected-category") responses.Add(JsonResponse("[{\"stream_id\":1,\"category_id\":99,\"category_ids\":[99]}]"));
                    else if (mode != "disabled") responses.Add(JsonResponse("[{\"stream_id\":1,\"category_id\":83}]"));
                    responses.Add(mode == "fallback-fails" ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
                        JsonResponse("[{\"stream_id\":1,\"name\":\"Existing\",\"category_id\":99}]"));
                    var handler = new QueueHandler(responses.ToArray());
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    var failed = false;
                    try { await service.SyncMoviesAsync(config, CancellationToken.None); }
                    catch (InvalidOperationException) { failed = true; }
                    Assert(failed == (mode == "fallback-fails"), "fallback must succeed only when its own category requests succeed: " + mode);
                    Assert(handler.Requests.Count == (mode == "disabled" ? 1 : 2) && handler.Requests.Last().RequestUri.Query.Contains("category_id=83"),
                        "disabled, unsupported and ambiguous modes must preserve category-specific fetching: " + mode);
                    Assert(File.ReadAllText(existing) == "https://provider.invalid/movie/u/p/1.mp4" && config.LastMovieSyncTimestamp == 100,
                        "fallback must preserve existing files and not advance timestamps on failure");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task InvalidCompleteVodCatalogProtectsFilesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var json in new[] {"[]", "{}", "null", "{", "[{\"stream_id\":0}]", "[{\"name\":\"missing ID\"}]"})
                {
                    var config = CreateProviderIdConfig(root);
                    config.PreferCompleteVodCatalog = true;
                    config.LastMovieSyncTimestamp = 100;
                    var existing = Path.Combine(root, "Movies", "Existing", "Existing.strm");
                    Directory.CreateDirectory(Path.GetDirectoryName(existing));
                    File.WriteAllText(existing, "existing playback URL");
                    var handler = new QueueHandler(JsonResponse(json));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    var failed = false;
                    try { await service.SyncMoviesAsync(config, CancellationToken.None); }
                    catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { failed = true; }
                    Assert(failed && handler.Requests.Count == 1 && service.GetSyncHistory().Single().Success == false,
                        "invalid or empty complete responses must fail protectively before any fallback or file processing");
                    Assert(File.ReadAllText(existing) == "existing playback URL" && config.LastMovieSyncTimestamp == 100 &&
                           Directory.GetFiles(root,"*.strm",SearchOption.AllDirectories).Length == 1,
                        "invalid complete catalogs must preserve files and timestamps even with a 100-percent cleanup threshold");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task CompleteVodCustomFoldersArePreservedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                config.PreferCompleteVodCatalog = true;
                config.SelectedVodCategoryIds = new[] {88,83};
                config.MovieFolderMode = "custom";
                config.MovieFolderMappings = "Preferred=88\nOther=83";
                var handler = new QueueHandler(
                    JsonResponse("[{\"category_id\":88,\"category_name\":\"First\"},{\"category_id\":83,\"category_name\":\"Second\"}]"),
                    JsonResponse("[{\"stream_id\":1,\"name\":\"Cross Listed\",\"category_id\":99,\"category_ids\":[99,83,88]}]"));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                await service.SyncMoviesAsync(config, CancellationToken.None);
                Assert(Directory.GetFiles(root,"*.strm",SearchOption.AllDirectories).Single().Contains(Path.Combine("Movies","Preferred")),
                    "cross-listed records must use the first selected category's custom folder instead of their unselected primary category");
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task CompleteVodSettingIsCopiedAsync()
        {
            var source = new PluginConfiguration { PreferCompleteVodCatalog=true, EnableGenreBasedLibraryRouting=true, SelectedVodCategoryIds=new[] {83,88} };
            var apiType = typeof(Emby.Xtream.Plugin.Api.XtreamTunerApi);
            var api = (PluginConfiguration)apiType.GetMethod("BuildDocumentaryMovieConfig", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] {source});
            var task = (PluginConfiguration)typeof(SyncDocumentariesTask).GetMethod("BuildDocumentaryConfig", BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] {source});
            Assert(api.PreferCompleteVodCatalog && task.PreferCompleteVodCatalog &&
                   api.SelectedVodCategoryIds.SequenceEqual(source.SelectedVodCategoryIds) && task.SelectedVodCategoryIds.SequenceEqual(source.SelectedVodCategoryIds),
                "both manual and scheduled documentary paths must carry the new option and source selection");
            var copy = new PluginConfiguration();
            typeof(Plugin).GetMethod("CopyConfiguration",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] {source,copy});
            Assert(copy.PreferCompleteVodCatalog && !new PluginConfiguration().PreferCompleteVodCatalog,
                "migration must preserve the opt-in option while existing installations retain the disabled default");
            return Task.CompletedTask;
        }

        private static async Task CompleteVodCancellationPreservesFilesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var path = Path.Combine(root, "Movies", "Existing", "Existing.strm");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "existing playback URL");
                var config = CreateProviderIdConfig(root);
                config.PreferCompleteVodCatalog = true;
                using (var cancellation = new CancellationTokenSource())
                {
                    var handler = new CancelCompleteVodHandler(cancellation);
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    var canceled = false;
                    try { await service.SyncMoviesAsync(config, cancellation.Token); }
                    catch (OperationCanceledException) { canceled = true; }
                    Assert(canceled && handler.Requests == 1 && File.ReadAllText(path) == "existing playback URL",
                        "canceling the active complete request must stop immediately without fallback or orphan cleanup");
                    Assert(service.GetSyncHistory().Single().FailureReason.Contains("cancel"),
                        "canceled jobs must persist an explanation rather than an unexplained Failed badge");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class CancelCompleteVodHandler : HttpMessageHandler
        {
            private readonly CancellationTokenSource _cancellation;
            public int Requests;
            public CancelCompleteVodHandler(CancellationTokenSource cancellation) => _cancellation = cancellation;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++;
                _cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(token);
            }
        }

        private static string CreateCompleteVodTestRoot()
        {
            var root = Path.Combine(Path.GetTempPath(),"xc2emby-complete-vod-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
