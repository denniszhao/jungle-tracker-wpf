using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net;
using System.Diagnostics;

namespace JungleTracker
{
    public class LeagueClientService
    {
        private const string API_BASE_URL = "https://127.0.0.1:2999/liveclientdata";
        private readonly HttpClient _httpClient;
        
        // Data we'll extract
        public string ActivePlayerRiotId { get; private set; }
        public string ActivePlayerTeam { get; private set; }
        public string EnemyJunglerChampionName { get; private set; }
        // New properties to track death status and respawn timer
        public bool EnemyJunglerIsDead { get; private set; }
        public double EnemyJunglerRespawnTimer { get; private set; }

        public LeagueClientService()
        {
            // Setup HttpClient to ignore SSL cert validation (required for Riot's API)
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler);
            
            // Initialize default values
            EnemyJunglerIsDead = false;
            EnemyJunglerRespawnTimer = 0.0;
        }

        public async Task<bool> TryGetGameDataAsync(int maxRetries = 10, int retryDelayMs = 5000)
        {
            int attempts = 0;
            Debug.WriteLine($"Trying to get game data... will try {maxRetries} times");
            
            while (attempts < maxRetries)
            {
                try
                {
                    Debug.WriteLine($"Fetching game data, attempt {attempts + 1}");
                    var success = await FetchAndParseGameDataAsync();
                    if (success)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // API might not be ready yet
                    Debug.WriteLine($"Attempt {attempts + 1}: Error fetching game data: {ex.Message}");
                }
                
                attempts++;
                if (attempts < maxRetries)
                {
                    await Task.Delay(retryDelayMs);
                }
            }
            
            return false;
        }

        private async Task<bool> FetchAndParseGameDataAsync()
        {
            // Request all game data
            string url = $"{API_BASE_URL}/allgamedata";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                // Game might be in loading screen
                return false;
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // 1. Get active player riot ID
            var activePlayer = root.GetProperty("activePlayer");
            ActivePlayerRiotId = activePlayer.GetProperty("riotId").GetString();
            
            string activePlayerChampionName = "";
            string enemyTeam = "";
            
            // 2. Find active player's team and determine enemy team
            var allPlayers = root.GetProperty("allPlayers");
            foreach (var player in allPlayers.EnumerateArray())
            {
                string riotId = player.GetProperty("riotId").GetString();
                
                if (riotId == ActivePlayerRiotId)
                {
                    activePlayerChampionName = player.GetProperty("championName").GetString();
                    ActivePlayerTeam = player.GetProperty("team").GetString();
                    enemyTeam = ActivePlayerTeam == "ORDER" ? "CHAOS" : "ORDER";
                    break;
                }
            }
            
            // 3. Find enemy jungler (player on enemy team with Smite)
            bool enemyJunglerFound = false;
            foreach (var player in allPlayers.EnumerateArray())
            {
                string team = player.GetProperty("team").GetString();
                
                if (team != enemyTeam) continue;
                
                var summonerSpells = player.GetProperty("summonerSpells");
                var spell1 = summonerSpells.GetProperty("summonerSpellOne").GetProperty("displayName").GetString();
                var spell2 = summonerSpells.GetProperty("summonerSpellTwo").GetProperty("displayName").GetString();
                
                if (spell1.Contains("Smite") || spell2.Contains("Smite"))
                {
                    string originalName = player.GetProperty("championName").GetString();

                    // Use the helper method for normalization
                    EnemyJunglerChampionName = ChampionNameHelper.Normalize(originalName);
                    
                    // Get death status and respawn timer
                    EnemyJunglerIsDead = player.GetProperty("isDead").GetBoolean();
                    EnemyJunglerRespawnTimer = EnemyJunglerIsDead ? player.GetProperty("respawnTimer").GetDouble() : 0.0;

                    Debug.WriteLine($"Found enemy jungler: {originalName} (Normalized: {EnemyJunglerChampionName}) on {enemyTeam} side");
                    Debug.WriteLine($"Enemy jungler is {(EnemyJunglerIsDead ? "dead" : "alive")}{(EnemyJunglerIsDead ? $", respawning in {EnemyJunglerRespawnTimer:F1} seconds" : "")}");
                    
                    enemyJunglerFound = true;
                    break;
                }
            }
            
            // If no enemy jungler found with Smite, reset death status properties
            if (!enemyJunglerFound)
            {
                EnemyJunglerIsDead = false;
                EnemyJunglerRespawnTimer = 0.0;
                Debug.WriteLine("No enemy jungler with Smite found");
                return false;
            }
            
            return true;
        }
    }
}