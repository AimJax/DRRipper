using System;
using System.Collections.Concurrent;
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
    /// Manages global and per-host connection budgets for network transfer operations.
    /// Permits are acquired before starting a network read and released after completion/failure/cancellation.
    /// </summary>
    public sealed class ConnectionBudget : IDisposable, INetworkPermitGate
    {
        private readonly SemaphoreSlim _globalSemaphore;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _globalBudget;
        private readonly int _perHostBudget;
        private readonly object _hostLock = new();
        private int _currentGlobalUsed;
        private int _maxGlobalObserved;
        private readonly ConcurrentDictionary<string, int> _maxHostObserved = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <summary>Maximum concurrent global permits ever observed (tests/diagnostics).</summary>
        public int MaxGlobalObserved => Volatile.Read(ref _maxGlobalObserved);
        /// <summary>Currently held global permits (tests/diagnostics).</summary>
        public int CurrentGlobalUsed => Volatile.Read(ref _currentGlobalUsed);

        public ConnectionBudget(int globalBudget, int perHostBudget)
        {
            if (globalBudget <= 0) throw new ArgumentOutOfRangeException(nameof(globalBudget));
            if (perHostBudget <= 0) throw new ArgumentOutOfRangeException(nameof(perHostBudget));
            if (perHostBudget > globalBudget) throw new ArgumentException("Per-host budget cannot exceed global budget.");

            _globalBudget = globalBudget;
            _perHostBudget = perHostBudget;
            _globalSemaphore = new SemaphoreSlim(globalBudget, globalBudget);
        }

        /// <summary>
        /// Acquires one global and one per-host permit for the given host key.
        /// Both permits must be acquired together to avoid deadlock.
        /// Returns a disposable permit handle that releases both on disposal.
        /// </summary>
        public async Task<BudgetPermit> AcquireAsync(string hostKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(hostKey))
                throw new ArgumentException("Host key cannot be empty.", nameof(hostKey));

            ObjectDisposedException.ThrowIf(_disposed, nameof(ConnectionBudget));

            // Get or create host semaphore
            SemaphoreSlim hostSemaphore;
            lock (_hostLock)
            {
                hostSemaphore = _hostSemaphores.GetOrAdd(hostKey, _ => new SemaphoreSlim(_perHostBudget, _perHostBudget));
            }

            // Acquire both permits. Use a timeout to detect deadlocks.
            // We acquire global first, then host. On failure, release in reverse order.
            await _globalSemaphore.WaitAsync(ct);
            bool globalAcquired = true;
            try
            {
                await hostSemaphore.WaitAsync(ct);
                var used = Interlocked.Increment(ref _currentGlobalUsed);
                int prev;
                do { prev = Volatile.Read(ref _maxGlobalObserved); }
                while (used > prev && Interlocked.CompareExchange(ref _maxGlobalObserved, used, prev) != prev);
                _maxHostObserved.AddOrUpdate(hostKey,
                    _ => _perHostBudget - hostSemaphore.CurrentCount,
                    (_, _) => _perHostBudget - hostSemaphore.CurrentCount);
                return new BudgetPermit(_globalSemaphore, hostSemaphore, hostKey, this);
            }
            catch
            {
                if (globalAcquired)
                    _globalSemaphore.Release();
                throw;
            }
        }

        async Task<IDisposable> INetworkPermitGate.AcquireAsync(string hostKey, CancellationToken ct)
        {
            return await AcquireAsync(hostKey, ct);
        }

        /// <summary>
        /// Returns the current available global permits (for diagnostics).
        /// </summary>
        public int AvailableGlobal => _globalSemaphore.CurrentCount;

        /// <summary>
        /// Returns the current available permits for a host (for diagnostics).
        /// </summary>
        public int AvailableForHost(string hostKey)
        {
            lock (_hostLock)
            {
                if (_hostSemaphores.TryGetValue(hostKey, out var sem))
                    return sem.CurrentCount;
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

        /// <summary>
        /// Cleanup for fully-released host semaphores (no extra Release here:
        /// BudgetPermit.Dispose already released both semaphores exactly once).
        /// </summary>
        internal void ReleaseHost(string hostKey)
        {
            lock (_hostLock)
            {
                if (_hostSemaphores.TryGetValue(hostKey, out var semaphore))
                {
                    // Only remove when fully free to avoid unbounded growth.
                    if (semaphore.CurrentCount == _perHostBudget)
                    {
                        _hostSemaphores.TryRemove(hostKey, out _);
                    }
                }
            }
            Interlocked.Decrement(ref _currentGlobalUsed);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _globalSemaphore.Dispose();
                foreach (var sem in _hostSemaphores.Values)
                {
                    // SemaphoreSlim doesn't have Dispose in older .NET, but does in .NET Core
                    // We'll just let them be GC'd
                }
                _hostSemaphores.Clear();
            }
        }

        /// <summary>
        /// A permit that holds both global and per-host budget slots.
        /// Disposing releases both permits automatically.
        /// </summary>
        public sealed class BudgetPermit : IDisposable
        {
            private readonly SemaphoreSlim _globalSemaphore;
            private readonly SemaphoreSlim _hostSemaphore;
            private readonly string _hostKey;
            private readonly ConnectionBudget _budget;
            private bool _released;

            internal BudgetPermit(SemaphoreSlim globalSemaphore, SemaphoreSlim hostSemaphore, string hostKey, ConnectionBudget budget)
            {
                _globalSemaphore = globalSemaphore;
                _hostSemaphore = hostSemaphore;
                _hostKey = hostKey;
                _budget = budget;
            }

            public void Dispose()
            {
                if (!_released)
                {
                    _released = true;
                    try { _hostSemaphore.Release(); } catch { }
                    try { _globalSemaphore.Release(); } catch { }
                    // Notify budget that host permit released (for cleanup)
                    try { _budget.ReleaseHost(_hostKey); } catch { }
                }
            }
        }
    }
}