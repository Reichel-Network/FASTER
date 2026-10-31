using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using FASTER.Models;
using FASTER.Properties;
using FASTER.Services.A3S;

namespace FASTER.ViewModel
{
    /// <summary>
    /// Native A3S Repositories (Phases 1–3: sync, build/publish, events ↔ profiles) with Updater-like busy/log.
    /// </summary>
    public class RepositoriesViewModel : INotifyPropertyChanged
    {
        private readonly A3SRepositoryService _repositoryService;
        private readonly IA3SMetadataStore _metadataStore;
        private readonly A3SProtocolClient _protocolClient;
        private readonly A3SSyncService _syncService;
        private readonly A3SBuildService _buildService;
        private readonly A3SPublishService _publishService;
        private readonly A3SModImport _modImport;
        private readonly A3SEventService _eventService;

        private A3SRepository? _selectedRepository;
        private string _newName = string.Empty;
        private string _newAutoconfigUrl = string.Empty;
        private string _newLocalPath = string.Empty;
        private string _editPublishRoot = string.Empty;
        private bool _editUseDeploymentInstallPath;
        private string _editUploadProtocol = "HTTPS";
        private string _editUploadUrl = string.Empty;
        private string _editUploadPort = string.Empty;
        private string _editUploadLogin = string.Empty;
        private string _editUploadPassword = string.Empty;
        private string _editNumberOfConnections = "4";
        private bool _isBusy;
        private bool _exactMatch;
        private string _statusText = string.Empty;
        private string _output = string.Empty;
        private bool _isAddPanelOpen;
        private bool _isConfigPanelOpen;
        private double _progress;
        private CancellationTokenSource? _cts;
        private A3SEventInfo? _selectedEvent;
        private ServerProfile? _selectedProfileForEvent;
        private bool _applyEventClient = true;
        private bool _applyEventServer = true;
        private bool _applyEventHeadless = true;

        public RepositoriesViewModel()
            : this(new A3SRepositoryService())
        {
        }

        public RepositoriesViewModel(A3SRepositoryService repositoryService)
            : this(
                repositoryService,
                A3SMetadataStoreFactory.CreateDefault(),
                new A3SProtocolClient(),
                null,
                null,
                null,
                new A3SModImport(),
                null)
        {
        }

        internal RepositoriesViewModel(
            A3SRepositoryService repositoryService,
            IA3SMetadataStore metadataStore,
            A3SProtocolClient protocolClient,
            A3SSyncService? syncService,
            A3SBuildService? buildService,
            A3SPublishService? publishService,
            A3SModImport modImport,
            A3SEventService? eventService)
        {
            _repositoryService = repositoryService ?? throw new ArgumentNullException(nameof(repositoryService));
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            _protocolClient = protocolClient ?? throw new ArgumentNullException(nameof(protocolClient));
            _syncService = syncService ?? new A3SSyncService(_metadataStore, _protocolClient);
            _buildService = buildService ?? new A3SBuildService(_metadataStore);
            _publishService = publishService ?? new A3SPublishService(_metadataStore, _protocolClient, _buildService);
            _modImport = modImport ?? throw new ArgumentNullException(nameof(modImport));
            _eventService = eventService ?? new A3SEventService(
                _metadataStore, _repositoryService, _syncService, _modImport, Settings.Default);
            Repositories = _repositoryService.Repositories;
            Events = new ObservableCollection<A3SEventInfo>();
            Profiles = new ObservableCollection<ServerProfile>();
            AppendLog(Settings.Default.a3sPreferNativeSerializer
                ? "Repositories ready. Sync/Check + Events use C# ObjectStream metadata reads; Build/Publish writes still use the bridge."
                : "Repositories ready. Sync/Check + Build/Publish + Events use native C# + IA3SMetadataStore Java bridge.");
        }

        public ObservableCollection<A3SRepository> Repositories { get; private set; }

        public ObservableCollection<A3SEventInfo> Events { get; }

        public ObservableCollection<ServerProfile> Profiles { get; }

        public A3SRepository? SelectedRepository
        {
            get => _selectedRepository;
            set
            {
                _selectedRepository = value;
                RaisePropertyChanged();
                if (value != null && IsConfigPanelOpen)
                    LoadConfigFromSelected();
                _ = RefreshEventsQuietAsync();
            }
        }

