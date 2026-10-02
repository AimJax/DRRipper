using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #006.1 context-menu binding tests (T-UI-CONTEXT-01..03).
    /// Real MainWindow + real DataGrid ContextMenu on STA (never shown, no render).
    /// Asserts every menu command binding resolves against MainViewModel —
    /// a popup-tree DataContext break would leave them null.
    /// </summary>
    public sealed class SchedulerContextMenuTests
    {
        private static readonly string[] ExpectedCommands =
        {
            "PauseSelectedCommand", "ResumeSelectedCommand", "CancelSelectedCommand",
            "RetrySelectedCommand", "RemoveSelectedCommand",
            "OpenFileCommand", "OpenFolderCommand", "CopyUrlCommand", "CopyPathCommand",
            "MoveUpCommand", "MoveDownCommand", "MoveTopCommand", "MoveBottomCommand",
        };

        private sealed class MenuOutcome
        {
            public Exception? Fault;
            public object? MenuDataContext;
            public bool IsViewModel;
            public int ItemCount;
            public readonly List<string> Resolved = new();
            public readonly List<string> Missing = new();
        }

        private static MenuOutcome InspectMenu()
        {
            var outcome = new MenuOutcome();
            var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    if (System.Windows.Application.Current == null)
                    {
                        var app = new App();
                        app.InitializeComponent();
                    }
                    var window = new MainWindow();
                    var grid = window.QueueGrid;
                    Assert.NotNull(grid);
                    // Bind a real (uninitialized) MainViewModel, as Loaded does in prod.
                    // No scheduler start: command resolution needs no running queue.
                    // STA thread has no sync context issues: store creation is pure IO.
                    var dir = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "DRRipperCtxMenu",
                        Guid.NewGuid().ToString("N"));
                    System.IO.Directory.CreateDirectory(dir);
                    Scheduler.JobStore? store = null;
                    Scheduler.DownloadScheduler? scheduler = null;
                    UI.MainViewModel? vm = null;
                    try
                    {
                        store = Scheduler.JobStore.CreateAsync(
                            System.IO.Path.Combine(dir, "queue.db")).GetAwaiter().GetResult();
                        scheduler = new Scheduler.DownloadScheduler(
                            store, Scheduler.SchedulerSettings.Default);
                        vm = new UI.MainViewModel(
                            scheduler, UI.ImmediateDispatcher.Instance);
                        window.DataContext = vm;
                    }
                    catch (Exception ex)
                    {
                        try { vm?.Dispose(); } catch { }
                        try { scheduler?.Dispose(); } catch { }
                        try { store?.Dispose(); } catch { }
                        try { System.IO.Directory.Delete(dir, true); } catch { }
                        throw new InvalidOperationException("Test composition failed: " + ex.Message, ex);
                    }
                    var menu = grid.ContextMenu;
                    Assert.NotNull(menu);
                    // Simulate what WPF does on open: the PlacementTarget link.
                    menu.PlacementTarget = grid;
                    // Force the DataContext binding to evaluate.
                    var dc = menu.DataContext;
                    outcome.MenuDataContext = dc;
                    outcome.IsViewModel = dc is UI.MainViewModel;
                    outcome.ItemCount = menu.Items.Count;
                    // Inherited DataContext propagates to item bindings via the
                    // dispatcher queue: pump once (as opening the popup would).
                    grid.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    foreach (var item in menu.Items.OfType<MenuItem>())
                    {
                        if (item.Command == null)
                        {
                            outcome.Missing.Add(item.Header?.ToString() ?? "(separator?)");
                            continue;
                        }
                        // Map the resolved command back to a VM command name.
                        var bound = dc as UI.MainViewModel;
                        string name = bound != null ? ResolveName(bound, item.Command) : item.Command.GetType().Name;
                        outcome.Resolved.Add(name);
                    }
                    try { vm?.Dispose(); } catch { }
                    try { scheduler?.Dispose(); } catch { }
                    try { store?.Dispose(); } catch { }
                    try { System.IO.Directory.Delete(dir, true); } catch { }
                }
                catch (Exception ex) { outcome.Fault = ex; }
                finally { done.Set(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!done.Wait(TimeSpan.FromSeconds(60)))
                throw new Xunit.Sdk.XunitException("STA menu inspection timed out.");
            if (outcome.Fault != null)
                throw new Xunit.Sdk.XunitException("STA fault: " + outcome.Fault.GetType().Name + ": " + outcome.Fault.Message);
            return outcome;
        }

        private static string ResolveName(UI.MainViewModel vm, System.Windows.Input.ICommand command)
        {
            var props = typeof(UI.MainViewModel).GetProperties();
            foreach (var p in props)
            {
                if (!typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType)) continue;
                try
                {
                    if (ReferenceEquals(p.GetValue(vm), command)) return p.Name;
                }
                catch { }
            }
            return "?" + command.GetType().Name;
        }

        [Fact] // T-UI-CONTEXT-01: menu DataContext resolves to MainViewModel.
        public void T_UI_CONTEXT_01_DataContext_Resolves()
        {
            var outcome = InspectMenu();
            Assert.True(outcome.IsViewModel,
                $"ContextMenu DataContext was {outcome.MenuDataContext?.GetType().FullName ?? "null"}, expected MainViewModel.");
        }

        [Fact] // T-UI-CONTEXT-02: Pause/Resume/Cancel/Retry/Remove resolve.
        public void T_UI_CONTEXT_02_Core_Commands_Resolve()
        {
            var outcome = InspectMenu();
            Assert.True(outcome.IsViewModel, "Menu is not bound to MainViewModel.");
            Assert.True(outcome.ItemCount > 0, $"ContextMenu has no items (count={outcome.ItemCount}).");
            foreach (var name in new[] { "PauseSelectedCommand", "ResumeSelectedCommand", "CancelSelectedCommand", "RetrySelectedCommand", "RemoveSelectedCommand" })
                Assert.Contains(name, outcome.Resolved);
            Assert.Empty(outcome.Missing);
        }

        [Fact] // T-UI-CONTEXT-03: open/copy/move bindings resolve.
        public void T_UI_CONTEXT_03_Open_Copy_Move_Resolve()
        {
            var outcome = InspectMenu();
            Assert.True(outcome.IsViewModel, "Menu is not bound to MainViewModel.");
            foreach (var name in ExpectedCommands)
                Assert.Contains(name, outcome.Resolved);
            Assert.Empty(outcome.Missing);
        }
    }
}
