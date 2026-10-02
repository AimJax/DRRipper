using System.Windows;
using System.Windows.Controls;
using DRRipper.UI;

namespace DRRipper.Views
{
    /// <summary>Thin host for <see cref="AddDownloadsViewModel"/> (Ticket #006).</summary>
    public partial class AddDownloadsDialog : Window
    {
        public AddDownloadsDialog()
        {
            InitializeComponent();
        }

        private AddDownloadsViewModel? Vm => DataContext as AddDownloadsViewModel;

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = Vm;
            if (vm == null) { DialogResult = false; return; }
            vm.StartAfterAdd = false;
            await vm.SubmitAsync();
            DialogResult = true;
        }

        private async void AddStartButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = Vm;
            if (vm == null) { DialogResult = false; return; }
            vm.StartAfterAdd = true;
            await vm.SubmitAsync();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = Vm;
            if (vm == null) return;
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = "Select download folder";
                dlg.ShowNewFolderButton = true;
                if (System.IO.Directory.Exists(vm.TargetDirectory))
                    dlg.SelectedPath = vm.TargetDirectory;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    vm.TargetDirectory = dlg.SelectedPath;
            }
            catch { }
        }

        private void ConnectionsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Vm is not { } vm) return;
            if (sender is System.Windows.Controls.ComboBox box && box.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                int.TryParse(item.Content?.ToString(), out int n))
                vm.Connections = n;
        }

        private void ConnectionsBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (Vm is not { } vm) return;
            if (sender is System.Windows.Controls.ComboBox box)
            {
                foreach (var item in box.Items)
                {
                    if (item is System.Windows.Controls.ComboBoxItem ci && int.TryParse(ci.Content?.ToString(), out int n) && n == vm.Connections)
                    {
                        box.SelectedItem = item;
                        return;
                    }
                }
            }
        }

        private void PriorityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var vm = Vm;
            if (vm == null) return;
            if (sender is System.Windows.Controls.ComboBox box && box.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                int.TryParse(item.Content?.ToString(), out int n))
                vm.Priority = n;
        }
    }
}
