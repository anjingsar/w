using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.Interactions;
using NetStone;
using NetStone.Model.Parseables.Character;
using NetStone.Model.Parseables.Character.ClassJob;
using NetStone.Search.Character;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;

namespace XivWidget.Modules;

[Group("widget", "Widget related commands")]
[IntegrationType(ApplicationIntegrationType.UserInstall)]
[CommandContextType(InteractionContextType.BotDm, InteractionContextType.PrivateChannel, InteractionContextType.Guild)]
public class WidgetModule : InteractionModuleBase<SocketInteractionContext>
{
    private LodestoneClient client;
    private DatabaseService _db;
    private FFLogsService _fflogs;

    public WidgetModule(LodestoneClient client, DatabaseService db, FFLogsService fflogs)
    {
        this.client = client;
        _db = db;
        _fflogs = fflogs;
    }

    [SlashCommand("setup", "Setup the widget")]
    public async Task SetupAsync(
        [Summary(description: "First name of the character")] string firstname,
        [Summary(description: "Last name of the character")] string lastname,
        [Summary(description: "World of the character"), Autocomplete(typeof(WorldAutocompleteHandler))] string world)
    {
        string normalizedCharacterString = string.Join(" ", $"{firstname} {lastname}".Split(' ')
            .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w.Substring(1).ToLower() : w));

        var searchResponse = await client.SearchCharacter(new CharacterSearchQuery()
        {
            CharacterName = normalizedCharacterString,
            World = world
        });

        var character = searchResponse?.Results.FirstOrDefault(entry => entry.Name == $"{normalizedCharacterString}");
        string? lodestoneId = character?.Id;

        if (string.IsNullOrEmpty(lodestoneId))
        {
            await RespondAsync($"Could not find character {normalizedCharacterString} on {world}", ephemeral: true);
            return;
        }

        var authorizeButton = new ButtonBuilder()
        {
            Style = ButtonStyle.Link,
            Label = "Authorize",
            Url = $"https://discord.com/oauth2/authorize?client_id=1520246314699456612&response_type=token&scope=openid+sdk.social_layer"
        };

        var lodestoneButton = new ButtonBuilder()
        {
            Style = ButtonStyle.Link,
            Label = "Lodestone",
            Url = $"https://na.finalfantasyxiv.com/lodestone/character/{lodestoneId}/"
        };

        var verifyButton = new ButtonBuilder()
        {
            Style = ButtonStyle.Primary,
            Label = "Verify",
            CustomId = $"verify:{lodestoneId}"
        };

        var row = new ActionRowBuilder().AddComponents(authorizeButton, lodestoneButton, verifyButton);
        var components = new ComponentBuilder().AddRow(row).Build();
        string verificationString = verificationToken(Context.User.Id);

