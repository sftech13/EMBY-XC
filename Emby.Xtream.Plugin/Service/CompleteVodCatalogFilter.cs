using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    internal static class CompleteVodCatalogFilter
    {
        internal static bool TrySelect(
            List<VodStreamInfo> catalog,
            int[] selectedCategoryIds,
            out List<VodStreamInfo> selected,
            out string fallbackReason)
        {
            int[] missing;
            if (!TrySelectWithMissingCategories(catalog, selectedCategoryIds, out selected, out missing, out fallbackReason))
                return false;
            if (missing.Length == 0) return true;
            selected = null;
            fallbackReason = "one or more selected categories are absent from the complete response";
            return false;
        }

        internal static bool TrySelectWithMissingCategories(
            List<VodStreamInfo> catalog,
            int[] selectedCategoryIds,
            out List<VodStreamInfo> selected,
            out int[] missingCategoryIds,
            out string fallbackReason)
        {
            selected = null;
            missingCategoryIds = new int[0];
            fallbackReason = string.Empty;
            if (catalog == null || catalog.Count == 0)
                throw new InvalidOperationException("Complete VOD catalog was empty; sync aborted to preserve the existing library.");
            if (catalog.Any(item => item == null || item.StreamId <= 0))
                throw new InvalidOperationException("Complete VOD catalog contains invalid media IDs; sync aborted to preserve the existing library.");

            var categories = selectedCategoryIds ?? new int[0];
            if (categories.Length == 0)
            {
                selected = catalog.GroupBy(item => item.StreamId).Select(group => group.First()).ToList();
                return true;
            }
            if (categories.Any(id => id <= 0))
                throw new InvalidOperationException("Selected VOD categories contain invalid IDs; sync aborted to preserve the existing library.");

            var rank = categories.Distinct().Select((id, index) => new {id, index})
                .ToDictionary(pair => pair.id, pair => pair.index);
            var represented = new HashSet<int>();
            var owners = new Dictionary<long, VodStreamInfo>();
            foreach (var item in catalog)
            {
                if (item.CategoryIds == null || item.CategoryIds.Length == 0 ||
                    item.CategoryIds.Any(id => id <= 0) ||
                    (item.CategoryId.HasValue && item.CategoryId.Value > 0 &&
                     !item.CategoryIds.Contains(item.CategoryId.Value)))
                {
                    fallbackReason = "complete catalog lacks unambiguous category_ids membership";
                    return false;
                }

                var bestCategory = 0;
                var bestRank = int.MaxValue;
                foreach (var id in item.CategoryIds)
                {
                    int position;
                    if (!rank.TryGetValue(id, out position)) continue;
                    represented.Add(id);
                    if (position < bestRank)
                    {
                        bestRank = position;
                        bestCategory = id;
                    }
                }
                if (bestCategory == 0) continue;

                VodStreamInfo previous;
                if (!owners.TryGetValue(item.StreamId, out previous) || bestRank < rank[previous.CategoryId.Value])
                    owners[item.StreamId] = item.WithCategory(bestCategory);
            }

            // Missing categories still require authoritative endpoint checks. Keep
            // the valid part of this snapshot instead of re-fetching every category.
            missingCategoryIds = categories.Distinct().Where(id => !represented.Contains(id)).ToArray();
            selected = owners.Values.OrderBy(item => rank[item.CategoryId.Value]).ToList();
            return true;
        }
    }
}
