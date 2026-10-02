using System;

namespace DRRipper.UI
{
    /// <summary>
    /// Settings dialog model (Ticket #006 §18). Edits a copy; Save builds a
    /// normalized <see cref="AppSettings"/>. Budget/DB-path rows are flagged
    /// restart-required (§20); limits/defaults/refresh apply live.
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private int _activeDownloadLimit;
        private int _connectionsPerFileDefault;
        private int _globalConnectionBudget;
        private int _perHostConnectionBudget;
        private string _defaultDownloadDirectory = string.Empty;
        private int _uiRefreshMs;
        private bool _rememberWindowPlacement;
        private CloseButtonBehavior _closeBehavior;
        private bool _minimizeToTray;

        public SettingsViewModel(AppSettings current)
        {
            current ??= AppSettings.Defaults();
            _activeDownloadLimit = current.ActiveDownloadLimit;
            _connectionsPerFileDefault = current.ConnectionsPerFileDefault;
            _globalConnectionBudget = current.GlobalConnectionBudget;
            _perHostConnectionBudget = current.PerHostConnectionBudget;
            _defaultDownloadDirectory = current.DefaultDownloadDirectory;
            _uiRefreshMs = current.UiRefreshMs;
            _rememberWindowPlacement = current.RememberWindowPlacement;
            _closeBehavior = current.CloseBehavior;
            _minimizeToTray = current.MinimizeToTray;
        }

        public int ActiveDownloadLimit { get => _activeDownloadLimit; set => Set(ref _activeDownloadLimit, value); }
        public int ConnectionsPerFileDefault { get => _connectionsPerFileDefault; set => Set(ref _connectionsPerFileDefault, value); }
        public int GlobalConnectionBudget { get => _globalConnectionBudget; set => Set(ref _globalConnectionBudget, value); }
        public int PerHostConnectionBudget { get => _perHostConnectionBudget; set => Set(ref _perHostConnectionBudget, value); }
        public string DefaultDownloadDirectory { get => _defaultDownloadDirectory; set => Set(ref _defaultDownloadDirectory, value ?? string.Empty); }
        public int UiRefreshMs { get => _uiRefreshMs; set => Set(ref _uiRefreshMs, value); }
        public bool RememberWindowPlacement { get => _rememberWindowPlacement; set => Set(ref _rememberWindowPlacement, value); }
        public CloseButtonBehavior CloseBehavior { get => _closeBehavior; set => Set(ref _closeBehavior, value); }
        public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }

        /// <summary>True when a budgeted value differs (restart to take effect).</summary>
        public bool BudgetsChanged(AppSettings original)
        {
            if (original == null) return false;
            return GlobalConnectionBudget != original.GlobalConnectionBudget ||
               PerHostConnectionBudget != original.PerHostConnectionBudget;
        }

        public AppSettings Build()
        {
            var s = new AppSettings
            {
                ActiveDownloadLimit = _activeDownloadLimit,
                ConnectionsPerFileDefault = _connectionsPerFileDefault,
                GlobalConnectionBudget = _globalConnectionBudget,
                PerHostConnectionBudget = _perHostConnectionBudget,
                DefaultDownloadDirectory = _defaultDownloadDirectory,
                UiRefreshMs = _uiRefreshMs,
                RememberWindowPlacement = _rememberWindowPlacement,
                CloseBehavior = Enum.IsDefined(typeof(CloseButtonBehavior), _closeBehavior)
                    ? _closeBehavior : CloseButtonBehavior.MinimizeToTray,
                MinimizeToTray = _minimizeToTray,
            };
            s.Normalize();
            // Reflect normalization back so the dialog shows effective values.
            _activeDownloadLimit = s.ActiveDownloadLimit;
            _connectionsPerFileDefault = s.ConnectionsPerFileDefault;
            _globalConnectionBudget = s.GlobalConnectionBudget;
            _perHostConnectionBudget = s.PerHostConnectionBudget;
            _uiRefreshMs = s.UiRefreshMs;
            Raise(nameof(ActiveDownloadLimit));
            Raise(nameof(ConnectionsPerFileDefault));
            Raise(nameof(GlobalConnectionBudget));
            Raise(nameof(PerHostConnectionBudget));
            Raise(nameof(UiRefreshMs));
            return s;
        }

        public bool IsRestartRequired(AppSettings original) => BudgetsChanged(original);
    }
}
