using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace eft_dma_radar.Source.Tarkov
{
    public class PlayerProfileAPI
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly ConcurrentDictionary<string, PlayerStats> _playerStatsCache = new ConcurrentDictionary<string, PlayerStats>();

        private static readonly List<(int Level, int Experience)> _levelChart = new()
        {
            (1, 0), (2, 1000), (3, 4017), (4, 8432), (5, 14256), (6, 21477), (7, 30023), (8, 39936),
            (9, 51204), (10, 63723), (11, 77563), (12, 92713), (13, 111881), (14, 134674), (15, 161139),
            (16, 191417), (17, 225194), (18, 262366), (19, 302484), (20, 345751), (21, 391649), (22, 440444),
            (23, 492366), (24, 547896), (25, 609066), (26, 679255), (27, 755444), (28, 837672), (29, 925976),
            (30, 1020396), (31, 1120969), (32, 1227735), (33, 1344260), (34, 1470605), (35, 1606833),
            (36, 1759965), (37, 1923579), (38, 2097740), (39, 2282513), (40, 2477961), (41, 2684149),
            (42, 2901143), (43, 3132824), (44, 3379281), (45, 3640603), (46, 3929436), (47, 4233995),
            (48, 4554372), (49, 4890662), (50, 5242956), (51, 5611348), (52, 5995931), (53, 6402287),
            (54, 6830542), (55, 7280825), (56, 7753260), (57, 8247975), (58, 8765097), (59, 9304752),
            (60, 9876880), (61, 10512365), (62, 11193911), (63, 11929835), (64, 12727177), (65, 13615989),
            (66, 14626588), (67, 15864243), (68, 17555001), (69, 19926895), (70, 22926895), (71, 26526895),
            (72, 30726895), (73, 35526895), (74, 40926895), (75, 46926895), (76, 53526895), (77, 60726895),
            (78, 69126895), (79, 81126895)
        };

        private static int GetPlayerLevel(int experience) => _levelChart.LastOrDefault(x => experience >= x.Experience).Level;

        private static bool IsValidAccountId(string accountId) => !string.IsNullOrEmpty(accountId) && accountId != "0" && long.TryParse(accountId, out _);

        public static async Task<PlayerStats> GetPlayerStatsAsync(string accountId)
        {
            if (!IsValidAccountId(accountId))
            {
                Program.Log($"[GetPlayerStatsAsync] => Invalid player ID: {accountId}");
                return new PlayerStats { Nickname = "Invalid ID" };
            }

            if (_playerStatsCache.TryGetValue(accountId, out var cached))
                return cached;

            try
            {
                return await FetchPlayerStats(accountId);
            }
            catch
            {
                if (await ReloadPlayerProfileAsync(accountId))
                {
                    try
                    {
                        return await FetchPlayerStats(accountId);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[GetPlayerStatsAsync] => Error fetching player stats '{accountId}' after reload: {ex.Message}");
                        return new PlayerStats { Nickname = "Human" };
                    }
                }
                return new PlayerStats { Nickname = "Human" };
            }
        }

        private static async Task<PlayerStats> FetchPlayerStats(string accountId)
        {
            var url = $"https://players.tarkov.dev/profile/{accountId}.json";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var playerData = await JsonSerializer.DeserializeAsync<PlayerProfile>(
                await response.Content.ReadAsStreamAsync());

            if (playerData?.info?.nickname == null)
                return new PlayerStats { Nickname = "Human" };

            var stats = CreatePlayerStats(playerData);
            _playerStatsCache[accountId] = stats;
            return stats;
        }

        public static async Task<bool> ReloadPlayerProfileAsync(string accountId)
        {
            if (!IsValidAccountId(accountId))
            {
                Program.Log($"[ReloadPlayerProfileAsync] => Invalid player ID.");
                return false;
            }

            try
            {
                var url = $"https://tarkov.dev/players/regular/{accountId}";
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                return true;
            }
            catch (Exception ex)
            {
                Program.Log($"[ReloadPlayerProfileAsync] => Error reloading profile: {ex.Message}");
                return false;
            }
        }

        private static PlayerStats CreatePlayerStats(PlayerProfile profile)
        {
            if (profile?.pmcStats?.eft?.overAllCounters?.Items is null)
            {
                return new PlayerStats
                {
                    Nickname = profile?.info?.nickname ?? "Human",
                    KDRatio = 0,
                    HoursPlayed = 0,
                    Level = profile?.info?.experience is not null ? GetPlayerLevel(profile.info.experience) : 0
                };
            }

            var pmcStats = profile.pmcStats.eft.overAllCounters.Items;
            var kills = pmcStats.FirstOrDefault(x => x.Key.Contains("Kills"))?.Value ?? 0;
            var deaths = pmcStats.FirstOrDefault(x => x.Key.Contains("Deaths"))?.Value ?? 1;

            return new PlayerStats
            {
                Nickname = profile.info.nickname,
                KDRatio = MathF.Round((float)kills / deaths, 2),
                HoursPlayed = profile.pmcStats.eft.totalInGameTime / 3600,
                Level = GetPlayerLevel(profile.info.experience)
            };
        }
    }

    public class PlayerProfile
    {
        public int aid { get; set; }
        public PlayerInfo info { get; set; }
        public PMCStats pmcStats { get; set; }
    }

    public class PlayerInfo
    {
        public string nickname { get; set; }
        public int experience { get; set; }
    }

    public class PMCStats
    {
        public PMCDetail eft { get; set; }
    }

    public class PMCDetail
    {
        public int totalInGameTime { get; set; }
        public PMCOverAllCounters overAllCounters { get; set; }
    }

    public class PMCOverAllCounters
    {
        public List<PMCStatItem> Items { get; set; }
    }

    public class PMCStatItem
    {
        public string[] Key { get; set; }
        public int Value { get; set; }
    }

    public class PlayerStats
    {
        public string Nickname { get; set; }
        public float KDRatio { get; set; }
        public int HoursPlayed { get; set; }
        public int Level { get; set; }
    }
}
