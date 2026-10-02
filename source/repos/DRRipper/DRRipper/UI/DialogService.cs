using System;

namespace DRRipper.UI
{
    /// <summary>
    /// Window-showing behind a delegate-friendly seam (Ticket #006). The WPF
    /// implementation lives in MainWindow code-behind; view-models only see
    /// Func delegates, so tests stub them. No business logic here.
    /// </summary>
    public interface IDialogService
    {
        bool? ShowAddDownloads(AddDownloadsViewModel vm);
        bool? ShowSettings(SettingsViewModel vm);
    }

    /// <summary>Null implementation for headless composition (dialogs unavailable).</summary>
    public sealed class NullDialogService : IDialogService
    {
        public bool? ShowAddDownloads(AddDownloadsViewModel vm) => null;
        public bool? ShowSettings(SettingsViewModel vm) => null;
    }
}
