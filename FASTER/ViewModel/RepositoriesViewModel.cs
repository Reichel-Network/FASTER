using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    /// Native A3S Repositories (Phase 1): add/remove + Sync/Check/Cancel with Updater-like busy/log.
    /// </summary>
    public class RepositoriesViewModel : INotifyPropertyChanged
    {
        private readonly A3SRepositoryService _repositoryService;
        private readonly IA3SMetadataStore _metadataStore;
        private readonly A3SProtocolClient _protocolClient;
        private readonly A3SSyncService _syncService;
        private readonly A3SModImport _modImport;

        private A3SRepository? _selectedRepository;
        private string _newName = string.Empty;
        private string _newAutoconfigUrl = string.Empty;
        private string _newLocalPath = string.Empty;
        private bool _isBusy;
        private bool _exactMatch;
        private string _statusText = string.Empty;
        private string _output = string.Empty;
        private bool _isAddPanelOpen;
        private double _progress;
        private CancellationTokenSource? _cts;

        public RepositoriesViewModel()
            : this(new A3SRepositoryService())
        {
        }

        public RepositoriesViewModel(A3SRepositoryService repositoryService)
            : this(
                repositoryService,
                new BridgeA3SMetadataStore(Settings.Default.a3sMetadataBridgePath),
                new A3SProtocolClient(),
                null,
                new A3SModImport())
        {
        }

        internal RepositoriesViewModel(
            A3SRepositoryService repositoryService,
            IA3SMetadataStore metadataStore,
            A3SProtocolClient protocolClient,
            A3SSyncService? syncService,
            A3SModImport modImport)
        {
            _repositoryService = repositoryService ?? throw new ArgumentNullException(nameof(repositoryService));
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            _protocolClient = protocolClient ?? throw new ArgumentNullException(nameof(protocolClient));
            _syncService = syncService ?? new A3SSyncService(_metadataStore, _protocolClient);
            _modImport = modImport ?? throw new ArgumentNullException(nameof(modImport));
            Repositories = _repositoryService.Repositories;
            AppendLog("Repositories ready. Sync/Check use native C# download + IA3SMetadataStore bridge.");
        }

        public ObservableCollection<A3SRepository> Repositories { get; private set; }

        public A3SRepository? SelectedRepository
        {
            get => _selectedRepository;
            set
            {
                _selectedRepository = value;
                RaisePropertyChanged();
            }
        }

        public string NewName
        {
            get => _newName;
            set
            {
                _newName = value ?? string.Empty;
                RaisePropertyChanged();
            }
        }

        public string NewAutoconfigUrl
        {
            get => _newAutoconfigUrl;
            set
            {
                _newAutoconfigUrl = value ?? string.Empty;
                RaisePropertyChanged();
            }
        }

        public string NewLocalPath
        {
            get => _newLocalPath;
            set
            {
                _newLocalPath = value ?? string.Empty;
                RaisePropertyChanged();
            }
        }

        public bool IsAddPanelOpen
        {
            get => _isAddPanelOpen;
            set
            {
                _isAddPanelOpen = value;
                RaisePropertyChanged();
            }
        }

        public bool ExactMatch
        {
            get => _exactMatch;
            set
            {
                _exactMatch = value;
                RaisePropertyChanged();
            }
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
            set
            {
                _progress = value;
                RaisePropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value ?? string.Empty;
                RaisePropertyChanged();
            }
        }

        public string Output
        {
            get => _output;
            set
            {
                _output = value ?? string.Empty;
                RaisePropertyChanged();
            }
        }

        public bool HasRepositories => Repositories.Count > 0;

        public void LoadData()
        {
            Repositories = _repositoryService.Repositories;
            RaisePropertyChanged(nameof(Repositories));
            RaisePropertyChanged(nameof(HasRepositories));
            StatusText = Repositories.Count == 0
                ? "No repositories yet. Add one with a name and autoconfig URL."
                : $"{Repositories.Count} repository(ies) loaded.";
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
            IsAddPanelOpen = true;
        }

        public void CancelAdd()
        {
            IsAddPanelOpen = false;
            NewName = string.Empty;
            NewAutoconfigUrl = string.Empty;
            NewLocalPath = string.Empty;
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
            if (!TryBeginWork(out var repo, out var token))
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
            if (!TryBeginWork(out var repo, out var token))
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

        private bool TryBeginWork(out A3SRepository repo, out CancellationToken token)
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

            if (string.IsNullOrWhiteSpace(SelectedRepository.LocalPath))
            {
                DisplayMessage("Set a local sync path for this repository (add again with a path, or edit settings).");
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
