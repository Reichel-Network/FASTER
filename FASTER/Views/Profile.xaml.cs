using System;
using FASTER.ViewModel;
using System.Windows;
using System.Windows.Controls;

namespace FASTER.Views
{
    /// <summary>
    /// Interaction logic for Profile.xaml
    /// </summary>
    public partial class Profile
    {
        public Profile()
        {
            InitializeComponent();
            this.Dispatcher.ShutdownStarted += DispatcherOnShutdownStarted;
        }

        private void DispatcherOnShutdownStarted(object sender, EventArgs e)
        {
            ((ProfileViewModel) DataContext)?.UnloadData();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
            {
                IA3SSyncBeforeLaunch.IsChecked = vm.A3SSyncBeforeLaunch;
                vm.RefreshA3SRepositories();
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel)DataContext)?.UnloadData();
        }

        public MainWindow MetroWindow => (MainWindow)Window.GetWindow(this);

        internal void Refresh()
        {
            ((ProfileViewModel) DataContext)?.LoadData();
        }

        private void ClearModOrder(object sender, RoutedEventArgs e)
        {
            ( (ProfileViewModel) DataContext )?.ClearModOrder();
        }

        private void CopyFromClient(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.ModsCopyFrom(((MenuItem)sender).Tag, "Server + Client");
        }

        private void CopyFromServer(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.ModsCopyFrom(((MenuItem)sender).Tag, "Server Only");
        }

        private void CopyFromHeadless(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.ModsCopyFrom(((MenuItem)sender).Tag, "HC");
        }
		
        private void CopyFromOpt(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.ModsCopyFrom(((MenuItem)sender).Tag, "Opt");
        }

        private void ModsSelectAll(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.ModsSelectAll(((MenuItem)sender).Tag, true);
        }

        private void ModsSelectNone(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.ModsSelectAll(((MenuItem)sender).Tag, false);
        }

        private void MissionSelectAll(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.MissionSelectAll(true);
        }

        private void MissionSelectNone(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.MissionSelectAll(false);
        }

        private void MissionRefresh(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.LoadMissions();
        }

        private void LoadFromFile_Click(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.LoadModsFromFile();
        }

        private async void CopyModKeys_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.CopyModKeys();
        }

        private async void ClearModKeys_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.ClearModKeys();
        }

        private void SelectServerFile(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.SelectServerFile();
        }

        private void SelectBePath(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.SelectBePath();
        }

        private void SelectKeysFolder(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.SelectKeysFolder();
        }

        private void OpenProfileLocation(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.OpenProfileLocation();
        }

        private void SaveProfile(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.SaveProfile();
        }

        private void DeleteProfile(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.DeleteProfile();
        }

        private async void LaunchServer(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.LaunchServer();
        }

        private async void ApplyA3SEvent_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.ApplyAttachedA3SEventAsync();
        }

        private async void WriteA3SFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.WriteA3SFavoriteServerAsync();
        }

        private async void SyncEventDeployLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.SyncAttachedEventDeployLaunchAsync();
        }

        private async void RefreshA3SEvents_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.RefreshA3SEventsAsync();
        }

        private async void A3SRepository_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is ProfileViewModel vm)
                await vm.RefreshA3SEventsAsync();
        }

        private void A3SSyncBeforeLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ProfileViewModel vm || sender is not System.Windows.Controls.CheckBox box)
                return;
            vm.A3SSyncBeforeLaunch = box.IsChecked == true;
        }

        private void LaunchHCs(object sender, RoutedEventArgs e)
        {
            ((ProfileViewModel) DataContext)?.LaunchHCs();
        }
    }
}
