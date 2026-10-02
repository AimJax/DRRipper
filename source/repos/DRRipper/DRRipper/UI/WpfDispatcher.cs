using System;
using System.Windows.Threading;

namespace DRRipper.UI
{
    /// <summary>
    /// Production UI dispatcher (Ticket #006). The ONLY WPF-touching piece of the
    /// presentation layer outside views: posts scheduler callbacks to the main
    /// window's Dispatcher. Engine, scheduler, and view-models never reference it.
    /// </summary>
    public sealed class WpfDispatcher : IUiDispatcher
    {
        private readonly Dispatcher _dispatcher;

        public WpfDispatcher(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public void Post(Action action)
        {
            try { _dispatcher.BeginInvoke(action); } catch { }
        }

        public bool IsCurrentThread => _dispatcher.CheckAccess();
    }
}
