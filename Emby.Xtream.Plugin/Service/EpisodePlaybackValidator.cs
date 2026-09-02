using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    internal enum EpisodePlaybackResultKind
    {
        Alive,
        Definitive404,
        Definitive410,
        Inconclusive,
    }

    internal sealed class EpisodePlaybackResult
    {
        public EpisodePlaybackResultKind Kind { get; set; }
        public int? StatusCode { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public int BytesRead { get; set; }
        public string Detail { get; set; } = string.Empty;

        public bool IsDefinitiveDead =>
            Kind == EpisodePlaybackResultKind.Definitive404 ||
            Kind == EpisodePlaybackResultKind.Definitive410;
    }

    /// <summary>
    /// Persistent, privacy-safe state for an episode playback URL. The URL and
    /// provider credentials are deliberately not stored.
    /// </summary>
    internal sealed class EpisodePlaybackValidationState
    {
        public int EpisodeId { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string LastResult { get; set; } = string.Empty;
        public int? LastStatusCode { get; set; }
        public int ConsecutiveDefinitiveFailures { get; set; }
        public int ConsecutiveCatalogAbsences { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastCheckedUtc { get; set; }
        public DateTime? LastAliveUtc { get; set; }
        public DateTime? FirstDefinitiveFailureUtc { get; set; }
        public DateTime? LastDefinitiveFailureUtc { get; set; }
        public DateTime? FirstCatalogAbsentUtc { get; set; }
        public DateTime? LastCatalogAbsentUtc { get; set; }
        public string LastValidationRunId { get; set; } = string.Empty;
        public string LastCatalogRunId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Validates the real episode URL with a tiny Range GET. HEAD is avoided
    /// because a number of Xtream providers do not implement it correctly.
    /// </summary>
    internal sealed class EpisodePlaybackValidator
    {
        internal const int RangeBytes = 1024;
        internal const int MaxRedirects = 5;
        internal const string DefaultMediaUserAgent = "VLC/3.0.20 LibVLC/3.0.20";
        private readonly HttpClient _httpClient;
        private readonly string _userAgent;

        public EpisodePlaybackValidator(HttpClient httpClient, string userAgent = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _userAgent = string.IsNullOrWhiteSpace(userAgent)
                ? DefaultMediaUserAgent
                : userAgent.Trim();
        }

        public async Task<EpisodePlaybackResult> ValidateAsync(
            string playbackUrl,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(playbackUrl))
            {
                return Inconclusive(null, "STRM did not contain an absolute playback URL");
            }

            Uri uri;
            if (!Uri.TryCreate(playbackUrl.Trim().TrimStart('\uFEFF'), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Inconclusive(null, "STRM did not contain an HTTP(S) playback URL");
            }

            try
            {
                var currentUri = uri;
                for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, currentUri))
                    {
                        // Reapply Range on every hop. Some Xtream frontends redirect
                        // through a local proxy and then to object storage.
                        request.Headers.Range = new RangeHeaderValue(0, RangeBytes - 1);
                        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                        using (var response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken).ConfigureAwait(false))
                        {
                            var status = (int)response.StatusCode;
                            var mediaType = response.Content?.Headers?.ContentType?.MediaType ?? string.Empty;
                            if (IsRedirectStatus(response.StatusCode))
                            {
                                if (redirectCount >= MaxRedirects || response.Headers.Location == null)
                                {
                                    return Inconclusive(
                                        status,
                                        "playback endpoint redirect chain was missing a location or exceeded " +
                                        MaxRedirects.ToString(CultureInfo.InvariantCulture) + " hops",
                                        mediaType);
                                }

                                var redirectBase = response.RequestMessage?.RequestUri ?? currentUri;
                                Uri redirectUri;
                                if (!Uri.TryCreate(redirectBase, response.Headers.Location, out redirectUri) ||
                                    (redirectUri.Scheme != Uri.UriSchemeHttp && redirectUri.Scheme != Uri.UriSchemeHttps))
                                {
                                    return Inconclusive(
                                        status,
                                        "playback endpoint returned an invalid/non-HTTP redirect",
                                        mediaType);
                                }

                                currentUri = redirectUri;
                                continue;
                            }

                            if (response.StatusCode == HttpStatusCode.NotFound ||
                                response.StatusCode == HttpStatusCode.Gone)
                            {
                                return new EpisodePlaybackResult
                                {
                                    Kind = response.StatusCode == HttpStatusCode.NotFound
                                        ? EpisodePlaybackResultKind.Definitive404
                                        : EpisodePlaybackResultKind.Definitive410,
                                    StatusCode = status,
                                    ContentType = mediaType,
                                    Detail = "playback endpoint returned definitive HTTP " +
                                        status.ToString(CultureInfo.InvariantCulture),
                                };
                            }

                            if (response.StatusCode != HttpStatusCode.OK &&
                                response.StatusCode != HttpStatusCode.PartialContent)
                            {
                                return Inconclusive(
                                    status,
                                    "Range GET playback endpoint returned non-definitive HTTP " +
                                    status.ToString(CultureInfo.InvariantCulture),
                                    mediaType);
                            }

                            if (!IsMediaContentType(mediaType))
                            {
                                return Inconclusive(
                                    status,
                                    "Range GET playback endpoint returned HTTP " +
                                    status.ToString(CultureInfo.InvariantCulture) +
                                    " with non-media content type '" + mediaType + "'",
                                    mediaType);
                            }

                            var bytesRead = 0;
                            if (response.Content != null)
                            {
                                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                {
                                    var buffer = new byte[RangeBytes];
                                    while (bytesRead < buffer.Length)
                                    {
                                        var read = await stream.ReadAsync(
                                            buffer,
                                            bytesRead,
                                            buffer.Length - bytesRead,
                                            cancellationToken).ConfigureAwait(false);
                                        if (read == 0) break;
                                        bytesRead += read;
                                    }
                                }
                            }

                            if (bytesRead == 0)
                            {
                                return Inconclusive(
                                    status,
                                    "Range GET playback endpoint returned media headers but no data",
                                    mediaType);
                            }

                            return new EpisodePlaybackResult
                            {
                                Kind = EpisodePlaybackResultKind.Alive,
                                StatusCode = status,
                                ContentType = mediaType,
                                BytesRead = bytesRead,
                                Detail = "Range GET playback endpoint returned HTTP " +
                                    status.ToString(CultureInfo.InvariantCulture) +
                                    " media data after " +
                                    redirectCount.ToString(CultureInfo.InvariantCulture) +
                                    " explicit redirect(s)",
                            };
                        }
                    }
                }

                return Inconclusive(null, "playback endpoint redirect limit was exceeded");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                return Inconclusive(null, "playback endpoint timed out: " + ex.Message);
            }
            catch (HttpRequestException ex)
            {
                return Inconclusive(null, "playback endpoint connection/TLS failure: " + ex.Message);
            }
            catch (IOException ex)
            {
                return Inconclusive(null, "playback endpoint read failure: " + ex.Message);
            }
        }

        internal static bool ApplyResult(
            EpisodePlaybackValidationState state,
            EpisodePlaybackResult result,
            string syncRunId,
            DateTime checkedUtc)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (result == null) throw new ArgumentNullException(nameof(result));

            // A path is validated at most once per sync run, even when provider
            // records overlap categories or folders.
            if (string.Equals(state.LastValidationRunId, syncRunId, StringComparison.Ordinal))
                return state.ConsecutiveDefinitiveFailures >= 2;

            var currentResult = result.Kind.ToString();
            var sameDefinitiveResult = result.IsDefinitiveDead &&
                string.Equals(state.LastResult, currentResult, StringComparison.Ordinal);

            state.LastResult = currentResult;
            state.LastStatusCode = result.StatusCode;
            state.LastCheckedUtc = checkedUtc;
            state.LastValidationRunId = syncRunId ?? string.Empty;
            if (state.FirstSeenUtc == default(DateTime))
                state.FirstSeenUtc = checkedUtc;

            if (result.IsDefinitiveDead)
            {
                state.ConsecutiveDefinitiveFailures = sameDefinitiveResult
                    ? state.ConsecutiveDefinitiveFailures + 1
                    : 1;
                if (!sameDefinitiveResult)
                    state.FirstDefinitiveFailureUtc = checkedUtc;
                state.LastDefinitiveFailureUtc = checkedUtc;
            }
            else
            {
                // Alive and inconclusive checks both break a consecutive sequence.
                // This intentionally favors preservation when the provider is flaky.
                state.ConsecutiveDefinitiveFailures = 0;
                state.FirstDefinitiveFailureUtc = null;
                state.LastDefinitiveFailureUtc = null;
                if (result.Kind == EpisodePlaybackResultKind.Alive)
                    state.LastAliveUtc = checkedUtc;
            }

            return result.IsDefinitiveDead &&
                state.ConsecutiveDefinitiveFailures >= 2;
        }

        internal static bool IsMediaContentType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType)) return false;
            return mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                   mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mediaType, "application/x-mpegurl", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mediaType, "application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mediaType, "application/x-matroska", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRedirectStatus(HttpStatusCode statusCode)
        {
            var value = (int)statusCode;
            return value == 301 || value == 302 || value == 303 || value == 307 || value == 308;
        }

        private static EpisodePlaybackResult Inconclusive(
            int? statusCode,
            string detail,
            string mediaType = "")
        {
            return new EpisodePlaybackResult
            {
                Kind = EpisodePlaybackResultKind.Inconclusive,
                StatusCode = statusCode,
                ContentType = mediaType ?? string.Empty,
                Detail = detail ?? string.Empty,
            };
        }
    }
}
