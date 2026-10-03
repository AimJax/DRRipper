namespace DRRipper.Scheduler
{
    /// <summary>
    /// Per-file adaptive concurrency (Ticket #008 §6–§9): staged ramp-up along
    /// the <see cref="SchedulerThroughputPolicy.ConcurrencyLadder"/> with
    /// hysteresis, plateau detection, and throttle/regression backoff.
    /// Pure decision logic — no timers, no I/O; the session feeds one
    /// observation per measurement window and applies <see cref="Target"/>.
    /// </summary>
    public sealed class AdaptiveConcurrencyController
    {
        /// <summary>Measurement window floor: ignore thinner samples (§7).</summary>
        public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(2);

        /// <summary>Relative gain that justifies another rung (§6).</summary>
        public const double GainThreshold = 0.10;

        /// <summary>Relative regression that forces a step down (§9).</summary>
        public const double RegressionThreshold = 0.15;

        /// <summary>
        /// Post-increase validation gain (§29): after an up-step the next
        /// window must beat this, or the step is reverted (extra workers
        /// bought nothing — the path is globally capped).
        /// </summary>
        public const double ValidationGain = 0.05;

        /// <summary>
        /// Optimistic-probe floor (§28): ramp while each connection still
        /// delivers this fraction of its best rate (per-connection-capped
        /// servers scale linearly, so flat aggregate + healthy per-conn
        /// means "add workers").
        /// </summary>
        public const double OptimisticPerConnRatio = 0.6;

        /// <summary>Windows to hold after a step-down before ramping again (§7).</summary>
        public const int DownCooldownWindows = 3;

        /// <summary>Consecutive negligible-gain windows that declare a plateau (§6).</summary>
        public const int PlateauWindows = 2;

        /// <summary>Faults per window that count as severe (drop to floor, §9).</summary>
        public const int SevereFaultsPerWindow = 10;

        private readonly int _cap;
        private readonly int _floor;
        private double _lastThroughputMBps;
        private bool _hasLast;
        private int _windowsSinceDown = DownCooldownWindows;
        private int _negligibleStreak;
        private double _bestPerConn;
        /// <summary>Post-increase validation windows remaining (warm-up grace, §29).</summary>
        private int _validationLeft;
        /// <summary>Optimistic-probe block remaining (decaying plateau, §7).</summary>
        private int _probeBlock;
        /// <summary>Consecutive failed probes (drives block growth).</summary>
        private int _revertCount;

        public AdaptiveConcurrencyController(int initial, int cap)
        {
            _cap = Math.Clamp(cap, 1, 64);
            _floor = Math.Min(4, _cap);
            Target = Math.Clamp(initial, _floor, _cap);
            PeakMBps = 0;
        }

        /// <summary>Current worker target (what the session should enforce).</summary>
        public int Target { get; private set; }

        /// <summary>Best window throughput observed (telemetry, §21).</summary>
        public double PeakMBps { get; private set; }

        /// <summary>Last decision taken (telemetry, §21).</summary>
        public string LastDecision { get; private set; } = "init";

        /// <summary>Recent error pressure 0..1 (telemetry, §21).</summary>
        public double RecentErrorPressure { get; private set; }

        /// <summary>
        /// Feeds one measurement window. Never throws. Returns the (possibly
        /// unchanged) target.
        /// </summary>
        public int Observe(double windowMBps, int faults, int throttles)
        {
            try
            {
                if (double.IsNaN(windowMBps) || double.IsInfinity(windowMBps) || windowMBps < 0)
                    windowMBps = 0;
                faults = Math.Max(0, faults);
                throttles = Math.Max(0, throttles);
                RecentErrorPressure = Math.Clamp((faults + throttles * 2) / 10.0, 0.0, 1.0);

                if (windowMBps > PeakMBps)
                    PeakMBps = windowMBps;

                // Explicit throttle signals win immediately (§9/§31).
                if (throttles > 0)
                    return StepDown("down-throttle");

                // Severe fault storm: drop to floor, full cooldown.
                if (faults >= SevereFaultsPerWindow)
                    return DropToFloor("fault-storm");

                if (!_hasLast)
                {
                    _hasLast = true;
                    _lastThroughputMBps = windowMBps;
                    _validationLeft = 0;
                    LastDecision = "baseline";
                    return Target;
                }

                double prev = _lastThroughputMBps;
                _lastThroughputMBps = windowMBps;

                // First real comparison needs a sane denominator.
                if (prev <= 0)
                {
                    if (windowMBps > 0)
                        return StepUp("up-signal");
                    _validationLeft = 0;
                    LastDecision = "no-signal";
                    return Target;
                }

                double delta = (windowMBps - prev) / prev;
                double perConn = windowMBps / Math.Max(1, Target);
                if (perConn > _bestPerConn)
                    _bestPerConn = perConn;
                double perConnRatio = _bestPerConn > 0 ? perConn / _bestPerConn : 1.0;

                // Post-increase validation: the first window after a step is
                // warm-up grace (new workers still establishing); the second
                // decides. Acceptance needs BOTH aggregate gain (workers
                // bought throughput) AND per-connection health (no collapse):
                // either signal alone misfires (ramp-up lag vs global caps).
                if (_validationLeft > 0)
                {
                    _validationLeft--;
                    if (delta <= -RegressionThreshold)
                    {
                        _negligibleStreak = 0;
                        return RevertProbe();
                    }
                    if (_validationLeft > 0)
                    {
                        LastDecision = "warming";
                        return Target;
                    }
                    if (perConnRatio < OptimisticPerConnRatio || delta < ValidationGain)
                    {
                        _negligibleStreak = 0;
                        return RevertProbe();
                    }
                    _negligibleStreak = 0;
                    // Validated: fall through to the normal rules below.
                }

                if (delta <= -RegressionThreshold)
                {
                    _negligibleStreak = 0;
                    return StepDown("down-regression");
                }
                if (delta >= GainThreshold)
                {
                    _negligibleStreak = 0;
                    _probeBlock = 0; // genuine improvement breaks plateaus
                    _revertCount = 0;
                    return StepUp("up-gain");
                }
                // Flat window: count toward plateau, then probe optimistically
                // while per-connection health holds (§28), unless blocked.
                _negligibleStreak++;
                if (_negligibleStreak >= PlateauWindows)
                    _probeBlock = Math.Max(_probeBlock, 3);
                if (_probeBlock == 0 && perConnRatio >= OptimisticPerConnRatio &&
                    _windowsSinceDown >= DownCooldownWindows)
                    return StepUp("up-probe");
                if (_probeBlock > 0)
                {
                    _probeBlock--;
                    LastDecision = "plateau-hold";
                }
                else
                {
                    LastDecision = "hold";
                }
                _windowsSinceDown++;
                return Target;
            }
            catch
            {
                LastDecision = "error-hold";
                return Target;
            }
        }

        private int StepUp(string reason)
        {
            if (_probeBlock > 0)
            {
                _validationLeft = 0;
                LastDecision = "plateau-hold";
                _probeBlock--;
                _windowsSinceDown++;
                return Target;
            }
            if (_windowsSinceDown < DownCooldownWindows)
            {
                _validationLeft = 0;
                LastDecision = "cooldown-hold";
                _windowsSinceDown++;
                return Target;
            }
            int next = SchedulerThroughputPolicy.NextLadderStep(Target, _cap);
            if (next <= Target)
            {
                _validationLeft = 0;
                LastDecision = "at-cap";
                _windowsSinceDown++;
                return Target;
            }
            Target = next;
            _validationLeft = 2; // two validation windows (warm-up + verdict)
            LastDecision = reason;
            _windowsSinceDown++;
            return Target;
        }

        /// <summary>Failed probe: step back with a lengthening probe block.</summary>
        private int RevertProbe()
        {
            _revertCount++;
            _probeBlock = Math.Min(24, 3 * (1 << Math.Min(_revertCount, 3)));
            return StepDown("down-revert");
        }

        private int StepDown(string reason)
        {
            _validationLeft = 0;
            int next = SchedulerThroughputPolicy.PrevLadderStep(Target);
            next = Math.Clamp(next, _floor, _cap);
            if (next >= Target)
            {
                LastDecision = "at-floor";
                return Target;
            }
            Target = next;
            _windowsSinceDown = 0;
            _negligibleStreak = 0;
            LastDecision = reason;
            return Target;
        }

        private int DropToFloor(string reason)
        {
            _validationLeft = 0;
            if (Target > _floor)
            {
                Target = _floor;
                LastDecision = reason;
            }
            else
            {
                LastDecision = "at-floor";
            }
            _windowsSinceDown = 0;
            _negligibleStreak = 0;
            return Target;
        }
    }
}
