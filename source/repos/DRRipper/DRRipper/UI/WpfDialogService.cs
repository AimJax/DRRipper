using System;
using System.Windows;
using DRRipper.Views;

namespace DRRipper.UI
{
    /// <summary>
    /// Production dialog service (Ticket #006): thin window hosting over the
    /// testable dialog view-models. No business logic — OK/Cancel plumbing only.
    /// </summary>
    public sealed class WpfDialogService : IDialogService
    {
        private readonly Window _owner;

        public WpfDialogService(Window owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public bool? ShowAddDownloads(AddDownloadsViewModel vm)
        {
            try
            {
                var dlg = new AddDownloadsDialog { Owner = _owner, DataContext = vm };
                return dlg.ShowDialog();
            }
            catch { return null; }
        }

        public bool? ShowSettings(SettingsViewModel vm)
        {
            try
            {
                var dlg = new SettingsDialog { Owner = _owner, DataContext = vm };
                return dlg.ShowDialog();
            }
            catch { return null; }
        }
    }
}
