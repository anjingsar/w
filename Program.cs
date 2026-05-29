using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using NetStone;

namespace XivWidget;

public class Program
{
    private DiscordSocketClient? _client;
    private InteractionService? _interactionService;
    private IServiceProvider? _services;

    public static Task Main(string[] args) => new Program().MainAsync();

    public async Task MainAsync()
    {
        _client = new DiscordSocketClient();
        _interactionService = new InteractionService(_client);

        var lodestoneClient = await LodestoneClient.GetClientAsync();

        var fflogsClientId = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_ID") ?? "";
        var fflogsClientSecret = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_SECRET") ?? "";

        _services = new ServiceCollection()
            .AddSingleton(_client)
            .AddSingleton(_interactionService)
            .AddSingleton(lodestoneClient)
            .AddSingleton(new DatabaseService())
            .AddSingleton(new FFLogsService(fflogsClientId, fflogsClientSecret))
            .BuildServiceProvider();

        _client.Log += Log;
        _interactionService.Log += Log;

        _client.Ready += ReadyAsync;
        _client.InteractionCreated += InteractionCreatedAsync;

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("DISCORD_TOKEN environment variable is not set");
            return;
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(-1);
    }

    private Task Log(LogMessage message)
    {
        Console.WriteLine(message.ToString());
        return Task.CompletedTask;
    }

    private async Task ReadyAsync()
    {
        if (_client == null || _interactionService == null || _services == null) return;

        await _interactionService.AddModulesAsync(assembly: typeof(Program).Assembly, services: _services);
        await _interactionService.RegisterCommandsGloballyAsync();

        Console.WriteLine("Registered commands globally.");
    }

    private async Task InteractionCreatedAsync(SocketInteraction interaction)
    {
        if (_client == null || _interactionService == null || _services == null) return;

        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, services: _services);

            if (!result.IsSuccess)
            {
                Console.WriteLine($"Error executing command: {result.ErrorReason}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception handling interaction: {ex}");
            if (interaction.Type == InteractionType.ApplicationCommand)
            {
                await interaction.GetOriginalResponseAsync().ContinueWith(async (msg) => await msg.Result.DeleteAsync());
            }
        }
    }
}
