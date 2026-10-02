using System;
using System.Threading;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #006 XAML verification (virtualization + wiring).
    /// Instantiates the real MainWindow on an STA thread WITHOUT showing it
    /// (no HWND, no rendering — headless-safe) and asserts the queue grid keeps
    /// WPF row virtualization enabled with recycling. Purely visual scrolling
    /// behavior remains a manual-checklist item (see BASELINE).
    /// </summary>
    public sealed class SchedulerXamlTests
    {
        [Fact] // Virtualized recycling grid with a full job table.
        public void T_UI_XAML_Virtualization_Enabled()
        {
            Exception? fault = null;
            bool virtualizing = false;
            bool recycling = false;
            int columnCount = 0;
            var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    // Application resources (styles) without running the app loop.
                    // NOTE: the generated Main() normally calls InitializeComponent;
                    // a bare new App() does not, so call it explicitly here.
                    if (System.Windows.Application.Current == null)
                    {
                        var app = new App();
                        app.InitializeComponent();
                    }
                    int merged = System.Windows.Application.Current?.Resources.MergedDictionaries.Count ?? -1;
                    bool hasBg = false;
                    try { hasBg = System.Windows.Application.Current?.TryFindResource("BackgroundPrimary") != null; } catch { }
                    if (merged <= 0 || !hasBg)
                        throw new InvalidOperationException($"App resources not loaded (merged={merged}, bg={hasBg}).");
                    var window = new MainWindow();
                    var grid = window.QueueGrid;
                    Assert.NotNull(grid);
                    virtualizing = System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(grid);
                    recycling = System.Windows.Controls.VirtualizingPanel.GetVirtualizationMode(grid)
                        == System.Windows.Controls.VirtualizationMode.Recycling;
                    columnCount = grid.Columns.Count;
                    // Composition happens on Loaded; the ctor must stay side-effect free.
                    Assert.Null(window.DataContext);
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException != null ? " | inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : string.Empty;
                    fault = new Exception(ex.GetType().Name + ": " + ex.Message + inner);
                }
                finally { done.Set(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "STA check timed out.");
            if (fault != null) throw new Xunit.Sdk.XunitException("STA check fault: " + fault.Message);
            Assert.True(virtualizing, "Queue grid must keep UI virtualization enabled.");
            Assert.True(recycling, "Queue grid must use Recycling virtualization mode.");
            Assert.True(columnCount >= 8, $"Expected a full job table, found {columnCount} columns.");
        }
    }
}