        public A3SEventInfo? SelectedEvent
        {
            get => _selectedEvent;
            set { _selectedEvent = value; RaisePropertyChanged(); }
        }

        public ServerProfile? SelectedProfileForEvent
        {
            get => _selectedProfileForEvent;
            set { _selectedProfileForEvent = value; RaisePropertyChanged(); }
        }

        public bool ApplyEventClient
        {
            get => _applyEventClient;
            set { _applyEventClient = value; RaisePropertyChanged(); }
        }

        public bool ApplyEventServer
        {
            get => _applyEventServer;
            set { _applyEventServer = value; RaisePropertyChanged(); }
        }

        public bool ApplyEventHeadless
        {
            get => _applyEventHeadless;
            set { _applyEventHeadless = value; RaisePropertyChanged(); }
        }

        public bool HasEvents => Events.Count > 0;

        public string NewName
        {
            get => _newName;
            set { _newName = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string NewAutoconfigUrl
        {
            get => _newAutoconfigUrl;
            set { _newAutoconfigUrl = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string NewLocalPath
        {
            get => _newLocalPath;
            set { _newLocalPath = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string EditPublishRoot
        {
            get => _editPublishRoot;
            set { _editPublishRoot = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public bool EditUseDeploymentInstallPath
        {
            get => _editUseDeploymentInstallPath;
            set { _editUseDeploymentInstallPath = value; RaisePropertyChanged(); }
        }

        public string EditUploadProtocol
        {
            get => _editUploadProtocol;
            set { _editUploadProtocol = value ?? "HTTPS"; RaisePropertyChanged(); }
        }

        public string EditUploadUrl
        {
            get => _editUploadUrl;
            set { _editUploadUrl = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string EditUploadPort
        {
            get => _editUploadPort;
            set { _editUploadPort = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string EditUploadLogin
        {
            get => _editUploadLogin;
            set { _editUploadLogin = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string EditUploadPassword
        {
            get => _editUploadPassword;
            set { _editUploadPassword = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string EditNumberOfConnections
        {
            get => _editNumberOfConnections;
            set { _editNumberOfConnections = value ?? "4"; RaisePropertyChanged(); }
        }

        public bool IsAddPanelOpen
        {
            get => _isAddPanelOpen;
            set { _isAddPanelOpen = value; RaisePropertyChanged(); }
        }

        public bool IsConfigPanelOpen
        {
            get => _isConfigPanelOpen;
            set { _isConfigPanelOpen = value; RaisePropertyChanged(); }
        }

        public bool ExactMatch
        {
            get => _exactMatch;
            set { _exactMatch = value; RaisePropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanStartWork));
            }
        }

        public bool CanStartWork => !IsBusy;

        public double Progress
        {
            get => _progress;
            set { _progress = value; RaisePropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public string Output
        {
            get => _output;
            set { _output = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public bool HasRepositories => Repositories.Count > 0;

        public void LoadData()
        {
            Repositories = _repositoryService.Repositories;
            RaisePropertyChanged(nameof(Repositories));
            RaisePropertyChanged(nameof(HasRepositories));
            RefreshProfiles();
            StatusText = Repositories.Count == 0
                ? "No repositories yet. Add one with a name and autoconfig URL."
                : $"{Repositories.Count} repository(ies) loaded.";
            _ = RefreshEventsQuietAsync();
        }

        public void RefreshProfiles()
        {
            Profiles.Clear();
            var list = Settings.Default.Profiles;
            if (list == null)
                return;
            foreach (var p in list)
                Profiles.Add(p);
            if (SelectedProfileForEvent != null
                && Profiles.All(p => p.Id != SelectedProfileForEvent.Id))
            {
                SelectedProfileForEvent = null;
            }
        }

        public void UnloadData()
        {
            _repositoryService.Persist();
        }

        public void OpenAddPanel()
        {
            NewName = string.Empty;
            NewAutoconfigUrl = string.Empty;
            NewLocalPath = Settings.Default.a3sDefaultSyncPath ?? string.Empty;
            IsConfigPanelOpen = false;
            IsAddPanelOpen = true;
        }

        public void CancelAdd()
        {
            IsAddPanelOpen = false;
            NewName = string.Empty;
            NewAutoconfigUrl = string.Empty;
            NewLocalPath = string.Empty;
        }

        public void OpenConfigPanel()
        {
            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository to configure.");
                return;
            }

            IsAddPanelOpen = false;
            LoadConfigFromSelected();
            IsConfigPanelOpen = true;
        }

        public void CancelConfig()
        {
            IsConfigPanelOpen = false;
        }

        public bool SaveConfig()
        {
            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository to configure.");
                return false;
            }

            try
            {
                var connections = 4;
                if (!string.IsNullOrWhiteSpace(EditNumberOfConnections)
                    && (!int.TryParse(EditNumberOfConnections.Trim(), out connections) || connections < 1))
                {
                    throw new ArgumentException("Number of connections must be a positive integer.");
                }

                _repositoryService.ConfigurePublish(
                    SelectedRepository,
                    publishRoot: EditPublishRoot,
                    useDeploymentInstallPath: EditUseDeploymentInstallPath,
                    uploadProtocol: EditUploadProtocol,
                    uploadUrl: EditUploadUrl,
                    uploadPort: EditUploadPort,
                    uploadLogin: EditUploadLogin,
                    uploadPassword: EditUploadPassword,
                    numberOfConnections: connections);

                IsConfigPanelOpen = false;
                StatusText = $"Configured \"{SelectedRepository.Name}\".";
                AppendLog($"Configured publish settings for \"{SelectedRepository.Name}\" " +
                          $"(protocol={SelectedRepository.UploadProtocol}, root={A3SBuildService.ResolvePublishRoot(SelectedRepository)}).");
                DisplayMessage(StatusText);
                RaisePropertyChanged(nameof(SelectedRepository));
                return true;
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                AppendLog($"Configure failed: {ex.Message}");
                DisplayMessage(ex.Message);
                return false;
            }
        }

        public void UseDeploymentInstallPath()
        {
            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository first.");
                return;
            }

            try
            {
                _repositoryService.UseDeploymentInstallPathAsPublishRoot(SelectedRepository);
                LoadConfigFromSelected();
                StatusText = $"Publish root set to Deployment InstallPath.";
                AppendLog($"{SelectedRepository.Name}: publish root → {SelectedRepository.PublishRoot}");
                DisplayMessage(StatusText);
                RaisePropertyChanged(nameof(SelectedRepository));
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                AppendLog($"Use Deployment InstallPath failed: {ex.Message}");
                DisplayMessage(ex.Message);
            }
        }

        public bool AddRepository()
        {
            try
            {
                var local = string.IsNullOrWhiteSpace(NewLocalPath) ? null : NewLocalPath.Trim();
                var repo = _repositoryService.ImportByAutoconfigUrl(NewName, NewAutoconfigUrl, local);
                SelectedRepository = repo;
                IsAddPanelOpen = false;
                NewName = string.Empty;
                NewAutoconfigUrl = string.Empty;
                NewLocalPath = string.Empty;
                RaisePropertyChanged(nameof(HasRepositories));
                StatusText = $"Added \"{repo.Name}\".";
                AppendLog($"Added repository \"{repo.Name}\" → {repo.AutoconfigUrl}");
                if (!string.IsNullOrWhiteSpace(repo.LocalPath))
                    AppendLog($"  Local path: {repo.LocalPath}");
                DisplayMessage($"Repository \"{repo.Name}\" saved.");
                return true;
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                AppendLog($"Add failed: {ex.Message}");
                DisplayMessage(ex.Message);
                return false;
            }
        }

        public void RemoveSelected()
        {
            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository to remove.");
                return;
            }

            var name = SelectedRepository.Name;
            if (_repositoryService.Remove(SelectedRepository))
            {
                SelectedRepository = null;
                IsConfigPanelOpen = false;
                RaisePropertyChanged(nameof(HasRepositories));
                StatusText = $"Removed \"{name}\".";
                AppendLog($"Removed repository \"{name}\".");
            }
        }

        public void RefreshFromSettings()
        {
            LoadData();
            AppendLog("Refreshed repository list from settings.");
        }

        public async Task CheckSelectedAsync()
        {
            if (!TryBeginWork(out var repo, out var token, requireLocalPath: true))
                return;

            try
            {
                StatusText = $"Checking \"{repo.Name}\"…";
                AppendLog($"Check started: {repo.Name}");
                var progress = CreateProgress();
                var result = await _syncService.CheckAsync(repo, progress, token).ConfigureAwait(true);
                _repositoryService.Persist();

                AppendLog(
                    $"Check complete: revision {result.Revision}, remote files {result.TotalRemoteFiles}, " +
                    $"missing/outdated {result.MissingOrOutdated}, extra local {result.ExtraLocalFiles}, " +
                    $"download ~{Functions.ParseFileSize(result.DownloadBytes)}.");
                StatusText = $"Checked \"{repo.Name}\" — rev {result.Revision}, {result.MissingOrOutdated} to update.";
                DisplayMessage(StatusText);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Check cancelled.";
                AppendLog("Check cancelled.");
            }
            catch (Exception ex)
            {
                StatusText = $"Check failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            finally
            {
                EndWork();
            }
        }

        public async Task SyncSelectedAsync()
        {
            if (!TryBeginWork(out var repo, out var token, requireLocalPath: true))
                return;

            var exact = ExactMatch;
            var confirmed = false;
            if (exact)
            {
                var answer = MessageBox.Show(
                    "Exact-match will DELETE local files under this repository path that are not in the remote sync tree " +
                    "(except .a3s metadata and hidden folders). Continue?",
                    "Confirm exact-match delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                confirmed = answer == MessageBoxResult.Yes;
                if (!confirmed)
                {
                    AppendLog("Exact-match declined; sync will keep extra local files.");
                    exact = false;
                }
            }

            try
            {
                StatusText = $"Syncing \"{repo.Name}\"…";
                AppendLog($"Sync started: {repo.Name} (exact-match={exact})");
                var progress = CreateProgress();
                var result = await _syncService.SyncAsync(
                        repo,
                        new A3SSyncOptions { ExactMatch = exact, ExactMatchConfirmed = confirmed },
                        progress,
                        token)
                    .ConfigureAwait(true);

                foreach (var err in result.Errors)
                    AppendLog("  Error: " + err);

                AppendLog(
                    $"Sync complete: revision {result.Revision}, downloaded {result.Downloaded}, " +
                    $"unchanged {result.Unchanged}, deleted {result.Deleted}, failed {result.Failed}.");

                try
                {
                    var import = await _modImport
                        .ImportFromRepositoryAsync(
                            repo,
                            addonRelativePaths: result.AddonRelativePaths,
                            syncDeploymentList: true,
                            cancellationToken: token)
                        .ConfigureAwait(true);
                    AppendLog($"Mod import: added {import.Added}, updated {import.Updated}, skipped {import.Skipped}.");
                }
                catch (Exception importEx)
                {
                    AppendLog($"Mod import warning: {importEx.Message}");
                }

                _repositoryService.Persist();
                StatusText = result.Failed > 0
                    ? $"Sync finished with {result.Failed} failure(s) — rev {result.Revision}."
                    : $"Synced \"{repo.Name}\" — rev {result.Revision}.";
                DisplayMessage(StatusText);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Sync cancelled.";
                AppendLog("Sync cancelled.");
            }
            catch (Exception ex)
            {
                StatusText = $"Sync failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            finally
            {
                EndWork();
            }
        }

        public async Task BuildSelectedAsync()
        {
            if (!TryBeginWork(out var repo, out var token, requireLocalPath: false))
                return;

            try
            {
                var root = A3SBuildService.ResolvePublishRoot(repo);
                StatusText = $"Building \"{repo.Name}\"…";
                AppendLog($"Build started: {repo.Name} → {root}");
                var progress = CreateProgress();
                var result = await _buildService.BuildAsync(repo, progress: progress, cancellationToken: token)
                    .ConfigureAwait(true);
                _repositoryService.Persist();

                AppendLog(
                    $"Build complete: revision {result.Revision}, files {result.FileCount}, " +
                    $"size ~{Functions.ParseFileSize(result.TotalBytes)}, " +
                    $"addons +{result.NewAddons}/~{result.UpdatedAddons}/-{result.DeletedAddons}.");
                StatusText = $"Built \"{repo.Name}\" — rev {result.Revision}.";
                DisplayMessage(StatusText);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Build cancelled.";
                AppendLog("Build cancelled.");
            }
            catch (Exception ex)
            {
                StatusText = $"Build failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            finally
            {
                EndWork();
            }
        }

        public async Task PublishSelectedAsync()
        {
            if (!TryBeginWork(out var repo, out var token, requireLocalPath: false))
                return;

            try
            {
                StatusText = $"Publishing \"{repo.Name}\"…";
                AppendLog($"Publish started: {repo.Name} (upload={repo.UploadProtocol})");
                var progress = CreateProgress();
                var result = await _publishService.PublishAsync(
                        repo,
                        new A3SUploadOptions { BuildFirst = true },
                        progress,
                        token)
                    .ConfigureAwait(true);

                foreach (var err in result.Errors)
                    AppendLog("  Error: " + err);

                AppendLog(
                    $"Publish complete: revision {result.Revision}, uploaded {result.UploadedFiles} file(s), " +
                    $"metadata {result.UploadedMetadataFiles}, skipped {result.SkippedUnchanged}, failed {result.Failed}.");
                _repositoryService.Persist();
                StatusText = result.Failed > 0
                    ? $"Publish finished with {result.Failed} failure(s) — rev {result.Revision}."
                    : $"Published \"{repo.Name}\" — rev {result.Revision}.";
                DisplayMessage(StatusText);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Publish cancelled.";
                AppendLog("Publish cancelled.");
            }
            catch (Exception ex)
            {
                StatusText = $"Publish failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            finally
            {
                EndWork();
            }
        }

        public void CancelWork()
        {
            try
            {
                _cts?.Cancel();
                AppendLog("Cancel requested.");
                StatusText = "Cancelling…";
            }
            catch
            {
                // ignore
            }
        }

        public async Task LoadEventsAsync()
        {
            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository first.");
                return;
            }

            if (IsBusy)
            {
                DisplayMessage("A repository operation is already running.");
                return;
            }

            IsBusy = true;
            Progress = 0;
            try
            {
                StatusText = $"Loading events for \"{SelectedRepository.Name}\"…";
                AppendLog($"Events load: {SelectedRepository.Name}");
                var list = await _eventService.ListEventsAsync(SelectedRepository).ConfigureAwait(true);
                Events.Clear();
                foreach (var evt in list)
                    Events.Add(evt);
                SelectedEvent = Events.FirstOrDefault();
                RaisePropertyChanged(nameof(HasEvents));
                StatusText = Events.Count == 0
                    ? $"No events in \"{SelectedRepository.Name}\" metadata."
                    : $"{Events.Count} event(s) loaded from \"{SelectedRepository.Name}\".";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            catch (Exception ex)
            {
                StatusText = $"Load events failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            finally
            {
                IsBusy = false;
                Progress = 0;
            }
        }

        public void ApplySelectedEventToProfile()
        {
            if (SelectedEvent == null)
            {
                DisplayMessage("Select an event first (Load Events).");
                return;
            }

            if (SelectedProfileForEvent == null)
            {
                DisplayMessage("Select a server profile to apply the event modset.");
                return;
            }

            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository first.");
                return;
            }

            try
            {
                EnsureProfileModsLoaded(SelectedProfileForEvent);
                var result = _eventService.ApplyEventToProfile(
                    SelectedEvent,
                    SelectedProfileForEvent,
                    new A3SEventApplyOptions
                    {
                        ApplyClient = ApplyEventClient,
                        ApplyServer = ApplyEventServer,
                        ApplyHeadless = ApplyEventHeadless
                    });
                _eventService.AttachEventToProfile(SelectedProfileForEvent, SelectedRepository, SelectedEvent.Name);

                AppendLog(
                    $"Applied event \"{result.EventName}\" → profile \"{SelectedProfileForEvent.Name}\": " +
                    $"matched {result.MatchedMods.Count}, missing {result.MissingAddons.Count}.");
                if (result.MissingAddons.Count > 0)
                    AppendLog("  Missing addons: " + string.Join(", ", result.MissingAddons));

                StatusText = $"Event \"{result.EventName}\" applied to \"{SelectedProfileForEvent.Name}\".";
                DisplayMessage(StatusText);
            }
            catch (Exception ex)
            {
                StatusText = $"Apply event failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
        }

        public async Task WriteFavoriteServerAsync()
        {
            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository first.");
                return;
            }

            if (SelectedProfileForEvent == null)
            {
                DisplayMessage("Select a server profile for the favorite server entry.");
                return;
            }

            if (IsBusy)
            {
                DisplayMessage("A repository operation is already running.");
                return;
            }

            IsBusy = true;
            try
            {
                StatusText = "Writing favorite server into autoconfig…";
                AppendLog($"Favorite server ← profile \"{SelectedProfileForEvent.Name}\"");
                var fav = await _eventService
                    .WriteFavoriteServerFromProfileAsync(SelectedRepository, SelectedProfileForEvent)
                    .ConfigureAwait(true);
                StatusText = $"Favorite \"{fav.Name}\" ({fav.IpAddress}:{fav.Port}) written to autoconfig.";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            catch (Exception ex)
            {
                StatusText = $"Write favorite failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task SyncEventDeployLaunchAsync(bool launch)
        {
            if (!TryBeginWork(out var repo, out var token, requireLocalPath: true))
                return;

            if (SelectedEvent == null)
            {
                EndWork();
                DisplayMessage("Select an event first (Load Events).");
                return;
            }

            if (SelectedProfileForEvent == null)
            {
                EndWork();
                DisplayMessage("Select a server profile for Sync → Deploy → Launch.");
                return;
            }

            var exact = ExactMatch;
            var confirmed = false;
            if (exact)
            {
                var answer = MessageBox.Show(
                    "Exact-match will DELETE local files under this repository path that are not in the remote sync tree " +
                    "(except .a3s metadata and hidden folders). Continue?",
                    "Confirm exact-match delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                confirmed = answer == MessageBoxResult.Yes;
                if (!confirmed)
                    exact = false;
            }

            try
            {
                EnsureProfileModsLoaded(SelectedProfileForEvent);
                StatusText = launch
                    ? $"Sync event → Deploy → Launch \"{SelectedEvent.Name}\"…"
                    : $"Sync event → Deploy \"{SelectedEvent.Name}\"…";
                AppendLog(StatusText);

                Func<ServerProfile, Task>? launchFn = null;
                if (launch)
                {
                    launchFn = async profile =>
                    {
                        var vm = new ProfileViewModel(profile);
                        vm.SaveProfile();
                        await Task.Yield();
                        vm.LaunchServerCore();
                    };
                }

                Action? deploy = null;
                if (MainWindow.HasLoaded())
                {
                    deploy = () =>
                    {
                        try
                        {
                            MainWindow.Instance.DeployViewModel.DeployAll();
                        }
                        catch
                        {
                            // Event service falls back to internal deploy.
                        }
                    };
                }

                var progress = CreateProgress();
                var result = await _eventService.SyncEventDeployLaunchAsync(
                        repo,
                        SelectedProfileForEvent,
                        SelectedEvent.Name,
                        new A3SEventApplyOptions
                        {
                            ApplyClient = ApplyEventClient,
                            ApplyServer = ApplyEventServer,
                            ApplyHeadless = ApplyEventHeadless
                        },
                        new A3SSyncOptions { ExactMatch = exact, ExactMatchConfirmed = confirmed },
                        progress,
                        launchFn,
                        deploy,
                        token)
                    .ConfigureAwait(true);

                AppendLog(
                    $"Sync event complete: rev {result.Sync.Revision}, downloaded {result.Sync.Downloaded}, " +
                    $"matched mods {result.Apply.MatchedMods.Count}, missing {result.Apply.MissingAddons.Count}, " +
                    $"launched={result.Launched}.");
                if (result.Apply.MissingAddons.Count > 0)
                    AppendLog("  Missing addons: " + string.Join(", ", result.Apply.MissingAddons));

                StatusText = result.Launched
                    ? $"Synced event \"{SelectedEvent.Name}\" and launched \"{SelectedProfileForEvent.Name}\"."
                    : $"Synced event \"{SelectedEvent.Name}\" and deployed for \"{SelectedProfileForEvent.Name}\".";
                DisplayMessage(StatusText);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Sync event cancelled.";
                AppendLog(StatusText);
            }
            catch (Exception ex)
            {
                StatusText = $"Sync event failed: {ex.Message}";
                AppendLog(StatusText);
                DisplayMessage(StatusText);
            }
            finally
            {
                EndWork();
            }
        }

        private async Task RefreshEventsQuietAsync()
        {
            if (SelectedRepository == null || IsBusy)
            {
                Events.Clear();
                SelectedEvent = null;
                RaisePropertyChanged(nameof(HasEvents));
                return;
            }

            try
            {
                var list = await _eventService.ListEventsAsync(SelectedRepository).ConfigureAwait(true);
                Events.Clear();
                foreach (var evt in list)
                    Events.Add(evt);
                SelectedEvent = Events.FirstOrDefault(e =>
                    SelectedProfileForEvent != null
                    && string.Equals(e.Name, SelectedProfileForEvent.A3SEventName, StringComparison.OrdinalIgnoreCase))
                    ?? Events.FirstOrDefault();
                RaisePropertyChanged(nameof(HasEvents));
            }
            catch
            {
                // Quiet refresh — operator can click Load Events for errors.
                Events.Clear();
                SelectedEvent = null;
                RaisePropertyChanged(nameof(HasEvents));
            }
        }

        private static void EnsureProfileModsLoaded(ServerProfile profile)
        {
            if (profile.ProfileMods == null)
                profile.ProfileMods = new List<ProfileMod>();

            if (Settings.Default.armaMods?.ArmaMods == null)
                return;

            var modlist = new List<ProfileMod>();
            foreach (var mod in Settings.Default.armaMods.ArmaMods)
            {
                var existing = profile.ProfileMods.Find(m => m.Id == mod.WorkshopId);
                if (existing == null)
                {
                    modlist.Add(new ProfileMod
                    {
                        Name = mod.Name,
                        Id = mod.WorkshopId,
                        IsLocal = mod.IsLocal
                    });
                }
                else
                {
                    existing.Name = mod.Name;
                    modlist.Add(existing);
                }
            }

            foreach (var leftover in profile.ProfileMods.Where(pm =>
                         modlist.All(m => m.Id != pm.Id)))
            {
                modlist.Add(leftover);
            }

            profile.ProfileMods = modlist;
        }

        private void LoadConfigFromSelected()
        {
            if (SelectedRepository == null)
                return;

            EditPublishRoot = SelectedRepository.PublishRoot;
            EditUseDeploymentInstallPath = SelectedRepository.UseDeploymentInstallPath;
            EditUploadProtocol = string.IsNullOrWhiteSpace(SelectedRepository.UploadProtocol)
                ? "HTTPS"
                : SelectedRepository.UploadProtocol;
            EditUploadUrl = SelectedRepository.UploadUrl;
            EditUploadPort = SelectedRepository.UploadPort;
            EditUploadLogin = SelectedRepository.UploadLogin;
            EditUploadPassword = SelectedRepository.UploadPassword;
            EditNumberOfConnections = SelectedRepository.NumberOfConnections <= 0
                ? "4"
                : SelectedRepository.NumberOfConnections.ToString();
        }

        private bool TryBeginWork(out A3SRepository repo, out CancellationToken token, bool requireLocalPath)
        {
            repo = null!;
            token = CancellationToken.None;

            if (IsBusy)
            {
                DisplayMessage("A repository operation is already running.");
                return false;
            }

            if (SelectedRepository == null)
            {
                DisplayMessage("Select a repository first.");
                return false;
            }

            if (requireLocalPath && string.IsNullOrWhiteSpace(SelectedRepository.LocalPath)
                && string.IsNullOrWhiteSpace(SelectedRepository.PublishRoot)
                && !SelectedRepository.UseDeploymentInstallPath)
            {
                DisplayMessage("Set a local sync path for this repository (add again with a path, or Configure).");
                return false;
            }

            repo = SelectedRepository;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            IsBusy = true;
            Progress = 0;
            return true;
        }

        private void EndWork()
        {
            IsBusy = false;
            Progress = 0;
            _cts?.Dispose();
            _cts = null;
            RaisePropertyChanged(nameof(SelectedRepository));
        }

        private IProgress<A3SSyncProgress> CreateProgress()
        {
            return new Progress<A3SSyncProgress>(p =>
            {
                StatusText = p.Message;
                Progress = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Message))
                    AppendLog(p.Message);
            });
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(Output))
                Output = line;
            else
                Output += Environment.NewLine + line;
        }

        private static void DisplayMessage(string msg)
        {
            if (!MainWindow.HasLoaded())
                return;

            MainWindow.Instance.IFlyout.IsOpen = true;
            MainWindow.Instance.IFlyoutMessage.Content = msg;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string? property = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
