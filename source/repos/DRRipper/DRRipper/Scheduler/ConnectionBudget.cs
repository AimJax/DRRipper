using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Scheduler network permit gate (Ticket #005 §13). Implementations grant
    /// permission to start one HTTP transfer operation. Permits must be released
    /// reliably on success/retry/cancel/pause/failure via Dispose.
    /// Never held across backoff, checkpointing, finalization, or parking.
    /// </summary>
    public interface INetworkPermitGate
    {
        Task<IDisposable> AcquireAsync(string hostKey, CancellationToken ct);
    }

    /// <summary>
    /// Centralized global + per-host transfer budget allocator (Ticket #005.1 §6A).
    ///
    /// A request NEVER holds one scarce budget while waiting for the other:
    /// waiters sit in a single global FIFO queue and a grant is issued only when
    /// BOTH global capacity and the waiter's host capacity are free, atomically,
    /// under one short lock. No network I/O ever happens under that lock, and no
    /// background task exists per waiter.
    ///
    /// Fairness: the grant pass scans FIFO order and grants the first ELIGIBLE
    /// waiter, so a saturated host's queued waiters never block another host's
    /// eligible waiters behind them (no head-of-line blocking), while same-host
    /// order stays FIFO.
    ///
    /// Host entries are STABLE for the allocator lifetime (Ticket #005.1 §4):
    /// never removed, so no stale-reference pool can bypass the per-host cap.
    /// Only active transfers create entries; the count stays small.
    /// </summary>
    public sealed class ConnectionBudget : IDisposable, INetworkPermitGate
    {
        private readonly int _globalBudget;
        private readonly int _perHostBudget;
        private readonly object _lock = new();
        private int _globalActive;
        private int _maxGlobalObserved;
        private long _sequence;
        private bool _disposed;
        private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<Waiter> _queue = new();

        private sealed class HostState
        {
            public int Active;
            public int Pending;
            public int MaxObserved;
        }

        private sealed class Waiter
        {
            public string HostKey = string.Empty;
            public long Seq;
            public readonly TaskCompletionSource<BudgetPermit> Tcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration Registration;
            public LinkedListNode<Waiter>? Node;
            public bool Cancelled;
            public bool Granted;
        }

        /// <summary>Maximum concurrent global permits ever observed (tests/diagnostics).</summary>
        public int MaxGlobalObserved => Volatile.Read(ref _maxGlobalObserved);
        /// <summary>Currently held global permits (tests/diagnostics).</summary>
        public int CurrentGlobalUsed => Volatile.Read(ref _currentGlobalUsed);
        private int _currentGlobalUsed;

        /// <summary>Currently held permits for a host (tests/diagnostics).</summary>
        public int CurrentHostUsed(string hostKey)
        {
            lock (_lock)
            {
                return _hosts.TryGetValue(hostKey, out var hs) ? hs.Active : 0;
            }
        }

        /// <summary>Maximum concurrent permits ever observed for a host (tests/diagnostics).</summary>
        public int MaxHostObserved(string hostKey)
        {
            lock (_lock)
            {
                return _hosts.TryGetValue(hostKey, out var hs) ? hs.MaxObserved : 0;
            }
        }

        /// <summary>Number of retained host entries (tests/diagnostics; stable, never removed).</summary>
        public int HostStateCount
        {
            get { lock (_lock) return _hosts.Count; }
        }

        /// <summary>Number of currently queued (not yet granted) waiters (tests/diagnostics).</summary>
        public int PendingWaiterCount
        {
            get { lock (_lock) return _queue.Count; }
        }

        public ConnectionBudget(int globalBudget, int perHostBudget)
        {
            if (globalBudget <= 0) throw new ArgumentOutOfRangeException(nameof(globalBudget));
            if (perHostBudget <= 0) throw new ArgumentOutOfRangeException(nameof(perHostBudget));
            if (perHostBudget > globalBudget) throw new ArgumentException("Per-host budget cannot exceed global budget.");

            _globalBudget = globalBudget;
            _perHostBudget = perHostBudget;
        }

        /// <summary>
        /// Acquires one global AND one per-host permit, granted atomically only when
        /// both capacities are free. Queues FIFO while waiting; cancellation removes
        /// the waiter cleanly without consuming capacity.
        /// </summary>
        public Task<BudgetPermit> AcquireAsync(string hostKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(hostKey))
                throw new ArgumentException("Host key cannot be empty.", nameof(hostKey));

            Waiter waiter;
            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ConnectionBudget));
                ct.ThrowIfCancellationRequested();

                if (!_hosts.TryGetValue(hostKey, out var hs))
                {
                    hs = new HostState();
                    _hosts[hostKey] = hs;
                }

                waiter = new Waiter { HostKey = hostKey, Seq = ++_sequence };
                waiter.Node = _queue.AddLast(waiter);
                hs.Pending++;

                try
                {
                    waiter.Registration = ct.Register(OnWaiterCancelled, waiter);
                }
                catch
                {
                    // Registration itself failed (e.g. disposed CTS): unwind cleanly.
                    _queue.Remove(waiter.Node);
                    hs.Pending--;
                    throw;
                }

                PumpLocked();
            }
            return waiter.Tcs.Task;
        }

        async Task<IDisposable> INetworkPermitGate.AcquireAsync(string hostKey, CancellationToken ct)
        {
            return await AcquireAsync(hostKey, ct).ConfigureAwait(false);
        }

        private void OnWaiterCancelled(object? state)
        {
            var waiter = (Waiter)state!;
            lock (_lock)
            {
                // Grant already issued: the owner holds a valid permit
                // (SemaphoreSlim wait-completed semantics); leave it alone.
                if (waiter.Granted) return;
                if (waiter.Cancelled) return;
                waiter.Cancelled = true;
                if (waiter.Node != null)
                {
                    _queue.Remove(waiter.Node);
                    waiter.Node = null;
                }
                if (_hosts.TryGetValue(waiter.HostKey, out var hs) && hs.Pending > 0)
                    hs.Pending--;
                try { waiter.Registration.Dispose(); } catch { }
                waiter.Tcs.TrySetCanceled();
            }
        }

        /// <summary>Grant pass: issue permits to FIFO-eligible waiters while capacity allows. Lock held.</summary>
        private void PumpLocked()
        {
            while (_globalActive < _globalBudget)
            {
                LinkedListNode<Waiter>? candidate = null;
                for (var node = _queue.First; node != null; node = node.Next)
                {
                    var w = node.Value;
                    if (w.Cancelled) continue;
                    if (!_hosts.TryGetValue(w.HostKey, out var hs) || hs.Active >= _perHostBudget)
                        continue; // saturated host: must not block others behind it
                    candidate = node;
                    break;
                }
                if (candidate == null) break;

                var waiter = candidate.Value;
                _queue.Remove(candidate);
                waiter.Node = null;
                var host = _hosts[waiter.HostKey];
                host.Pending--;
                _globalActive++;
                host.Active++;

                var g = Interlocked.Increment(ref _currentGlobalUsed);
                int prev;
                do { prev = Volatile.Read(ref _maxGlobalObserved); }
                while (g > prev && Interlocked.CompareExchange(ref _maxGlobalObserved, g, prev) != prev);
                if (host.Active > host.MaxObserved) host.MaxObserved = host.Active;

                waiter.Granted = true;
                // RunContinuationsAsynchronously: safe to complete under the lock.
                waiter.Tcs.TrySetResult(new BudgetPermit(this, waiter.HostKey));
            }
        }

        private void ReleasePermit(string hostKey)
        {
            lock (_lock)
            {
                if (_globalActive > 0) _globalActive--;
                var g = Interlocked.Decrement(ref _currentGlobalUsed);
                if (g < 0) Interlocked.Exchange(ref _currentGlobalUsed, 0);
                if (_hosts.TryGetValue(hostKey, out var hs) && hs.Active > 0)
                    hs.Active--;
                if (!_disposed)
                    PumpLocked();
            }
        }

        /// <summary>
        /// Returns the current available global permits (for diagnostics).
        /// </summary>
        public int AvailableGlobal
        {
            get { lock (_lock) return _globalBudget - _globalActive; }
        }

        /// <summary>
        /// Returns the current available permits for a host (for diagnostics).
        /// </summary>
        public int AvailableForHost(string hostKey)
        {
            lock (_lock)
            {
                if (_hosts.TryGetValue(hostKey, out var hs))
                    return _perHostBudget - hs.Active;
                return _perHostBudget;
            }
        }

        /// <summary>
        /// Normalizes a URL to a host key for budgeting purposes.
        /// Format: scheme://host:port (default ports normalized).
        /// </summary>
        public static string NormalizeHostKey(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "unknown";

            var host = uri.Host;
            var scheme = uri.Scheme;
            var port = uri.Port;

            // Normalize default ports
            if ((scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && port == 80) ||
                (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && port == 443))
            {
                port = -1; // default port, omit
            }

            // IPv6 normalization
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                host = "[" + ip.ToString() + "]";
            }

            return port > 0 ? $"{scheme}://{host}:{port}" : $"{scheme}://{host}";
        }

        public void Dispose()
        {
            List<Waiter> pending;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                pending = new List<Waiter>(_queue.Count);
                foreach (var w in _queue)
                {
                    w.Cancelled = true;
                    w.Node = null;
                    try { w.Registration.Dispose(); } catch { }
                    pending.Add(w);
                }
                _queue.Clear();
                foreach (var hs in _hosts.Values) hs.Pending = 0;
            }
            // Outside the lock (RCA continuations make this safe regardless).
            // Permits granted earlier stay valid; their Dispose releases normally.
            foreach (var w in pending)
            {
                try { w.Tcs.TrySetException(new ObjectDisposedException(nameof(ConnectionBudget))); } catch { }
            }
        }

        /// <summary>
        /// A permit that holds both global and per-host budget slots.
        /// Disposing releases both permits automatically; idempotent (exactly-once).
        /// Safe to dispose after the budget itself was disposed.
        /// </summary>
        public sealed class BudgetPermit : IDisposable
        {
            private readonly ConnectionBudget _budget;
            private readonly string _hostKey;
            private int _released;

            internal BudgetPermit(ConnectionBudget budget, string hostKey)
            {
                _budget = budget;
                _hostKey = hostKey;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    try { _budget.ReleasePermit(_hostKey); } catch { }
                }
            }
        }
    }
}
