using System;
using System.Collections.Generic;

namespace JungleTracker
{
    public static class ChampionNameHelper
    {
        // Use a dictionary for easy expansion. Ignore case for lookups.
        private static readonly Dictionary<string, string> NameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Add known inconsistencies between API names and asset file names
            { "Wukong", "MonkeyKing" },
            { "Kha'Zix", "Khazix" }
            // Add other special cases here if needed in the future
            // e.g., { "Fiddlesticks", "FiddleSticks" } if asset names differ
        };

        /// <summary>
        /// Normalizes a champion name obtained from the API or user input
        /// to match the expected format used for asset resource lookups.
        /// </summary>
        /// <param name="championName">The original champion name.</param>
        /// <returns>The normalized name if a mapping exists, otherwise the original name.</returns>
        public static string Normalize(string championName)
        {
            if (string.IsNullOrEmpty(championName))
            {
                return championName; // Return null/empty as is
            }

            // Return the mapped name if it exists, otherwise return the original name unchanged
            return NameMap.TryGetValue(championName, out var normalizedName) ? normalizedName : championName; 
        }
    }
} 