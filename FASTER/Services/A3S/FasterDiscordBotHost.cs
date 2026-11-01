using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Discord;
using Discord.WebSocket;

using FASTER.Models;
using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// In-process Discord slash-command host for event status/list/deploy.
    /// Shares <see cref="A3SRemoteDeployService"/> with the localhost API (one FASTER process).
    /// </summary>
    public sealed class FasterDiscordBotHost : IDisposable
    {
        public const string EnvEnabled = "DISCORD_BOT_ENABLED";
        public const string EnvToken = "DISCORD_BOT_TOKEN";
        public const string EnvGuildId = "DISCORD_GUILD_ID";
        public const string EnvAllowUserIds = "DISCORD_ALLOW_USER_IDS";
        public const string EnvAllowRoleIds = "DISCORD_ALLOW_ROLE_IDS";
        public const string EnvDefaultRepo = "FASTER_DEFAULT_REPO";
        public const string EnvDefaultProfile = "FASTER_DEFAULT_PROFILE";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private readonly A3SRemoteDeployService _deploy;
        private readonly string _token;
        private readonly ulong? _guildId;
        private readonly HashSet<ulong> _allowUsers;
        private readonly HashSet<ulong> _allowRoles;
        private readonly string? _defaultRepo;
        private readonly string? _defaultProfile;
        private readonly Func<int> _apiPort;
        private readonly Func<bool> _apiListening;

        private DiscordSocketClient? _client;
        private CancellationTokenSource? _cts;
        private int _started;

        public FasterDiscordBotHost(
            A3SRemoteDeployService deploy,
            string token,
            ulong? guildId,
            HashSet<ulong> allowUsers,
            HashSet<ulong> allowRoles,
            string? defaultRepo,
            string? defaultProfile,
            Func<int>? apiPort = null,
            Func<bool>? apiListening = null)
        {
            _deploy = deploy ?? throw new ArgumentNullException(nameof(deploy));
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Discord bot token is required.", nameof(token));
            _token = token.Trim();
            _guildId = guildId;
            _allowUsers = allowUsers ?? new HashSet<ulong>();
            _allowRoles = allowRoles ?? new HashSet<ulong>();
            _defaultRepo = string.IsNullOrWhiteSpace(defaultRepo) ? null : defaultRepo.Trim();
            _defaultProfile = string.IsNullOrWhiteSpace(defaultProfile) ? null : defaultProfile.Trim();
            _apiPort = apiPort ?? (() => FasterLocalApiHost.DefaultPort);
            _apiListening = apiListening ?? (() => false);
        }

        public bool IsStarted => Volatile.Read(ref _started) == 1;

        /// <summary>
        /// Resolve enable/token/guild/allowlist from env (preferred) then user settings.
        /// Returns null when disabled or token missing.
        /// </summary>
        public static FasterDiscordBotHost? TryCreateFromSettings(
            A3SRemoteDeployService deploy,
            Func<int>? apiPort = null,
            Func<bool>? apiListening = null)
        {
            var settings = Settings.Default;

            var enabledEnv = Environment.GetEnvironmentVariable(EnvEnabled);
            var enabled = !string.IsNullOrWhiteSpace(enabledEnv)
                ? enabledEnv is "1" or "true" or "TRUE" or "yes" or "YES"
                : settings.discordBotEnabled;

            if (!enabled)
                return null;

            var token = Environment.GetEnvironmentVariable(EnvToken);
            if (string.IsNullOrWhiteSpace(token))
                token = settings.discordBotToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                Logger.Log("Discord bot enabled but token is empty — set DISCORD_BOT_TOKEN or discordBotToken in Settings.");
                return null;
            }

            var guildRaw = Environment.GetEnvironmentVariable(EnvGuildId);
            if (string.IsNullOrWhiteSpace(guildRaw))
                guildRaw = settings.discordGuildId;
            ulong? guildId = ulong.TryParse(guildRaw?.Trim(), out var g) ? g : null;

            var allowUsers = ParseIdSet(
                Environment.GetEnvironmentVariable(EnvAllowUserIds) ?? settings.discordAllowUserIds);
            var allowRoles = ParseIdSet(
                Environment.GetEnvironmentVariable(EnvAllowRoleIds) ?? settings.discordAllowRoleIds);

            var defaultRepo = Environment.GetEnvironmentVariable(EnvDefaultRepo);
            if (string.IsNullOrWhiteSpace(defaultRepo))
                defaultRepo = settings.discordDefaultRepo;

            var defaultProfile = Environment.GetEnvironmentVariable(EnvDefaultProfile);
            if (string.IsNullOrWhiteSpace(defaultProfile))
                defaultProfile = settings.a3sLocalApiDefaultProfile;

            return new FasterDiscordBotHost(
                deploy,
                token!,
                guildId,
                allowUsers,
                allowRoles,
                defaultRepo,
                defaultProfile,
                apiPort,
                apiListening);
        }

        public async Task StartAsync()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
                return;

            _cts = new CancellationTokenSource();
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds
            };
            _client = new DiscordSocketClient(config);
            _client.Log += msg =>
            {
                Logger.Log($"Discord: {msg}");
                return Task.CompletedTask;
            };
            _client.Ready += OnReadyAsync;
            _client.SlashCommandExecuted += OnSlashCommandAsync;

            try
            {
                await _client.LoginAsync(TokenType.Bot, _token).ConfigureAwait(false);
                await _client.StartAsync().ConfigureAwait(false);
                Logger.Log("Discord bot starting (gateway connect)…");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _started, 0);
                try { _client?.Dispose(); } catch { /* ignore */ }
                _client = null;
                throw new InvalidOperationException($"Discord bot failed to start: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try
            {
                if (_client != null)
                {
                    _client.Ready -= OnReadyAsync;
                    _client.SlashCommandExecuted -= OnSlashCommandAsync;
                    try { _client.StopAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
                    try { _client.LogoutAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
                    _client.Dispose();
                }
            }
            catch { /* ignore */ }
            finally
            {
                _client = null;
                _cts?.Dispose();
                _cts = null;
                Interlocked.Exchange(ref _started, 0);
            }
        }

        private async Task OnReadyAsync()
        {
            if (_client?.CurrentUser == null)
                return;

            Logger.Log($"Discord logged in as {_client.CurrentUser}");
            var cmds = BuildCommands();
            if (_guildId is ulong gid)
            {
                var guild = _client.GetGuild(gid);
                if (guild != null)
                {
                    await guild.BulkOverwriteApplicationCommandAsync(cmds).ConfigureAwait(false);
                    Logger.Log($"Discord: registered {cmds.Length} guild commands on {gid}.");
                    return;
                }

                Logger.Log(
                    $"Discord: guild {gid} not visible yet — invite the bot, then restart FASTER. " +
                    $"Invite: https://discord.com/oauth2/authorize?client_id={_client.CurrentUser.Id}&scope=bot%20applications.commands&permissions=2147483648");
            }

            await _client.BulkOverwriteGlobalApplicationCommandsAsync(cmds).ConfigureAwait(false);
            Logger.Log($"Discord: registered {cmds.Length} global commands (may take up to ~1h to appear).");
        }

        private async Task OnSlashCommandAsync(SocketSlashCommand command)
        {
            try
            {
                if (!IsAllowed(command.User, command.User as SocketGuildUser))
                {
                    await command.RespondAsync("You are not allowed to use FASTER deploy commands.", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }

                switch (command.Data.Name)
                {
                    case "faster-status":
                        await HandleStatusAsync(command).ConfigureAwait(false);
                        break;
                    case "faster-events":
                        await HandleEventsAsync(command).ConfigureAwait(false);
                        break;
                    case "faster-deploy":
                        await HandleDeployAsync(command).ConfigureAwait(false);
                        break;
                    case "faster-start":
                        await HandleStartAsync(command).ConfigureAwait(false);
                        break;
                    case "faster-stop":
                        await HandleStopAsync(command).ConfigureAwait(false);
                        break;
                    default:
                        await command.RespondAsync("Unknown command.", ephemeral: true).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Discord slash error: {ex}");
                try
                {
                    if (command.HasResponded)
                        await command.FollowupAsync($"Error: {ex.Message}", ephemeral: true).ConfigureAwait(false);
                    else
                        await command.RespondAsync($"Error: {ex.Message}", ephemeral: true).ConfigureAwait(false);
                }
                catch { /* ignore secondary Discord failures */ }
            }
        }

        private async Task HandleStatusAsync(SocketSlashCommand command)
        {
            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            var status = _deploy.GetStatus(_apiPort(), _apiListening());
            var body = JsonSerializer.Serialize(status, JsonOptions);
            await command.FollowupAsync($"```json\n{Trim(body, 1800)}\n```", ephemeral: true).ConfigureAwait(false);
        }

        private async Task HandleEventsAsync(SocketSlashCommand command)
        {
            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            var repo = GetOpt(command, "repo");
            var url = GetOpt(command, "url");
            if (string.IsNullOrWhiteSpace(repo) && string.IsNullOrWhiteSpace(url))
            {
                await command.FollowupAsync("Pass `repo` or `url`.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            try
            {
                var result = await _deploy.ListEventsAsync(repo, url).ConfigureAwait(false);
                var body = JsonSerializer.Serialize(result, JsonOptions);
                await command.FollowupAsync($"```json\n{Trim(body, 1800)}\n```", ephemeral: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await command.FollowupAsync($"Events failed: {ex.Message}", ephemeral: true).ConfigureAwait(false);
            }
        }

        private async Task HandleDeployAsync(SocketSlashCommand command)
        {
            await command.DeferAsync().ConfigureAwait(false);
            var evt = GetOpt(command, "event");
            var repo = GetOpt(command, "repo") ?? _defaultRepo;
            var url = GetOpt(command, "url");
            var profile = GetOpt(command, "profile") ?? _defaultProfile;

            if (string.IsNullOrWhiteSpace(evt))
            {
                await command.FollowupAsync("`event` is required.").ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(repo) && string.IsNullOrWhiteSpace(url))
            {
                await command.FollowupAsync(
                        "Pass `repo` (Mode A) or `url` (Mode B), or set default repo in Settings / FASTER_DEFAULT_REPO.")
                    .ConfigureAwait(false);
                return;
            }

            await command.FollowupAsync($"Starting deploy: event **{evt}** … (sync can take a while)")
                .ConfigureAwait(false);

            try
            {
                var result = await _deploy.DeployAsync(new A3SRemoteDeployRequest
                {
                    Event = evt,
                    Repo = string.IsNullOrWhiteSpace(url) ? repo : null,
                    Url = url,
                    Profile = profile,
                    Launch = true
                }).ConfigureAwait(false);

                var body = JsonSerializer.Serialize(result, JsonOptions);
                await command.FollowupAsync($"Deploy OK:\n```json\n{Trim(body, 1700)}\n```").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await command.FollowupAsync($"Deploy failed: {ex.Message}").ConfigureAwait(false);
            }
        }

        private async Task HandleStartAsync(SocketSlashCommand command)
        {
            await command.DeferAsync().ConfigureAwait(false);
            var profile = GetOpt(command, "profile") ?? _defaultProfile;
            try
            {
                var result = await _deploy.StartServerAsync(profile).ConfigureAwait(false);
                var body = JsonSerializer.Serialize(result, JsonOptions);
                await command.FollowupAsync($"{result.Message}\n```json\n{Trim(body, 1500)}\n```")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await command.FollowupAsync($"Start failed: {ex.Message}").ConfigureAwait(false);
            }
        }

        private async Task HandleStopAsync(SocketSlashCommand command)
        {
            await command.DeferAsync().ConfigureAwait(false);
            var profile = GetOpt(command, "profile") ?? _defaultProfile;
            try
            {
                var result = _deploy.StopServer(profile);
                var body = JsonSerializer.Serialize(result, JsonOptions);
                await command.FollowupAsync($"{result.Message}\n```json\n{Trim(body, 1500)}\n```")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await command.FollowupAsync($"Stop failed: {ex.Message}").ConfigureAwait(false);
            }
        }

        private bool IsAllowed(IUser user, SocketGuildUser? guildUser)
        {
            if (_allowUsers.Count == 0 && _allowRoles.Count == 0)
                return true;
            if (_allowUsers.Contains(user.Id))
                return true;
            if (guildUser == null)
                return false;
            return guildUser.Roles.Any(r => _allowRoles.Contains(r.Id));
        }

        public static ApplicationCommandProperties[] BuildCommands()
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

            var start = new SlashCommandBuilder()
                .WithName("faster-start")
                .WithDescription("Start the Arma dedicated server for a FASTER profile")
                .AddOption("profile", ApplicationCommandOptionType.String, "FASTER server profile name", isRequired: false);

            var stop = new SlashCommandBuilder()
                .WithName("faster-stop")
                .WithDescription("Stop the Arma dedicated server (and HC) for a FASTER profile")
                .AddOption("profile", ApplicationCommandOptionType.String, "FASTER server profile name", isRequired: false);

            return new ApplicationCommandProperties[]
            {
                status.Build(),
                events.Build(),
                deploy.Build(),
                start.Build(),
                stop.Build()
            };
        }

        public static HashSet<ulong> ParseIdSet(string? raw)
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

        private static string? GetOpt(SocketSlashCommand command, string name)
            => command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value?.ToString();

        private static string Trim(string s, int max = 1500)
        {
            s = s.Trim();
            return s.Length <= max ? s : s[..max] + "…";
        }
    }
}
