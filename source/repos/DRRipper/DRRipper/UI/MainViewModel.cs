using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DRRipper.Scheduler;

namespace DRRipper.UI
{
    /// <summary>Queue filter buckets (Ticket #006 §11). Interrupted waits with Queued.</summary>
    public enum JobFilter { All, Active, Queued, Paused, Completed, Failed, Cancelled }

    /// <summary>Sortable columns (Ticket #006 §13). Visual only — never mutates QueuePosition.</summary>
    public enum JobSortColumn { Name, Status, Progress, Size, Speed, Added, Host }

    /// <summary>
    /// Queue presentation root (Ticket #006). Owns no transfer/DB/file logic: every
    /// mutation goes through <see cref="DownloadScheduler"/>; every scheduler event
    /// arrives via <see cref="IUiDispatcher"/>. Single refresh timer + single search
    /// debouncer (no per-row timers); progress events update rows O(1) without
    /// rebuilding the visible list.
    /// </summary>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly DownloadScheduler _scheduler;
        private readonly IUiDispatcher _dispatcher;
        private readonly IShellService _shell;
        private readonly IClipboardService _clipboard;
        private readonly Func<AddDownloadsViewModel, bool?> _showAddDialog;
        private readonly Func<SettingsViewModel, bool?> _showSettingsDialog;

        private readonly Dictionary<Guid, DownloadJobViewModel> _master = new();
        private readonly HashSet<Guid> _selectedIds = new();
        private readonly System.Threading.Timer _refreshTimer;
        private readonly Debouncer _searchDebouncer;
        private readonly object _viewLock = new();
        private bool _disposed;
        private bool _initialized;

        private ObservableCollection<DownloadJobViewModel> _visibleJobs = new();
        private DateTimeOffset _lastRebuildUtc = DateTimeOffset.MinValue;
        private bool _viewDirty;
        private JobFilter _filter = JobFilter.All;
        private string _searchText = string.Empty;
        private string _pendingSearch = string.Empty;
        private JobSortColumn _sortColumn = JobSortColumn.Added;
        private bool _sortDescending;
        private string _statusText = string.Empty;
        private string _startupError = string.Empty;
        private bool _isShuttingDown;
        private double _aggregateMBps;
        private int _totalJobs, _activeJobs, _queuedJobs, _completedJobs, _failedJobs;
        private string _permitsText = string.Empty;
        private readonly List<string> _lastBulkErrors = new();

        public MainViewModel(
            DownloadScheduler scheduler,
            IUiDispatcher dispatcher,
            IShellService? shell = null,
            IClipboardService? clipboard = null,
            Func<AddDownloadsViewModel, bool?>? showAddDialog = null,
            Func<SettingsViewModel, bool?>? showSettingsDialog = null,
            int refreshMs = 1000,
            int searchDebounceMs = 250)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _shell = shell ?? new ShellService();
            _clipboard = clipboard ?? new ClipboardService();
            _showAddDialog = showAddDialog ?? (_ => null);
            _showSettingsDialog = showSettingsDialog ?? (_ => null);

            VisibleJobs = new ObservableCollection<DownloadJobViewModel>();
            _refreshTimer = new System.Threading.Timer(_ => _dispatcher.Post(UpdateSummaries), null, Timeout.Infinite, Timeout.Infinite);
            RefreshIntervalMs = Math.Clamp(refreshMs, 250, 5000);
            _searchDebouncer = new Debouncer(ApplyPendingSearch, Math.Max(0, searchDebounceMs));

