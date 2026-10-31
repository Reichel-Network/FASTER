using FASTER.ViewModel;
using System.Windows;
using System.Windows.Controls;

namespace FASTER.Views
{
    /// <summary>
    /// Native A3S Repositories (Phases 1–3: sync, build/publish, events ↔ profiles).
    /// </summary>
    public partial class Repositories : UserControl
    {
        public Repositories()
        {
            InitializeComponent();
        }

        private RepositoriesViewModel? Vm => DataContext as RepositoriesViewModel;

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Vm?.LoadData();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Vm?.UnloadData();
        }

        private void AddRepository_Click(object sender, RoutedEventArgs e)
        {
            Vm?.OpenAddPanel();
        }

        private void SaveAdd_Click(object sender, RoutedEventArgs e)
        {
            Vm?.AddRepository();
        }

        private void CancelAdd_Click(object sender, RoutedEventArgs e)
        {
            Vm?.CancelAdd();
        }

        private void Configure_Click(object sender, RoutedEventArgs e)
        {
            Vm?.OpenConfigPanel();
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            Vm?.SaveConfig();
        }

        private void CancelConfig_Click(object sender, RoutedEventArgs e)
        {
            Vm?.CancelConfig();
        }

        private void UseDeploymentPath_Click(object sender, RoutedEventArgs e)
        {
            Vm?.UseDeploymentInstallPath();
        }

        private void RemoveRepository_Click(object sender, RoutedEventArgs e)
        {
            Vm?.RemoveSelected();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Vm?.RefreshFromSettings();
        }

        private async void Check_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;
            await Vm.CheckSelectedAsync();
        }

        private async void Sync_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;
            await Vm.SyncSelectedAsync();
        }

        private async void Build_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;
            await Vm.BuildSelectedAsync();
        }

        private async void Publish_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;
            await Vm.PublishSelectedAsync();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Vm?.CancelWork();
        }

        private async void LoadEvents_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;
            await Vm.LoadEventsAsync();
        }

        private void ApplyEvent_Click(object sender, RoutedEventArgs e)
        {
            Vm?.ApplySelectedEventToProfile();
        }

        private async void WriteFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;
            await Vm.WriteFavoriteServerAsync();
        }

        private async void SyncEventDeployLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;
            await Vm.SyncEventDeployLaunchAsync(launch: true);
        }
    }
}
