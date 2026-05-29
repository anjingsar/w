using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivWidget;

public class FFLogsService
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public FFLogsService(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _httpClient = new HttpClient();
    }

    private async Task EnsureAccessTokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry)
            return;

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.fflogs.com/oauth/token")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);

        _accessToken = tokenResponse?.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds((tokenResponse?.ExpiresIn ?? 3600) - 60);
    }

    public async Task<(int cleared, int total, string label)> GetRaidProgressAsync(
        string characterName, string serverSlug, string serverRegion, string raidType)
    {
        await EnsureAccessTokenAsync();

        string label;

        if (raidType.Equals("Ultimate", StringComparison.OrdinalIgnoreCase))
        {
            label = "Ultimate";
            var (cleared, total) = await GetUltimateProgressAsync(characterName, serverSlug, serverRegion);
            return (cleared, total, label);
        }
        else
        {
            label = "Savage";
            var (cleared, total) = await GetSavageProgressAsync(characterName, serverSlug, serverRegion);
            return (cleared, total, label);
        }
    }

    private async Task<(int cleared, int total)> GetSavageProgressAsync(
        string characterName, string serverSlug, string serverRegion)
    {
        var zonesQuery = @"
        query {
            worldData {
                zones(expansion_id: 5) {
                    id
                    name
                    difficulties {
                        id
                        name
                    }
                    encounters {
                        id
                    }
                }
            }
        }";
        var zonesResult = await ExecuteGraphQLAsync(zonesQuery);
        var zones = zonesResult.RootElement.GetProperty("data").GetProperty("worldData").GetProperty("zones");

        int? latestSavageZoneId = null;
        int savageEncountersCount = 0;

        foreach (var z in zones.EnumerateArray())
        {
            var name = z.GetProperty("name").GetString() ?? "";
            bool isSavage = name.Contains("Savage", StringComparison.OrdinalIgnoreCase);

            if (!isSavage && z.TryGetProperty("difficulties", out var diffs) && diffs.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in diffs.EnumerateArray())
                {
                    if (d.TryGetProperty("id", out var dId) && dId.GetInt32() == 101)
                    {
                        isSavage = true;
                        break;
                    }
                }
            }

            if (isSavage)
            {
                latestSavageZoneId = z.GetProperty("id").GetInt32();
                if (z.TryGetProperty("encounters", out var encs) && encs.ValueKind == JsonValueKind.Array)
                    savageEncountersCount = encs.GetArrayLength();
                break;
            }
        }

        if (latestSavageZoneId == null) return (0, 0);

        var query = @"
        query {
            characterData {
                character(name: """ + EscapeGraphQL(characterName) + @""", serverSlug: """ + EscapeGraphQL(serverSlug) + @""", serverRegion: """ + EscapeGraphQL(serverRegion) + @""") {
                    zoneRankings(zoneID: " + latestSavageZoneId + @", difficulty: 101, metric: rdps)
                }
            }
        }";

        var result = await ExecuteGraphQLAsync(query);

        try
        {
            var characterData = result.RootElement
                .GetProperty("data")
                .GetProperty("characterData")
                .GetProperty("character");

            var rankingsData = characterData.GetProperty("zoneRankings");

            if ((rankingsData.TryGetProperty("rankings", out var ranks) || rankingsData.TryGetProperty("ranks", out ranks))
                && ranks.ValueKind == JsonValueKind.Array)
            {
                int total = savageEncountersCount > 0 ? savageEncountersCount : ranks.GetArrayLength();
                int cleared = 0;
                foreach (var rank in ranks.EnumerateArray())
                {
                    if (rank.TryGetProperty("totalKills", out var kills) && kills.GetInt32() > 0)
                        cleared++;
                }
                return (cleared, total);
            }
        }
        catch { }

        return (0, savageEncountersCount);
    }

    private async Task<(int cleared, int total)> GetUltimateProgressAsync(
        string characterName, string serverSlug, string serverRegion)
    {
        var zonesQuery = @"
        query {
            worldData {
                zones {
                    id
                    name
                    encounters {
                        id
                        name
                    }
                }
            }
        }";
        var zonesResult = await ExecuteGraphQLAsync(zonesQuery);
        var zones = zonesResult.RootElement.GetProperty("data").GetProperty("worldData").GetProperty("zones");

        var ultimateZoneIds = new System.Collections.Generic.List<int>();
        var uniqueUltimateNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var encounterIdToName = new System.Collections.Generic.Dictionary<int, string>();

        foreach (var z in zones.EnumerateArray())
        {
            var name = z.GetProperty("name").GetString() ?? "";

            bool isUltimate = name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ||
                              name.Equals("Futures Rewritten", StringComparison.OrdinalIgnoreCase);

            if (isUltimate)
            {
                var zoneId = z.GetProperty("id").GetInt32();
                ultimateZoneIds.Add(zoneId);

                if (z.TryGetProperty("encounters", out var encs) && encs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var enc in encs.EnumerateArray())
                    {
                        if (enc.TryGetProperty("id", out var idProp) && enc.TryGetProperty("name", out var nameProp))
                        {
                            var encId = idProp.GetInt32();
                            var encName = nameProp.GetString() ?? "Unknown";
                            encounterIdToName[encId] = encName;
                            uniqueUltimateNames.Add(encName);
                        }
                    }
                }
            }
        }

        if (ultimateZoneIds.Count == 0) return (0, 0);
        var clearedUltimates = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var queryBuilder = new StringBuilder("query {\n  characterData {\n    character(name: \"" + EscapeGraphQL(characterName) + "\", serverSlug: \"" + EscapeGraphQL(serverSlug) + "\", serverRegion: \"" + EscapeGraphQL(serverRegion) + "\") {\n");
        for (int i = 0; i < ultimateZoneIds.Count; i++)
        {
            queryBuilder.AppendLine($"      z{i}: zoneRankings(zoneID: {ultimateZoneIds[i]}, difficulty: 100, metric: rdps)");
        }
        queryBuilder.AppendLine("    }\n  }\n}");

        var result = await ExecuteGraphQLAsync(queryBuilder.ToString());

        try
        {
            if (!result.RootElement.TryGetProperty("data", out var dataProp)) return (0, uniqueUltimateNames.Count);
            if (!dataProp.TryGetProperty("characterData", out var characterDataProp)) return (0, uniqueUltimateNames.Count);
            if (!characterDataProp.TryGetProperty("character", out var characterProp) || characterProp.ValueKind == JsonValueKind.Null) return (0, uniqueUltimateNames.Count);

            for (int i = 0; i < ultimateZoneIds.Count; i++)
            {
                if (characterProp.TryGetProperty($"z{i}", out var rankingsData))
                {
                    if (rankingsData.TryGetProperty("rankings", out var rankings)
                        && rankings.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rank in rankings.EnumerateArray())
                        {
                            var kills = rank.TryGetProperty("totalKills", out var k) ? k.GetInt32() : 0;

                            if (rank.TryGetProperty("encounter", out var encounterObj) &&
                                encounterObj.TryGetProperty("id", out var encIdProp))
                            {
                                var encId = encIdProp.GetInt32();
                                if (kills > 0 && encounterIdToName.TryGetValue(encId, out var mappedName))
                                {
                                    clearedUltimates.Add(mappedName);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return (clearedUltimates.Count, uniqueUltimateNames.Count);
    }

    private async Task<JsonDocument> ExecuteGraphQLAsync(string query)
    {
        var payload = JsonSerializer.Serialize(new { query });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.fflogs.com/api/v2/client")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"FFLogs API error: {responseText}");
            throw new Exception($"FFLogs API error: {response.StatusCode}");
        }

        return JsonDocument.Parse(responseText);
    }

    private static string EscapeGraphQL(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