        await RespondAsync($"""
        To continue, please authorize the application using the button below and close the Discord website that opens.
        Then paste the following string into your lodestone character profile: `{verificationString}`.
        Once you have done both, click the "Verify" button to continue.

        -# Note that the authorization modal will display a ton of permissions it needs access to,
        -# this is because of the scope needed for widgets to work.
        -# xivwidget does not store the token and will never have access to act on any of these permissions.
        """, ephemeral: true, components: components);
    }

    [ComponentInteraction("verify:*", ignoreGroupNames: true)]
    public async Task VerifyButtonHandler(string lodestoneId)
    {
        await DeferAsync(ephemeral: true);

        string expectedToken = verificationToken(Context.User.Id);
        var character = await client.GetCharacter(lodestoneId);

        if (character != null && character.Bio.Contains(expectedToken))
        {
            _db.AddOrUpdateUser(Context.User.Id, lodestoneId);
            try
            {
                await SyncUserDiscordWidget(Context.User.Id, lodestoneId, character);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error syncing widget: {ex}");
            }

            await FollowupAsync("""
            Verification Successful! You can now add the widget to your profile:
            1\. Open Discord in your browser.
            2\. Open your browsers developer tools (CTRL + Shift + I).
            3\. Open the Console tab.
            4\. Type `allow pasting` into the console. This will allow you to copy the following code without having to manually type it out.
            5\. Paste in the following code, this will add the widget to your profile:
            ```js
            (async ()=>{let _mods=webpackChunkdiscord_app.push([[Symbol()],{},e=>e.c]);webpackChunkdiscord_app.pop(); let findByProps=(...e)=>{for(let t of Object.values(_mods))try{if(!t.exports||t.exports===window)continue;if(e.every(e=>t.exports?.[e]))return t.exports;for(let r in t.exports)if(e.every(e=>t.exports?.[r]?.[e])&&"IntlMessagesProxy"!==t.exports[r][Symbol.toStringTag])return t.exports[r]}catch{}}; let api = Object.values(_mods).find(x => x?.exports?.Bo?.get).exports.Bo; let id = findByProps("getCurrentUser").getCurrentUser().id; let current_widgets = (await api.get("/users/" + id + "/profile")).body.widgets; if (current_widgets.map(x=>x.data?.application_id).includes("1509844130082062396")) {return console.log("Already in your widgets — remove it via Discord client to re-add");} current_widgets.unshift({"data":{"type":"application","application_id":"1509844130082062396"}}); await api.put({url:"/users/@me/widgets",body:{widgets:current_widgets}});})()
            ```
            6\. If there is no error, reload your Discord client using CTRL + R.

            You can also customize your widget using /widget setjob and /widget setraid.
            """, ephemeral: true);
            return;
        }

        await FollowupAsync("Could not verify character. Make sure you have the exact verification string in your characters bio or try again in a couple of minutes.", ephemeral: true);
    }

    [SlashCommand("refresh", "Refresh your widget data")]
    public async Task RefreshWidgetAsync()
    {
        var lodestoneId = _db.GetLodestoneId(Context.User.Id);
        if (string.IsNullOrEmpty(lodestoneId))
        {
            await RespondAsync("You haven't setup your widget yet. Please use `/widget setup` first.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        try
        {
            var character = await client.GetCharacter(lodestoneId);
            if (character == null)
            {
                await FollowupAsync("Could not fetch character data from Lodestone.", ephemeral: true);
                return;
            }

            await SyncUserDiscordWidget(Context.User.Id, lodestoneId, character);
            await FollowupAsync("Widget refreshed successfully!", ephemeral: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error refreshing widget: {ex}");
            await FollowupAsync("An error occurred while refreshing your widget.", ephemeral: true);
        }
    }

    [SlashCommand("setjob", "Set your main job for the widget")]
    public async Task SetJobAsync(
        [Summary(description: "Your main job"), Autocomplete(typeof(JobAutocompleteHandler))] string job)
    {
        var lodestoneId = _db.GetLodestoneId(Context.User.Id);
        if (string.IsNullOrEmpty(lodestoneId))
        {
            await RespondAsync("You haven't setup your widget yet. Please use `/widget setup` first.", ephemeral: true);
            return;
        }

        if (!JobAutocompleteHandler.Jobs.Contains(job, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync($"Unknown job `{job}`. Please select a valid job from the autocomplete list.", ephemeral: true);
            return;
        }

        _db.SetMainJob(Context.User.Id, job);
        await RespondAsync($"Main job set to **{job}**! Use `/widget refresh` to update your widget.", ephemeral: true);
    }

    [SlashCommand("setraids", "Choose whether to display Savage or Ultimate raid progress")]
    public async Task SetRaidsAsync(
        [Summary(description: "Raid type to display"), Choice("Savage", "Savage"), Choice("Ultimate", "Ultimate")] string raidType)
    {
        var lodestoneId = _db.GetLodestoneId(Context.User.Id);
        if (string.IsNullOrEmpty(lodestoneId))
        {
            await RespondAsync("You haven't setup your widget yet. Please use `/widget setup` first.", ephemeral: true);
            return;
        }

        _db.SetRaidType(Context.User.Id, raidType);
        await RespondAsync($"Raid display set to **{raidType}**! Use `/widget refresh` to update your widget.", ephemeral: true);
    }

    private async Task SyncUserDiscordWidget(ulong discordId, string lodestoneId, LodestoneCharacter character)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrEmpty(token)) return;

        var minionCount = 0;
        var mountCount = 0;
        var achievementPoints = 0;

        try
        {
            var minions = await client.GetCharacterMinion(lodestoneId);
            minionCount = minions?.Collectables?.Count() ?? 0;
        }
        catch { }

        try
        {
            var mounts = await client.GetCharacterMount(lodestoneId);
            mountCount = mounts?.Collectables?.Count() ?? 0;
        }
        catch { }

        try
        {
            var achievements = await client.GetCharacterAchievement(lodestoneId);
            achievementPoints = achievements?.AchievementPoints ?? 0;
        }
        catch { }

        var job = "";
        var jobLevel = "";
        var mainJob = _db.GetMainJob(discordId);

        try
        {
            var classJobInfo = await character.GetClassJobInfo();
            if (classJobInfo != null && !string.IsNullOrEmpty(mainJob))
            {
                var entry = GetClassJobEntry(classJobInfo, mainJob);
                if (entry != null)
                {
                    job = entry.IsJobUnlocked
                        ? entry.Name.Split('/')[0].Trim()
                        : entry.Name.Trim();
                    jobLevel = entry.Level.ToString();
                }
            }
        }
        catch { }

        var raidType = _db.GetRaidType(discordId);
        var raidCleared = 0;
        var raidTotal = 0;
        var raidLabel = raidType;

        try
        {
            var charName = character.Name;
            var serverSlug = character.Server?.ToLowerInvariant() ?? "";
            var serverRegion = GetServerRegion(serverSlug);

            var (cleared, total, label) = await _fflogs.GetRaidProgressAsync(charName, serverSlug, serverRegion, raidType);
            raidCleared = cleared;
            raidTotal = total;
            raidLabel = label;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching FFLogs data: {ex.Message}");
        }

        var dynamicData = new List<object>
        {
            new { type = 1, name = "full_name", value = character.Name },
            new { type = 1, name = "character_title", value = character.Title },
            new { type = 1, name = "world", value = $"@{character.Server}" },
            new { type = 1, name = "ach_points", value = $"{achievementPoints} Points" },
            new { type = 2, name = "minions", value = minionCount },
            new { type = 2, name = "mounts", value = mountCount },
            new { type = 1, name = "fc", value = character.FreeCompany?.Name ?? "None" },
            new { type = 1, name = "job", value = !string.IsNullOrEmpty(job) ? $"{job} Lv.{jobLevel}" : "Not set" },
            new { type = 1, name = "raids", value = $"{raidCleared}/{raidTotal}" },
            new { type = 1, name = "raid_label", value = raidLabel }
        };

        if (character.Portrait != null)
        {
            dynamicData.Add(new { type = 3, name = "character_icon", value = new { url = character.Portrait } });
        }

        var payload = new
        {
            username = character.Name,
            data = new
            {
                dynamic = dynamicData
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var clientId = "1509844130082062396";
        var url = $"https://discord.com/api/v9/applications/{clientId}/users/{discordId}/identities/0/profile";

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);

        var response = await httpClient.PatchAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var resText = await response.Content.ReadAsStringAsync();
            throw new Exception($"Discord API error: {resText}");
        }
    }

    private static ClassJobEntry? GetClassJobEntry(CharacterClassJob classJobInfo, string jobName)
    {
        return jobName.ToLowerInvariant() switch
        {
            "paladin" => classJobInfo.Paladin,
            "warrior" => classJobInfo.Warrior,
            "dark knight" => classJobInfo.DarkKnight,
            "gunbreaker" => classJobInfo.Gunbreaker,
            "white mage" => classJobInfo.WhiteMage,
            "scholar" => classJobInfo.Scholar,
            "astrologian" => classJobInfo.Astrologian,
            "sage" => classJobInfo.Sage,
            "monk" => classJobInfo.Monk,
            "dragoon" => classJobInfo.Dragoon,
            "ninja" => classJobInfo.Ninja,
            "samurai" => classJobInfo.Samurai,
            "reaper" => classJobInfo.Reaper,
            "viper" => classJobInfo.Viper,
            "bard" => classJobInfo.Bard,
            "machinist" => classJobInfo.Machinist,
            "dancer" => classJobInfo.Dancer,
            "black mage" => classJobInfo.BlackMage,
            "summoner" => classJobInfo.Summoner,
            "red mage" => classJobInfo.RedMage,
            "pictomancer" => classJobInfo.Pictomancer,
            "blue mage" => classJobInfo.BlueMage,
            _ => null
        };
    }

    private static string GetServerRegion(string serverSlug)
    {
        var naServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "adamantoise", "cactuar", "faerie", "gilgamesh", "jenova", "midgardsormr", "sargatanas", "siren",
            "balmung", "brynhildr", "coeurl", "diabolos", "goblin", "malboro", "mateus", "zalera",
            "cuchulainn", "golem", "halicarnassus", "kraken", "maduin", "marilith", "rafflesia", "seraph",
            "behemoth", "excalibur", "exodus", "famfrit", "hyperion", "lamia", "leviathan", "ultros"
        };

        var euServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cerberus", "louisoix", "moogle", "omega", "phantom", "ragnarok", "sagittarius", "spriggan",
            "alpha", "lich", "odin", "phoenix", "raiden", "shiva", "twintania", "zodiark"
        };

        var jpServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aegis", "atomos", "carbuncle", "garuda", "gungnir", "kujata", "tonberry", "typhon",
            "alexander", "bahamut", "durandal", "fenrir", "ifrit", "ridill", "tiamat", "ultima",
            "anima", "asura", "chocobo", "hades", "ixion", "masamune", "pandaemonium", "titan",
            "belias", "mandragora", "ramuh", "shinryu", "unicorn", "valefor", "yojimbo", "zeromus"
        };

        var ocServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bismarck", "ravana", "sephirot", "sophia", "zurvan"
        };

        if (naServers.Contains(serverSlug)) return "NA";
        if (euServers.Contains(serverSlug)) return "EU";
        if (jpServers.Contains(serverSlug)) return "JP";
        if (ocServers.Contains(serverSlug)) return "OC";

        return "NA";
    }

    private string verificationToken(ulong id)
    {
        string secret = Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "fallback_secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(id.ToString()));

        return $"xivwidget-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

public class WorldAutocompleteHandler : AutocompleteHandler
{
    private static readonly string[] Worlds = new[]
    {
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
        "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph",
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",

        "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan",
        "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark",

        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",

        "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon",
        "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima",
        "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan",
        "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus"
    };

    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var userInput = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";

        var suggestions = Worlds
            .Where(w => w.Contains(userInput, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(w => new AutocompleteResult(w, w));

        return AutocompletionResult.FromSuccess(suggestions);
    }
}

public class JobAutocompleteHandler : AutocompleteHandler
{
    public static readonly string[] Jobs = new[]
    {
        "Paladin", "Warrior", "Dark Knight", "Gunbreaker",
        "White Mage", "Scholar", "Astrologian", "Sage",
        "Monk", "Dragoon", "Ninja", "Samurai", "Reaper", "Viper",
        "Bard", "Machinist", "Dancer",
        "Black Mage", "Summoner", "Red Mage", "Pictomancer",
        "Blue Mage"
    };

    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var userInput = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";

        var suggestions = Jobs
            .Where(j => j.Contains(userInput, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(j => new AutocompleteResult(j, j));

        return AutocompletionResult.FromSuccess(suggestions);
    }
}
