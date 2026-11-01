using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Orchestrates Discord / localhost API deploys: resolve repo (Mode A/B) + profile,
    /// then <see cref="A3SEventService.SyncEventDeployLaunchAsync"/> with launch always on.
    /// </summary>
    public class A3SRemoteDeployService
    {
        private readonly A3SEventService _eventService;
        private readonly A3SRepositoryService _repositoryService;
        private readonly Settings _settings;
        private readonly Func<ServerProfile, Task>? _launchAsync;
        private readonly object _gate = new();
        private bool _busy;
        private A3SRemoteDeployResult? _lastSuccess;
        private string? _lastError;

        public A3SRemoteDeployService(
            A3SEventService? eventService = null,
            A3SRepositoryService? repositoryService = null,
            Func<ServerProfile, Task>? launchAsync = null)
            : this(eventService, repositoryService, Settings.Default, launchAsync)
        {
        }

        internal A3SRemoteDeployService(
            A3SEventService? eventService,
            A3SRepositoryService? repositoryService,
            Settings settings,
            Func<ServerProfile, Task>? launchAsync = null)
        {
            _eventService = eventService ?? new A3SEventService();
            _repositoryService = repositoryService ?? new A3SRepositoryService();
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _launchAsync = launchAsync;
        }

        public bool Busy
        {
            get { lock (_gate) return _busy; }
        }

        public A3SRemoteDeployResult? LastSuccess
        {
            get { lock (_gate) return _lastSuccess; }
        }

        public string? LastError
        {
            get { lock (_gate) return _lastError; }
        }

        public A3SRemoteStatusResult GetStatus(int port, bool apiListening)
        {
            var profiles = (_settings.Profiles ?? new List<ServerProfile>())
                .Select(p => p.Name ?? string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var repos = _repositoryService.List()
                .Select(r => r.Name ?? string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            lock (_gate)
            {
                return new A3SRemoteStatusResult
                {
                    ApiListening = apiListening,
                    Bind = "127.0.0.1",
                    Port = port,
                    DefaultProfile = string.IsNullOrWhiteSpace(_settings.a3sLocalApiDefaultProfile)
                        ? null
                        : _settings.a3sLocalApiDefaultProfile.Trim(),
                    LastJobId = _lastSuccess?.JobId,
                    LastEvent = _lastSuccess?.EventName,
                    LastProfile = _lastSuccess?.ProfileName,
                    LastLaunched = _lastSuccess?.Launched,
                    LastError = _lastError,
                    Busy = _busy,
                    Profiles = profiles,
                    Repositories = repos
                };
            }
        }

        public async Task<A3SRemoteEventsResult> ListEventsAsync(
            string? repo,
            string? autoconfigOrSyncUrl,
            CancellationToken cancellationToken = default)
        {
            var repository = ResolveRepository(repo, autoconfigOrSyncUrl, importIfMissing: !string.IsNullOrWhiteSpace(autoconfigOrSyncUrl));
            var events = await _eventService.ListEventsAsync(repository, cancellationToken).ConfigureAwait(false);
            return new A3SRemoteEventsResult
            {
                RepositoryName = repository.Name,
                AutoconfigUrl = repository.AutoconfigUrl,
                Events = events.Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
            };
        }

        public async Task<A3SRemoteDeployResult> DeployAsync(
            A3SRemoteDeployRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);

            lock (_gate)
            {
                if (_busy)
                    throw new InvalidOperationException("A deploy job is already running.");
                _busy = true;
                _lastError = null;
            }

            var jobId = Guid.NewGuid().ToString("N")[..12];
            try
            {
                var repository = ResolveRepository(
                    request.Repo,
                    request.ResolvedAutoconfigUrl,
                    importIfMissing: !string.IsNullOrWhiteSpace(request.ResolvedAutoconfigUrl));
                var profile = ResolveProfile(request.Profile);

                // Product path: always auto-launch (locked decision).
                Func<ServerProfile, Task> launch = _launchAsync ?? (p =>
                {
                    TryLaunchProfileProcess(p);
                    return Task.CompletedTask;
                });

                var result = await _eventService.SyncEventDeployLaunchAsync(
                        repository,
                        profile,
                        request.Event!.Trim(),
                        applyOptions: new A3SEventApplyOptions
                        {
                            ApplyClient = true,
                            ApplyServer = false,
                            ApplyHeadless = true
                        },
                        launchAsync: launch,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var payload = new A3SRemoteDeployResult
                {
                    JobId = jobId,
                    EventName = result.Apply.EventName,
                    ProfileName = profile.Name,
                    RepositoryName = repository.Name,
                    RepositoryId = repository.Id,
                    EventAddonCount = result.Apply.EventAddonCount,
                    MatchedMods = result.Apply.MatchedMods.Count,
                    MissingAddons = result.Apply.MissingAddons.Count,
                    MissingAddonKeys = result.Apply.MissingAddons.ToList(),
                    Launched = result.Launched,
                    Message =
                        $"Deployed \"{result.Apply.EventName}\" → profile \"{profile.Name}\": " +
                        $"event addons {result.Apply.EventAddonCount}, matched {result.Apply.MatchedMods.Count}, " +
                        $"missing {result.Apply.MissingAddons.Count}, launched={result.Launched}."
                };

                lock (_gate) _lastSuccess = payload;
                return payload;
            }
            catch (Exception ex)
            {
                lock (_gate) _lastError = ex.Message;
                throw;
            }
            finally
            {
                lock (_gate) _busy = false;
            }
        }

        public static void ValidateRequest(A3SRemoteDeployRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.Event))
                throw new ArgumentException("Field \"event\" (modset / A3S event name) is required.");

            var hasRepo = !string.IsNullOrWhiteSpace(request.Repo);
            var hasUrl = !string.IsNullOrWhiteSpace(request.ResolvedAutoconfigUrl);
            if (!hasRepo && !hasUrl)
            {
                throw new ArgumentException(
                    "Provide \"repo\" (Mode A) or \"autoconfigUrl\" / \"url\" (Mode B).");
            }
        }

        public A3SRepository ResolveRepository(string? repo, string? autoconfigOrSyncUrl, bool importIfMissing)
        {
            if (!string.IsNullOrWhiteSpace(autoconfigOrSyncUrl))
            {
                if (importIfMissing)
                    return _repositoryService.FindOrImportByAutoconfigUrl(autoconfigOrSyncUrl);
                var normalized = A3SUrlHelpers.NormalizeAutoconfigUrl(autoconfigOrSyncUrl);
                var byUrl = _repositoryService.List().FirstOrDefault(r =>
                    !string.IsNullOrWhiteSpace(r.AutoconfigUrl)
                    && string.Equals(
                        A3SUrlHelpers.NormalizeAutoconfigUrl(r.AutoconfigUrl),
                        normalized,
                        StringComparison.OrdinalIgnoreCase));
                if (byUrl == null)
                    throw new InvalidOperationException($"No repository matches URL \"{autoconfigOrSyncUrl}\".");
                return byUrl;
            }

            if (string.IsNullOrWhiteSpace(repo))
                throw new ArgumentException("Repository name/id is required when no URL is provided.");

            var key = repo.Trim();
            var found = _repositoryService.List().FirstOrDefault(r =>
                string.Equals(r.Id, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                throw new InvalidOperationException($"Repository \"{key}\" was not found.");
            return found;
        }

        public ServerProfile ResolveProfile(string? profileName)
        {
            var profiles = _settings.Profiles ?? new List<ServerProfile>();
            if (profiles.Count == 0)
                throw new InvalidOperationException("No server profiles configured in FASTER.");

            var name = !string.IsNullOrWhiteSpace(profileName)
                ? profileName.Trim()
                : (_settings.a3sLocalApiDefaultProfile ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(name))
            {
                var match = profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new InvalidOperationException($"Profile \"{name}\" was not found.");
                return match;
            }

            if (profiles.Count == 1)
                return profiles[0];

            throw new InvalidOperationException(
                "Multiple profiles exist — pass \"profile\" or set a3sLocalApiDefaultProfile.");
        }

        /// <summary>
        /// Start the Arma dedicated server for a profile (UI launch path when available).
        /// </summary>
        public async Task<A3SRemoteServerControlResult> StartServerAsync(
            string? profileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = ResolveProfile(profileName);
            var port = profile.Port <= 0 ? 2302 : profile.Port;

            var already = ServerLaunch.FindMatchingArmaProcesses(profile);
            if (already.Count > 0)
            {
                return new A3SRemoteServerControlResult
                {
                    Action = "start",
                    ProfileName = profile.Name ?? string.Empty,
                    ProfileId = profile.Id ?? string.Empty,
                    Port = port,
                    Ok = true,
                    Message = $"Server for \"{profile.Name}\" already running ({already.Count} process(es)).",
                    ProcessIds = already.Select(p => p.ProcessId).ToList()
                };
            }

            if (_launchAsync != null)
            {
                await _launchAsync(profile).ConfigureAwait(false);
            }
            else
            {
                TryLaunchProfileProcess(profile);
            }

            // Brief settle so WMI can see the new process when callers check immediately.
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            var running = ServerLaunch.FindMatchingArmaProcesses(profile);
            return new A3SRemoteServerControlResult
            {
                Action = "start",
                ProfileName = profile.Name ?? string.Empty,
                ProfileId = profile.Id ?? string.Empty,
                Port = port,
                Ok = true,
                Message = running.Count > 0
                    ? $"Started \"{profile.Name}\" (PID {string.Join(", ", running.Select(p => p.ProcessId))})."
                    : $"Launch requested for \"{profile.Name}\" (process not visible yet).",
                ProcessIds = running.Select(p => p.ProcessId).ToList()
            };
        }

        /// <summary>
        /// Stop arma3server* (+ HC) processes matching the profile.
        /// </summary>
        public A3SRemoteServerControlResult StopServer(string? profileName)
        {
            var profile = ResolveProfile(profileName);
            var port = profile.Port <= 0 ? 2302 : profile.Port;
            var stopped = ServerLaunch.StopMatchingArmaProcesses(profile);
            return new A3SRemoteServerControlResult
            {
                Action = "stop",
                ProfileName = profile.Name ?? string.Empty,
                ProfileId = profile.Id ?? string.Empty,
                Port = port,
                Ok = true,
                Message = stopped.Count == 0
                    ? $"No running arma3server process matched profile \"{profile.Name}\"."
                    : $"Stopped {stopped.Count} process(es) for \"{profile.Name}\" (PID {string.Join(", ", stopped.Select(p => p.ProcessId))}).",
                ProcessIds = stopped.Select(p => p.ProcessId).ToList()
            };
        }

        private void TryLaunchProfileProcess(ServerProfile profile)
        {
            profile.RaisePropertyChanged(nameof(ServerProfile.CommandLine));
            var resolved = ServerLaunch.ResolveExecutable(profile.Executable, _settings.serverPath);
            if (resolved == null)
            {
                throw new InvalidOperationException(
                    $"Could not resolve arma3server executable for profile \"{profile.Name}\".");
            }

            profile.Executable = resolved;
            ServerLaunch.StartServer(profile.Executable, profile.CommandLine, profile.ArmaPath);
        }
    }
}
