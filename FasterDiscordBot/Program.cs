using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Discord;
using Discord.WebSocket;

namespace FasterDiscordBot;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task Main(string[] args)
    {
        var discordToken = Env("DISCORD_BOT_TOKEN")
            ?? throw new InvalidOperationException("Set DISCORD_BOT_TOKEN.");
        var apiToken = Env("FASTER_LOCAL_API_TOKEN")
            ?? throw new InvalidOperationException("Set FASTER_LOCAL_API_TOKEN (same as FASTER local API).");
        var apiBase = (Env("FASTER_LOCAL_API_BASE") ?? "http://127.0.0.1:8787").TrimEnd('/');
        var guildIdRaw = Env("DISCORD_GUILD_ID");
        ulong? guildId = ulong.TryParse(guildIdRaw, out var g) ? g : null;
        var allowUsers = ParseIdSet(Env("DISCORD_ALLOW_USER_IDS"));
        var allowRoles = ParseIdSet(Env("DISCORD_ALLOW_ROLE_IDS"));
        var defaultRepo = Env("FASTER_DEFAULT_REPO");
        var defaultProfile = Env("FASTER_DEFAULT_PROFILE");

        using var http = new HttpClient { BaseAddress = new Uri(apiBase + "/"), Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
        };
        using var client = new DiscordSocketClient(config);
        client.Log += msg =>
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        };

        client.Ready += async () =>
        {
            Console.WriteLine($"Logged in as {client.CurrentUser}");
            var cmds = BuildCommands();
            if (guildId is ulong gid)
            {
                var guild = client.GetGuild(gid);
                if (guild != null)
                {
                    await guild.BulkOverwriteApplicationCommandAsync(cmds).ConfigureAwait(false);
                    Console.WriteLine($"Registered {cmds.Length} guild commands on {gid}.");
                    return;
                }

                Console.WriteLine(
                    $"Guild {gid} not visible yet — invite the bot, then restart. " +
                    $"Invite: https://discord.com/oauth2/authorize?client_id={client.CurrentUser.Id}&scope=bot%20applications.commands&permissions=2147483648");
            }

            await client.BulkOverwriteGlobalApplicationCommandsAsync(cmds).ConfigureAwait(false);
            Console.WriteLine($"Registered {cmds.Length} global commands (may take up to ~1h to appear).");
        };

        client.SlashCommandExecuted += async command =>
        {
            try
            {
                if (!IsAllowed(command.User, command.User as SocketGuildUser, allowUsers, allowRoles))
                {
                    await command.RespondAsync("You are not allowed to use FASTER deploy commands.", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }

                switch (command.Data.Name)
                {
                    case "faster-status":
                        await HandleStatusAsync(command, http).ConfigureAwait(false);
                        break;
                    case "faster-events":
                        await HandleEventsAsync(command, http).ConfigureAwait(false);
                        break;
                    case "faster-deploy":
                        await HandleDeployAsync(command, http, defaultRepo, defaultProfile).ConfigureAwait(false);
                        break;
                    default:
                        await command.RespondAsync("Unknown command.", ephemeral: true).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                if (command.HasResponded)
                    await command.FollowupAsync($"Error: {ex.Message}", ephemeral: true).ConfigureAwait(false);
                else
                    await command.RespondAsync($"Error: {ex.Message}", ephemeral: true).ConfigureAwait(false);
            }
        };

        await client.LoginAsync(TokenType.Bot, discordToken).ConfigureAwait(false);
        await client.StartAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
    }

    private static ApplicationCommandProperties[] BuildCommands()
    {
        var status = new SlashCommandBuilder()
            .WithName("faster-status")
            .WithDescription("FASTER local API status (profiles, last deploy)");

        var events = new SlashCommandBuilder()
            .WithName("faster-events")
            .WithDescription("List A3S events/modsets for a repo or URL")
            .AddOption("repo", ApplicationCommandOptionType.String, "Repository name", isRequired: false)
            .AddOption("url", ApplicationCommandOptionType.String, "Autoconfig or sync URL", isRequired: false);

        var deploy = new SlashCommandBuilder()
            .WithName("faster-deploy")
            .WithDescription("Sync → apply event → deploy → auto-launch Arma server")
            .AddOption("event", ApplicationCommandOptionType.String, "A3S event / modset name", isRequired: true)
            .AddOption("repo", ApplicationCommandOptionType.String, "Known repository name (Mode A)", isRequired: false)
            .AddOption("url", ApplicationCommandOptionType.String, "Autoconfig/sync URL (Mode B)", isRequired: false)
            .AddOption("profile", ApplicationCommandOptionType.String, "FASTER server profile name", isRequired: false);

        return new ApplicationCommandProperties[]
        {
            status.Build(),
            events.Build(),
            deploy.Build()
        };
    }

    private static async Task HandleStatusAsync(SocketSlashCommand command, HttpClient http)
    {
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        using var res = await http.GetAsync("v1/status").ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            await command.FollowupAsync($"API {(int)res.StatusCode}: {Trim(body)}", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await command.FollowupAsync($"```json\n{Trim(body, 1800)}\n```", ephemeral: true).ConfigureAwait(false);
    }

    private static async Task HandleEventsAsync(SocketSlashCommand command, HttpClient http)
    {
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var repo = GetOpt(command, "repo");
        var url = GetOpt(command, "url");
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(repo)) qs.Add("repo=" + Uri.EscapeDataString(repo));
        if (!string.IsNullOrWhiteSpace(url)) qs.Add("url=" + Uri.EscapeDataString(url));
        if (qs.Count == 0)
        {
            await command.FollowupAsync("Pass `repo` or `url`.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        using var res = await http.GetAsync("v1/events?" + string.Join("&", qs)).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            await command.FollowupAsync($"API {(int)res.StatusCode}: {Trim(body)}", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await command.FollowupAsync($"```json\n{Trim(body, 1800)}\n```", ephemeral: true).ConfigureAwait(false);
    }

    private static async Task HandleDeployAsync(
        SocketSlashCommand command,
        HttpClient http,
        string? defaultRepo,
        string? defaultProfile)
    {
        await command.DeferAsync().ConfigureAwait(false);
        var evt = GetOpt(command, "event");
        var repo = GetOpt(command, "repo") ?? defaultRepo;
        var url = GetOpt(command, "url");
        var profile = GetOpt(command, "profile") ?? defaultProfile;

        if (string.IsNullOrWhiteSpace(evt))
        {
            await command.FollowupAsync("`event` is required.").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(repo) && string.IsNullOrWhiteSpace(url))
        {
            await command.FollowupAsync("Pass `repo` (Mode A) or `url` (Mode B), or set FASTER_DEFAULT_REPO.")
                .ConfigureAwait(false);
            return;
        }

        var payload = new Dictionary<string, object?> { ["event"] = evt };
        if (!string.IsNullOrWhiteSpace(url))
            payload["url"] = url;
        else
            payload["repo"] = repo;
        if (!string.IsNullOrWhiteSpace(profile))
            payload["profile"] = profile;
        payload["launch"] = true;

        var json = JsonSerializer.Serialize(payload);
        await command.FollowupAsync(
                $"Starting deploy: event **{evt}** … (sync can take a while)")
            .ConfigureAwait(false);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await http.PostAsync("v1/deploy", content).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            await command.FollowupAsync($"Deploy failed ({(int)res.StatusCode}): {Trim(body)}")
                .ConfigureAwait(false);
            return;
        }

        await command.FollowupAsync($"Deploy OK:\n```json\n{Trim(body, 1700)}\n```").ConfigureAwait(false);
    }

    private static bool IsAllowed(
        IUser user,
        SocketGuildUser? guildUser,
        HashSet<ulong> allowUsers,
        HashSet<ulong> allowRoles)
    {
        if (allowUsers.Count == 0 && allowRoles.Count == 0)
            return true; // open until allowlists configured — tighten in production
        if (allowUsers.Contains(user.Id))
            return true;
        if (guildUser == null)
            return false;
        return guildUser.Roles.Any(r => allowRoles.Contains(r.Id));
    }

    private static string? GetOpt(SocketSlashCommand command, string name)
        => command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value?.ToString();

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static HashSet<ulong> ParseIdSet(string? raw)
    {
        var set = new HashSet<ulong>();
        if (string.IsNullOrWhiteSpace(raw))
            return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(part, out var id))
                set.Add(id);
        }

        return set;
    }

    private static string Trim(string s, int max = 1500)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
