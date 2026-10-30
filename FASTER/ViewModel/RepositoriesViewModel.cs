using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using FASTER.Models;
using FASTER.Services.A3S;

namespace FASTER.ViewModel
{
    /// <summary>
    /// Native A3S Repositories shell (Phase 0). Add/remove/persist name + autoconfig URL.
    /// Sync/Check/Cancel land in Phase 1 — busy/log props mirror Updater for that work.
    /// </summary>
    public class RepositoriesViewModel : INotifyPropertyChanged
    {
        private readonly A3SRepositoryService _repositoryService;
        private A3SRepository? _selectedRepository;
        private string _newName = string.Empty;
        private string _newAutoconfigUrl = string.Empty;
        private bool _isBusy;
        private string _statusText = string.Empty;
        private string _output = string.Empty;
        private bool _isAddPanelOpen;

        public RepositoriesViewModel()
            : this(new A3SRepositoryService())
        {
        }

        public RepositoriesViewModel(A3SRepositoryService repositoryService)
        {
            _repositoryService = repositoryService ?? throw new ArgumentNullException(nameof(repositoryService));
            Repositories = _repositoryService.Repositories;
            AppendLog("Repositories ready. Sync/Check arrive in a later phase.");
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

        public bool IsAddPanelOpen
        {
            get => _isAddPanelOpen;
            set
            {
                _isAddPanelOpen = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>Updater-style busy flag (unused until Phase 1 sync).</summary>
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
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

        /// <summary>Updater-style log pane text.</summary>
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
            IsAddPanelOpen = true;
        }

        public void CancelAdd()
        {
            IsAddPanelOpen = false;
            NewName = string.Empty;
            NewAutoconfigUrl = string.Empty;
        }

        /// <summary>Add repository by name + autoconfig URL and persist (no network).</summary>
        public bool AddRepository()
        {
            try
            {
                var repo = _repositoryService.ImportByAutoconfigUrl(NewName, NewAutoconfigUrl);
                SelectedRepository = repo;
                IsAddPanelOpen = false;
                NewName = string.Empty;
                NewAutoconfigUrl = string.Empty;
                RaisePropertyChanged(nameof(HasRepositories));
                StatusText = $"Added \"{repo.Name}\".";
                AppendLog($"Added repository \"{repo.Name}\" → {repo.AutoconfigUrl}");
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