            AddCommand = new AsyncRelayCommand(ShowAddDialogAsync);
            PauseSelectedCommand = new AsyncRelayCommand(_ => BulkAsync("Pause", _scheduler.PauseJobAsync), _ => SelectedIds.Any() && SelectionHas(s => s.CanPause));
            ResumeSelectedCommand = new AsyncRelayCommand(_ => BulkAsync("Resume", ResumeOneAsync), _ => SelectedIds.Any() && SelectionHas(s => s.CanResume));
            TogglePauseResumeCommand = new AsyncRelayCommand(_ => TogglePauseResumeAsync(), _ => SelectedIds.Any() && SelectionHas(s => s.CanPause || s.CanResume));
            CancelSelectedCommand = new AsyncRelayCommand(_ => BulkAsync("Cancel", _scheduler.CancelJobAsync), _ => SelectedIds.Any() && SelectionHas(s => s.CanCancel));
            RetrySelectedCommand = new AsyncRelayCommand(_ => BulkAsync("Retry", _scheduler.RetryJobAsync), _ => SelectedIds.Any() && SelectionHas(s => s.CanRetry));
            RemoveSelectedCommand = new AsyncRelayCommand(_ => BulkAsync("Remove", _scheduler.RemoveJobAsync), _ => SelectedIds.Any());
            PauseAllCommand = new AsyncRelayCommand(() => _scheduler.PauseAllAsync());
            ResumeAllCommand = new AsyncRelayCommand(() => _scheduler.ResumeAllAsync());
            ClearDoneCommand = new AsyncRelayCommand(() => _scheduler.ClearCompletedAsync());
            RefreshCommand = new RelayCommand(() => RebuildView());
            FocusSearchCommand = new RelayCommand(() => { try { FocusSearchRequested?.Invoke(); } catch { } });
            MoveUpCommand = new AsyncRelayCommand(_ => MoveSelectedAsync(MoveKind.Up), _ => CanMove());
            MoveDownCommand = new AsyncRelayCommand(_ => MoveSelectedAsync(MoveKind.Down), _ => CanMove());
            MoveTopCommand = new AsyncRelayCommand(_ => MoveSelectedAsync(MoveKind.Top), _ => CanMove());
            MoveBottomCommand = new AsyncRelayCommand(_ => MoveSelectedAsync(MoveKind.Bottom), _ => CanMove());
            OpenFileCommand = new RelayCommand(_ => OpenSelectedFile(), _ => CanOpenFile());
            OpenFolderCommand = new RelayCommand(_ => OpenSelectedFolder(), _ => SelectedIds.Count == 1);
            CopyUrlCommand = new RelayCommand(_ => CopySelectedUrl(), _ => SelectedIds.Count == 1);
            CopyPathCommand = new RelayCommand(_ => CopySelectedPath(), _ => SelectedIds.Count == 1);
        }

        public event Action? FocusSearchRequested;

        public ObservableCollection<DownloadJobViewModel> VisibleJobs
        {
            get => _visibleJobs;
            private set => Set(ref _visibleJobs, value);
        }

        public IReadOnlySet<Guid> SelectedIds => _selectedIds;
        public int RefreshIntervalMs { get; private set; }
        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
        public string StartupError { get => _startupError; private set => Set(ref _startupError, value); }
        public bool IsShuttingDown { get => _isShuttingDown; private set => Set(ref _isShuttingDown, value); }
        public double AggregateMBps { get => _aggregateMBps; private set { if (Set(ref _aggregateMBps, value)) Raise(nameof(AggregateText)); } }
        public string AggregateText => "Aggregate " + Formatting.FormatRate(AggregateMBps * 1024.0 * 1024.0);
        public int TotalJobs { get => _totalJobs; private set => Set(ref _totalJobs, value); }
        public int ActiveJobs { get => _activeJobs; private set { if (Set(ref _activeJobs, value)) Raise(nameof(HasActiveTransfers)); } }
        public int QueuedJobs { get => _queuedJobs; private set => Set(ref _queuedJobs, value); }
        /// <summary>Close-safety signal for the window close controller (Ticket #006.1 §6).</summary>
        public bool HasActiveTransfers => ActiveJobs > 0;
        public int CompletedJobs { get => _completedJobs; private set => Set(ref _completedJobs, value); }
        public int FailedJobs { get => _failedJobs; private set => Set(ref _failedJobs, value); }
        public string PermitsText { get => _permitsText; private set => Set(ref _permitsText, value); }
        public IReadOnlyList<string> LastBulkErrors => _lastBulkErrors;

        public JobFilter Filter { get => _filter; set { if (Set(ref _filter, value)) RebuildView(); } }
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value ?? string.Empty))
                {
                    _pendingSearch = _searchText;
                    _searchDebouncer.Trigger();
                }
            }
        }
        public JobSortColumn SortColumn { get => _sortColumn; set { if (Set(ref _sortColumn, value)) RebuildView(); } }
        public bool SortDescending { get => _sortDescending; set { if (Set(ref _sortDescending, value)) RebuildView(); } }

        public ICommand AddCommand { get; }
        public ICommand PauseSelectedCommand { get; }
        public ICommand ResumeSelectedCommand { get; }
        public ICommand TogglePauseResumeCommand { get; }
        public ICommand CancelSelectedCommand { get; }
        public ICommand RetrySelectedCommand { get; }
        public ICommand RemoveSelectedCommand { get; }
        public ICommand PauseAllCommand { get; }
        public ICommand ResumeAllCommand { get; }
        public ICommand ClearDoneCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand FocusSearchCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand MoveTopCommand { get; }
        public ICommand MoveBottomCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand CopyUrlCommand { get; }
        public ICommand CopyPathCommand { get; }

        public AppSettings Settings { get; private set; } = AppSettings.Defaults();

        public void ApplySettings(AppSettings settings, int refreshMs)
        {
            Settings = settings ?? AppSettings.Defaults();
            RefreshIntervalMs = Math.Clamp(refreshMs, 250, 5000);
            try { _refreshTimer.Change(RefreshIntervalMs, RefreshIntervalMs); } catch { }
        }

        // ---------- lifecycle ----------

        public async Task InitializeAsync(CancellationToken ct = default)
            => await InitializeAsync(startScheduler: true, ct).ConfigureAwait(false);

        /// <summary>
        /// Initializes the view-model. When <paramref name="startScheduler"/> is false,
        /// the scheduler loop is left stopped (pure view/model tests with
        /// researcher-supplied snapshots); otherwise the queue is started and loaded.
        /// </summary>
        public async Task InitializeAsync(bool startScheduler, CancellationToken ct = default)
        {
            Attach();
            if (startScheduler)
            {
                try
                {
                    await _scheduler.StartAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    StartupError = "Queue failed to start: " + Formatting.FormatError(ex.Message);
                    StatusText = StartupError;
                    return;
                }
            }
            List<DownloadJob> all;
            try { all = await _scheduler.GetAllJobsAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                StartupError = "Queue failed to load: " + Formatting.FormatError(ex.Message);
                StatusText = StartupError;
                return;
            }
            var tcs = new TaskCompletionSource();
            _dispatcher.Post(() =>
            {
                try
                {
                    lock (_viewLock)
                    {
                        _master.Clear();
                        foreach (var j in all)
                        {
                            var vm = new DownloadJobViewModel(j.JobId);
                            vm.Apply(j);
                            _master[j.JobId] = vm;
                        }
                    }
                    RebuildView();
                    UpdateSummaries();
                    StatusText = "Queue ready.";
                    _initialized = true;
                }
                catch (Exception ex) { StartupError = Formatting.FormatError(ex.Message); }
                finally { tcs.TrySetResult(); }
            });
            await tcs.Task.ConfigureAwait(false);
            try { _refreshTimer.Change(RefreshIntervalMs, RefreshIntervalMs); } catch { }
        }

        public async Task ShutdownAsync()
        {
            IsShuttingDown = true;
            StatusText = "Saving download state…";
            Detach();
            try { _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try { _searchDebouncer.Dispose(); } catch { }
            try { await _scheduler.StopAsync().ConfigureAwait(false); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
            try { _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try { _refreshTimer.Dispose(); } catch { }
            try { _searchDebouncer.Dispose(); } catch { }
        }

        private bool _attached;
        private void Attach()
        {
            if (_attached) return;
            _attached = true;
            _scheduler.JobAdded += OnJobAdded;
            _scheduler.JobUpdated += OnJobUpdated;
            _scheduler.JobRemoved += OnJobRemoved;
            _scheduler.JobProgress += OnJobProgress;
        }

        private void Detach()
        {
            if (!_attached) return;
            _attached = false;
            try { _scheduler.JobAdded -= OnJobAdded; } catch { }
            try { _scheduler.JobUpdated -= OnJobUpdated; } catch { }
            try { _scheduler.JobRemoved -= OnJobRemoved; } catch { }
            try { _scheduler.JobProgress -= OnJobProgress; } catch { }
        }

        // ---------- scheduler event intake (marshaled, O(1) each) ----------

        private void OnJobAdded(object? s, DownloadJob job) => _dispatcher.Post(() => UpsertJob(job));
        private void OnJobUpdated(object? s, DownloadJob job) => _dispatcher.Post(() => UpsertJob(job));
        private void OnJobRemoved(object? s, Guid jobId) => _dispatcher.Post(() => RemoveJob(jobId));
        private void OnJobProgress(object? s, JobProgressEvent p) => _dispatcher.Post(() =>
        {
            DownloadJobViewModel? vm;
            lock (_viewLock) _master.TryGetValue(p.JobId, out vm);
            vm?.ApplyProgress(p.CompletedBytes, p.TotalBytes > 0 ? p.TotalBytes : null, DateTimeOffset.UtcNow);
        });

        internal void UpsertJob(DownloadJob job)
        {
            bool membershipMayChange;
            DownloadJobViewModel vm;
            lock (_viewLock)
            {
                if (!_master.TryGetValue(job.JobId, out vm!))
                {
                    vm = new DownloadJobViewModel(job.JobId);
                    _master[job.JobId] = vm;
                    vm.Apply(job);
                    membershipMayChange = true; // new row: (coalesced) rebuild to insert sorted
                }
                else
                {
                    bool before = PassesFilterState(vm.State) && PassesSearchText(vm);
                    vm.Apply(job);
                    bool after = PassesFilterState(vm.State) && PassesSearchText(vm);
                    membershipMayChange = before != after;
                }
            }
            if (membershipMayChange) RequestRebuild();
            RaiseCanExecute();
        }

        private void RemoveJob(Guid jobId)
        {
            DownloadJobViewModel? removed = null;
            lock (_viewLock)
            {
                if (_master.TryGetValue(jobId, out removed)) _master.Remove(jobId);
                _selectedIds.Remove(jobId);
            }
            if (removed != null)
            {
                VisibleJobs.Remove(removed);
                UpdateSummaries();
                RaiseCanExecute();
            }
        }

        // ---------- view projection (filter/search/sort) ----------

        private bool PassesFilter(DownloadJobViewModel vm) => PassesFilterState(vm.State);

        private bool PassesFilterState(JobState state)
        {
            return _filter switch
            {
                JobFilter.All => true,
                JobFilter.Active => state == JobState.Downloading || state == JobState.Pausing || state == JobState.Retrying,
                JobFilter.Queued => state == JobState.Queued || state == JobState.Interrupted,
                JobFilter.Paused => state == JobState.Paused,
                JobFilter.Completed => state == JobState.Completed,
                JobFilter.Failed => state == JobState.Failed,
                JobFilter.Cancelled => state == JobState.Cancelled,
                _ => true,
            };
        }

        private bool PassesSearch(DownloadJobViewModel vm) => PassesSearchText(vm);

        private bool PassesSearchText(DownloadJobViewModel vm)
        {
            string q = _pendingSearch.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return vm.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || vm.Url.Contains(q, StringComparison.OrdinalIgnoreCase)
                || vm.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains(q, StringComparison.OrdinalIgnoreCase)
                || vm.TargetPath.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private IComparer<DownloadJobViewModel> Sorter()
        {
            int dir = _sortDescending ? -1 : 1;
            return Comparer<DownloadJobViewModel>.Create((a, b) =>
            {
                int c = _sortColumn switch
                {
                    JobSortColumn.Name => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase),
                    JobSortColumn.Status => a.State.CompareTo(b.State),
                    JobSortColumn.Progress => a.Progress.CompareTo(b.Progress),
                    JobSortColumn.Size => SingularSize(a).CompareTo(SingularSize(b)),
                    JobSortColumn.Speed => a.SpeedMBps.CompareTo(b.SpeedMBps),
                    JobSortColumn.Added => a.Added.CompareTo(b.Added),
                    JobSortColumn.Host => string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase),
                    _ => 0,
                };
                if (c == 0) c = a.Added.CompareTo(b.Added); // stable tiebreak (never QueuePosition)
                return c * dir;
            });
        }

        private static long SingularSize(DownloadJobViewModel vm) => vm.TotalBytes > 0 ? vm.TotalBytes : long.MaxValue;

        internal void RebuildView()
        {
            List<DownloadJobViewModel> ordered;
            lock (_viewLock)
            {
                ordered = _master.Values.Where(v => PassesFilter(v) && PassesSearch(v)).ToList();
            }
            ordered.Sort(Sorter());
            var next = new ObservableCollection<DownloadJobViewModel>(ordered);
            VisibleJobs = next;
            _lastRebuildUtc = DateTimeOffset.UtcNow;
            _viewDirty = false;
            UpdateSummaries();
            RaiseCanExecute();
        }

        /// <summary>
        /// Event-driven rebuild request, coalesced during bursts (bulk import fires
        /// thousands of JobAdded events): at most one rebuild per 250 ms, the rest
        /// fold into the next refresh tick. Explicit user actions call RebuildView
        /// directly and always take effect immediately.
        /// </summary>
        internal void RequestRebuild()
        {
            if (DateTimeOffset.UtcNow - _lastRebuildUtc >= TimeSpan.FromMilliseconds(250))
            {
                RebuildView();
                return;
            }
            _viewDirty = true;
        }

        private void ApplyPendingSearch() => _dispatcher.Post(() => RebuildView());

        // ---------- selection ----------

        public void SyncSelection(IEnumerable<Guid> ids)
        {
            lock (_viewLock)
            {
                _selectedIds.Clear();
                foreach (var id in ids) _selectedIds.Add(id);
            }
            RaiseCanExecute();
        }

        private bool SelectionHas(Func<DownloadJobViewModel, bool> pred)
        {
            lock (_viewLock)
            {
                foreach (var id in _selectedIds)
                    if (_master.TryGetValue(id, out var vm) && pred(vm)) return true;
            }
            return false;
        }

        private List<DownloadJobViewModel> SelectedVms()
        {
            var list = new List<DownloadJobViewModel>();
            lock (_viewLock)
            {
                foreach (var id in _selectedIds)
                    if (_master.TryGetValue(id, out var vm)) list.Add(vm);
            }
            return list;
        }

        // ---------- commands ----------

        private async Task ShowAddDialogAsync()
        {
            var vm = new AddDownloadsViewModel(
                _scheduler,
                string.IsNullOrWhiteSpace(Settings.DefaultDownloadDirectory) ? null : Settings.DefaultDownloadDirectory,
                Settings.ConnectionsPerFileDefault);
            bool? added;
            try { added = _showAddDialog(vm); }
            catch (Exception ex) { StatusText = "Add Downloads failed: " + Formatting.FormatError(ex.Message); return; }
            if (added != true) return;
            if (vm.StartAfterAdd)
            {
                try { await _scheduler.ResumeAllAsync().ConfigureAwait(false); }
                catch (Exception ex) { StatusText = "Resume failed: " + Formatting.FormatError(ex.Message); return; }
            }
            StatusText = vm.ResultSummary;
        }

        public async Task ShowSettingsAsync(Func<SettingsViewModel, bool?> show, SettingsService service)
        {
            var current = Settings;
            var vm = new SettingsViewModel(current);
            bool? ok;
            try { ok = show(vm); } catch (Exception ex) { StatusText = Formatting.FormatError(ex.Message); return; }
            if (ok != true) return;
            var next = vm.Build();
            bool restartRequired = vm.BudgetsChanged(current);
            try { await service.SaveAsync(next).ConfigureAwait(false); }
            catch (Exception ex) { StatusText = "Settings save failed: " + Formatting.FormatError(ex.Message); return; }
            ApplyLiveSettings(next);
            StatusText = "Settings saved." + (restartRequired ? " Restart DRRipper to apply connection budgets." : string.Empty);
        }

        internal void ApplyLiveSettings(AppSettings next)
        {
            var prev = Settings;
            Settings = next;
            try { _scheduler.Settings.ActiveDownloadLimit = next.ActiveDownloadLimit; } catch { }
            // Mode + per-file ceiling apply to newly started jobs live; the
            // connection-budget allocator itself is restart-scoped (§20).
            try { _scheduler.Settings.TransferMode = next.TransferMode; } catch { }
            try { _scheduler.Settings.MaxConnectionsPerFile = next.MaxConnectionsPerFile; } catch { }
            ApplySettings(next, next.UiRefreshMs);
            _ = prev;
        }

        private Task ResumeOneAsync(Guid id) => _scheduler.ResumeJobAsync(id);

        /// <summary>Space-bar toggle: pauses pausable selection, else resumes resumable selection.</summary>
        private async Task TogglePauseResumeAsync()
        {
            var ids = SelectedIds.ToList();
            if (ids.Count == 0) return;
            bool anyPausable;
            lock (_viewLock)
            {
                anyPausable = ids.Any(id => _master.TryGetValue(id, out var vm) && vm.CanPause);
            }
            if (anyPausable)
                await BulkAsync("Pause", _scheduler.PauseJobAsync).ConfigureAwait(false);
            else
                await BulkAsync("Resume", ResumeOneAsync).ConfigureAwait(false);
        }

        private async Task BulkAsync(string verb, Func<Guid, Task> op)
        {
            var ids = SelectedIds.ToList();
            if (ids.Count == 0) return;
            _lastBulkErrors.Clear();
            int ok = 0;
            foreach (var id in ids)
            {
                try { await op(id).ConfigureAwait(false); ok++; }
                catch (Exception ex) { _lastBulkErrors.Add(ShortId(id) + ": " + Formatting.FormatError(ex.Message)); }
            }
            int failed = ids.Count - ok;
            StatusText = failed == 0
                ? $"{verb}: {ok} job(s) OK."
                : $"{verb}: {ok} OK, {failed} failed — {string.Join("; ", _lastBulkErrors.Take(2))}";
        }

        private static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);

        private bool CanMove()
        {
            if (SelectedIds.Count == 0) return false;
            lock (_viewLock)
            {
                foreach (var id in _selectedIds)
                {
                    if (!_master.TryGetValue(id, out var vm)) return false;
                    if (vm.IsActive || vm.IsTerminal) return false;
                }
            }
            return true;
        }

        private enum MoveKind { Up, Down, Top, Bottom }

        private async Task MoveSelectedAsync(MoveKind kind)
        {
            List<DownloadJob> scoped;
            try { scoped = await _scheduler.GetAllJobsAsync().ConfigureAwait(false); }
            catch (Exception ex) { StatusText = "Reorder failed: " + Formatting.FormatError(ex.Message); return; }
            // Scope: movable (non-active, non-terminal) jobs in execution order.
            var movable = scoped
                .Where(j => !IsActiveState(j.State) && !j.IsTerminal)
                .OrderBy(j => j.Priority).ThenBy(j => j.QueuePosition).ThenBy(j => j.CreatedUtc)
                .Select(j => j.JobId).ToList();
            var selected = new HashSet<Guid>(SelectedIds.Where(id => movable.Contains(id)));
            if (selected.Count == 0) return;

            List<Guid> moved = kind switch
            {
                MoveKind.Top => selected.Concat(movable.Where(id => !selected.Contains(id))).ToList(),
                MoveKind.Bottom => movable.Where(id => !selected.Contains(id)).Concat(selected).ToList(),
                MoveKind.Up => ShiftBlock(movable, selected, -1),
                _ => ShiftBlock(movable, selected, +1),
            };
            // Rebuild the FULL execution order: non-movable jobs keep exact slots.
            var full = scoped.OrderBy(j => j.Priority).ThenBy(j => j.QueuePosition).ThenBy(j => j.CreatedUtc).Select(j => j.JobId).ToList();
            var movedSet = new HashSet<Guid>(moved);
            var result = new List<Guid>(full.Count);
            var moveQueue = new Queue<Guid>(moved);
            foreach (var id in full)
                result.Add(movedSet.Contains(id) ? moveQueue.Dequeue() : id);
            try { await _scheduler.ReorderAsync(result).ConfigureAwait(false); }
            catch (Exception ex) { StatusText = "Reorder failed: " + Formatting.FormatError(ex.Message); return; }
            StatusText = $"Moved {selected.Count} job(s).";
        }

        private static bool IsActiveState(JobState s) =>
            s == JobState.Downloading || s == JobState.Pausing || s == JobState.Retrying;

        /// <summary>Shifts a selected block one step, preserving contiguity.</summary>
        internal static List<Guid> ShiftBlock(List<Guid> order, HashSet<Guid> selected, int dir)
        {
            var result = order.ToList();
            if (dir < 0)
            {
                int first = result.FindIndex(selected.Contains);
                if (first <= 0) return result;
                var moving = result.Where(selected.Contains).ToList();
                result.RemoveAll(selected.Contains);
                result.InsertRange(first - 1, moving);
            }
            else
            {
                int last = result.FindLastIndex(selected.Contains);
                if (last < 0 || last >= result.Count - 1) return result;
                var moving = result.Where(selected.Contains).ToList();
                result.RemoveAll(selected.Contains);
                // After removal, the element that followed the block sits at index `last - moving.Count + 1`;
                // insert after it.
                int insertAt = last - moving.Count + 2;
                if (insertAt > result.Count) insertAt = result.Count;
                result.InsertRange(insertAt, moving);
            }
            return result;
        }

        private bool CanOpenFile()
        {
            var vms = SelectedVms();
            if (vms.Count != 1) return false;
            return _shell.CanOpenFile(vms[0]);
        }

        private void OpenSelectedFile()
        {
            var vms = SelectedVms();
            if (vms.Count != 1) return;
            if (!_shell.TryOpenFile(vms[0], out string? error))
                StatusText = "Open File failed: " + Formatting.FormatError(error);
        }

        private void OpenSelectedFolder()
        {
            var vms = SelectedVms();
            if (vms.Count != 1) return;
            if (!_shell.TryOpenFolder(vms[0], out string? error))
                StatusText = "Open Folder failed: " + Formatting.FormatError(error);
        }

        private void CopySelectedUrl()
        {
            var vms = SelectedVms();
            if (vms.Count != 1) return;
            try { _clipboard.SetText(vms[0].Url); StatusText = "URL copied."; }
            catch (Exception ex) { StatusText = "Copy failed: " + Formatting.FormatError(ex.Message); }
        }

        private void CopySelectedPath()
        {
            var vms = SelectedVms();
            if (vms.Count != 1) return;
            try { _clipboard.SetText(vms[0].TargetPath); StatusText = "Path copied."; }
            catch (Exception ex) { StatusText = "Copy failed: " + Formatting.FormatError(ex.Message); }
        }

        // ---------- coalesced refresh (single timer) ----------

        internal void UpdateSummaries()
        {
            if (_disposed) return;
            if (_viewDirty)
            {
                _viewDirty = false;
                RebuildView();
                return; // RebuildView already refreshed summaries + commands
            }
            int total, active = 0, queued = 0, completed = 0, failed = 0;
            double sum = 0;
            List<DownloadJobViewModel> actives = new();
            lock (_viewLock)
            {
                total = _master.Count;
                foreach (var vm in _master.Values)
                {
                    switch (vm.State)
                    {
                        case JobState.Downloading:
                        case JobState.Pausing:
                        case JobState.Retrying:
                            active++; sum += Math.Max(0.0, vm.SpeedMBps); actives.Add(vm); break;
                        case JobState.Queued:
                        case JobState.Interrupted:
                            queued++; break;
                        case JobState.Completed:
                            completed++; break;
                        case JobState.Failed:
                            failed++; break;
                    }
                }
            }
            TotalJobs = total;
            ActiveJobs = active;
            QueuedJobs = queued;
            CompletedJobs = completed;
            FailedJobs = failed;
            AggregateMBps = AggregateMBps * 0.6 + sum * 0.4;
            foreach (var vm in actives) vm.RefreshDerived();
            try { PermitsText = $"Network {_scheduler.Budget.AvailableGlobal} / {_scheduler.Budget.AvailableGlobal + _scheduler.Budget.CurrentGlobalUsed}"; }
            catch { PermitsText = string.Empty; }
            RaiseCanExecute();
        }

        private void RaiseCanExecute()
        {
            foreach (var cmd in new ICommand[]
            {
                (ICommand)PauseSelectedCommand, ResumeSelectedCommand, TogglePauseResumeCommand,
                CancelSelectedCommand,
                RetrySelectedCommand, RemoveSelectedCommand, MoveUpCommand, MoveDownCommand,
                MoveTopCommand, MoveBottomCommand, OpenFileCommand, OpenFolderCommand,
                CopyUrlCommand, CopyPathCommand,
            })
            {
                try
                {
                    if (cmd is RelayCommand rc) rc.RaiseCanExecuteChanged();
                    else if (cmd is AsyncRelayCommand ac) ac.RaiseCanExecuteChanged();
                }
                catch { }
            }
        }

        /// <summary>Single-flight debouncer (search box). No per-row timers.</summary>
        private sealed class Debouncer : IDisposable
        {
            private readonly Action _action;
            private readonly int _delayMs;
            private System.Threading.Timer? _timer;
            private bool _disposed;

            public Debouncer(Action action, int delayMs)
            {
                _action = action;
                _delayMs = delayMs;
            }

            public void Trigger()
            {
                if (_disposed) return;
                if (_delayMs <= 0) { try { _action(); } catch { } return; }
                try
                {
                    _timer ??= new System.Threading.Timer(_ =>
                    {
                        try { _action(); } catch { }
                    }, null, Timeout.Infinite, Timeout.Infinite);
                    _timer.Change(_delayMs, Timeout.Infinite);
                }
                catch { }
            }

            public void Dispose()
            {
                _disposed = true;
                try { _timer?.Dispose(); } catch { }
            }
        }
    }
}
