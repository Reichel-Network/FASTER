using FASTER.ViewModel;
using System.Windows;
using System.Windows.Controls;

namespace FASTER.Views
{
    /// <summary>
    /// Native A3S Repositories shell (Phase 0).
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

        private void RemoveRepository_Click(object sender, RoutedEventArgs e)
        {
            Vm?.RemoveSelected();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Vm?.RefreshFromSettings();
        }
    }
}
