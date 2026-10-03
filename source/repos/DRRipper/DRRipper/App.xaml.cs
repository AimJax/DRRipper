using System.Windows;
using DRRipper.Scheduler;

namespace DRRipper
{
    /// <summary>
    /// Interaction logic for App.xaml (Ticket #007 §29/§30): parses explicit
    /// launch flags and enforces single-instance before any window appears.
    /// Constructor stays trivial so headless/STA tests can build App freely.
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static SingleInstanceGuard? _instanceGuard;

        /// <summary>Parsed launch flags for this process (defaults when unparsed).</summary>
        public static AppLaunchOptions LaunchOptions { get; private set; } = new();

        /// <summary>Test seam: resets parsed options between hosted runs.</summary>
        internal static void ResetLaunchOptionsForTests() => LaunchOptions = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            try { LaunchOptions = AppLaunchOptions.Parse(e?.Args ?? Array.Empty<string>()); } catch { }
            try
            {
                var guard = new SingleInstanceGuard();
                if (!guard.TryAcquire())
                {
                    // Another desktop process owns the queue: wake its bridge
                    // (best-effort) and exit without a second window/queue.
                    try { guard.Dispose(); } catch { }
                    _ = SingleInstanceGuard.PingBridgeAsync(
                        BrowserBridgeService.DefaultPipeName, TimeSpan.FromSeconds(3));
                    Shutdown();
                    return;
                }
                _instanceGuard = guard;
            }
            catch { }
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _instanceGuard?.Dispose(); } catch { }
            _instanceGuard = null;
            base.OnExit(e);
        }
    }
}
