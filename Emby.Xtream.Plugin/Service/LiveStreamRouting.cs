using System;
using MediaBrowser.Model.Dto;

namespace Emby.Xtream.Plugin.Service
{
    internal static class LiveStreamRouting
    {
        internal static bool UsesSharedRelay(PluginConfiguration config, string outputFormat)
        {
            return config != null && config.EnableSharedLiveTvRelay &&
                string.Equals(outputFormat, "ts", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ApplyPlaybackPolicy(MediaSourceInfo source, PluginConfiguration config, string outputFormat)
        {
            // Provider-direct playback cannot guarantee connection sharing. Emby's
            // direct-stream path still copies original bytes without forced encoding.
            source.SupportsDirectPlay = config.EnableLiveTvDirectPlay && !UsesSharedRelay(config, outputFormat);
        }

        internal static string BuildLocalRelayUrl(string localApiUrl, string uniqueId, string container)
        {
            if (!Uri.TryCreate(localApiUrl, UriKind.Absolute, out var api) ||
                (api.Scheme != Uri.UriSchemeHttp && api.Scheme != Uri.UriSchemeHttps) ||
                !api.IsLoopback || !string.IsNullOrEmpty(api.UserInfo) ||
                !string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment))
                throw new InvalidOperationException("Shared Live TV requires a valid Emby loopback API URL.");
            if (!Guid.TryParseExact(uniqueId, "N", out _))
                throw new InvalidOperationException("Shared Live TV requires a valid live-stream instance ID.");

            var extension = string.Equals(container, "flv", StringComparison.OrdinalIgnoreCase) ? "flv" : "ts";
            // This is Emby's existing local-only endpoint. It looks up the registered
            // ILiveStream by its opaque instance ID and invokes the same bounded copier
            // as native direct streaming/recording, not another provider connection.
            return api.AbsoluteUri.TrimEnd('/') + "/LiveTv/LiveStreamFiles/" + uniqueId + "/stream." + extension;
        }
    }
}
