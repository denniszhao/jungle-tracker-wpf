using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net;

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

        public LeagueClientService()
        {
            // Setup HttpClient to ignore SSL cert validation (required for Riot's API)
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler);
        }

        public async Task<bool> TryGetGameDataAsync(int maxRetries = 10, int retryDelayMs = 5000)
        {
            int attempts = 0;
            
            while (attempts < maxRetries)
            {
                try
                {
                    var success = await FetchAndParseGameDataAsync();
                    if (success)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // API might not be ready yet
                    Console.WriteLine($"Attempt {attempts + 1}: Error fetching game data: {ex.Message}");
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
            foreach (var player in allPlayers.EnumerateArray())
            {
                string team = player.GetProperty("team").GetString();
                
                // Only look at enemy team
                if (team != enemyTeam) continue;
                
                // Check for Smite in summoner spells
                var summonerSpells = player.GetProperty("summonerSpells");
                var spell1 = summonerSpells.GetProperty("summonerSpellOne").GetProperty("displayName").GetString();
                var spell2 = summonerSpells.GetProperty("summonerSpellTwo").GetProperty("displayName").GetString();
                
                if (spell1 == "Smite" || spell2 == "Smite")
                {
                    EnemyJunglerChampionName = player.GetProperty("championName").GetString();
                    
                    // Special case for Wukong/MonkeyKing - normalize to "MonkeyKing" for consistency
                    // since that's how the image files are stored
                    if (EnemyJunglerChampionName == "Wukong")
                    {
                        EnemyJunglerChampionName = "MonkeyKing";
                        Console.WriteLine("Normalized Wukong to MonkeyKing to match filename");
                    }
                    
                    Console.WriteLine($"Found enemy jungler: {EnemyJunglerChampionName} on {enemyTeam} side");
                    return true;
                }
            }
            
            // No enemy jungler found with Smite
            Console.WriteLine("No enemy jungler with Smite found");
            return false;
        }
    }
}