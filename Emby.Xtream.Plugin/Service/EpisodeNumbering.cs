using System.Globalization;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    internal static class EpisodeNumbering
    {
        internal static int Season(string mapKey, EpisodeInfo episode)
        {
            if (episode.Season > 0) return episode.Season;
            int season;
            return int.TryParse(mapKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out season) && season >= 0
                ? season : 1;
        }
        internal static int Number(EpisodeInfo episode) => episode.EpisodeNum >= 0 ? episode.EpisodeNum : 1;
    }
}
