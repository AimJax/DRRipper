using System;

namespace DRRipper.UI
{
    /// <summary>
    /// UI-thread marshaling abstraction (Ticket #006). View-models post all
    /// scheduler-event handling through this; production posts to the WPF
    /// Dispatcher, tests post inline. View-models never touch Dispatcher directly.
    /// </summary>
    public interface IUiDispatcher
    {
        /// <summary>Enqueues work on the UI thread (or runs inline in tests).</summary>
        void Post(Action action);

        /// <summary>True when the caller is already on the UI thread.</summary>
        bool IsCurrentThread { get; }
    }

    /// <summary>Headless/test dispatcher: runs everything inline, synchronously.</summary>
    public sealed class ImmediateDispatcher : IUiDispatcher
    {
        public static readonly ImmediateDispatcher Instance = new();
        public void Post(Action action) => action();
        public bool IsCurrentThread => true;
    }
}
