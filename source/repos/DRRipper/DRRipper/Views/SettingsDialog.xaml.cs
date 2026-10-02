using System.Windows;
using System.Windows.Controls;
using DRRipper.UI;

namespace DRRipper.Views
{
    /// <summary>Thin host for <see cref="SettingsViewModel"/> (Ticket #006).</summary>
    public partial class SettingsDialog : Window
    {
        public SettingsDialog()
        {
            InitializeComponent();
        }

        private SettingsViewModel? Vm => DataContext as SettingsViewModel;
        private bool _syncing;

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
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
                dlg.Description = "Select default download folder";
                dlg.ShowNewFolderButton = true;
                if (System.IO.Directory.Exists(vm.DefaultDownloadDirectory))
                    dlg.SelectedPath = vm.DefaultDownloadDirectory;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    vm.DefaultDownloadDirectory = dlg.SelectedPath;
            }
            catch { }
        }

        private static void SelectValue(System.Windows.Controls.ComboBox box, int value)
        {
            foreach (var item in box.Items)
            {
                if (item is ComboBoxItem ci && int.TryParse(ci.Content?.ToString(), out int n) && n == value)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.Items.Count > 0 && box.SelectedItem == null)
                box.SelectedIndex = 0;
        }

        private static int ReadValue(System.Windows.Controls.ComboBox box, int fallback)
        {
            if (box.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Content?.ToString(), out int n))
                return n;
            return fallback;
        }

        private void ActiveBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox box && Vm is { } vm)
            { _syncing = true; try { SelectValue(box, vm.ActiveDownloadLimit); } finally { _syncing = false; } }
        }

        private void ActiveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm is not { } vm) return;
            if (sender is System.Windows.Controls.ComboBox box) vm.ActiveDownloadLimit = ReadValue(box, vm.ActiveDownloadLimit);
        }

        private void ConnsBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox box && Vm is { } vm)
            { _syncing = true; try { SelectValue(box, vm.ConnectionsPerFileDefault); } finally { _syncing = false; } }
        }

        private void ConnsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm is not { } vm) return;
            if (sender is System.Windows.Controls.ComboBox box) vm.ConnectionsPerFileDefault = ReadValue(box, vm.ConnectionsPerFileDefault);
        }

        private void RefreshBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox box && Vm is { } vm)
            { _syncing = true; try { SelectValue(box, vm.UiRefreshMs); } finally { _syncing = false; } }
        }

        private void RefreshBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm is not { } vm) return;
            if (sender is System.Windows.Controls.ComboBox box) vm.UiRefreshMs = ReadValue(box, vm.UiRefreshMs);
        }

        private void GlobalBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox box && Vm is { } vm)
            { _syncing = true; try { SelectValue(box, vm.GlobalConnectionBudget); } finally { _syncing = false; } }
        }

        private void GlobalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm is not { } vm) return;
            if (sender is System.Windows.Controls.ComboBox box) vm.GlobalConnectionBudget = ReadValue(box, vm.GlobalConnectionBudget);
        }

        private void HostBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox box && Vm is { } vm)
            { _syncing = true; try { SelectValue(box, vm.PerHostConnectionBudget); } finally { _syncing = false; } }
        }

        private void HostBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm is not { } vm) return;
            if (sender is System.Windows.Controls.ComboBox box) vm.PerHostConnectionBudget = ReadValue(box, vm.PerHostConnectionBudget);
        }
    }
}
