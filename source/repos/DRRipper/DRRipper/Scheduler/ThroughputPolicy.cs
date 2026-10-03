namespace DRRipper.Scheduler
{
    /// <summary>
    /// Transfer-mode policy (Ticket #008 §5/§11/§13/§22): presets, effective
    /// budgets, and size-tiered initial concurrency. Pure functions —
    /// unit-tested, no I/O. Balanced behavior is byte-identical to the
    /// pre-#008 scheduler; MaximumThroughput is explicit opt-in only (§52).
    /// </summary>
    public static class SchedulerThroughputPolicy
    {
        /// <summary>Maximum-mode budget preset (§11/§23): one active large file may take it all.</summary>
        public const int MaximumGlobalBudget = 32;
        public const int MaximumPerHostBudget = 32;
        public const int MaximumPerFileConnections = 32;

        /// <summary>Balanced defaults (unchanged pre-#008 behavior).</summary>
        public const int BalancedGlobalBudget = 16;
        public const int BalancedPerHostBudget = 8;
        public const int BalancedPerFileConnections = 8;

        /// <summary>Adaptive ladder (§6): staged targets, never jumped blindly.</summary>
        public static readonly int[] ConcurrencyLadder = [4, 8, 16, 24, 32];

        /// <summary>Size tiers for initial concurrency (§13).</summary>
        public const long SmallFileThreshold = 64L * 1024 * 1024;
        public const long LargeFileThreshold = 1024L * 1024 * 1024;

        /// <summary>
        /// Applies the Maximum preset onto mutable settings (called explicitly
        /// when the user picks the mode — never silently, §52).
        /// </summary>
        public static void ApplyMaximumPreset(SchedulerSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.TransferMode = TransferMode.MaximumThroughput;
            settings.GlobalConnectionBudget = MaximumGlobalBudget;
            settings.PerHostConnectionBudget = MaximumPerHostBudget;
            settings.MaxConnectionsPerFile = MaximumPerFileConnections;
        }

        public static bool IsMaximum(SchedulerSettings? settings)
            => settings != null && settings.TransferMode == TransferMode.MaximumThroughput;

        /// <summary>Effective per-file cap: configured ceiling, clamped to [1, 64].</summary>
        public static int EffectiveMaxPerFile(SchedulerSettings? settings)
        {
            int cap = settings?.MaxConnectionsPerFile ?? BalancedPerFileConnections;
            return Math.Clamp(cap, 1, 64);
        }

        /// <summary>
        /// Initial worker target (§13): small files stay cheap, large files
        /// start aggressive. Unknown size (-1) starts moderate.
        /// </summary>
        public static int InitialConcurrency(long totalBytes, int cap)
        {
            cap = Math.Clamp(cap, 1, 64);
            if (totalBytes <= 0)
                return Math.Min(8, cap);
            if (totalBytes < SmallFileThreshold)
                return Math.Min(4, cap);
            if (totalBytes < LargeFileThreshold)
                return Math.Min(8, cap);
            return Math.Min(16, cap);
        }

        /// <summary>
        /// Maximum-mode per-file share (§12/§53): the 32-file ceiling divided
        /// among ACTUAL active jobs (never below 8, never above 32). One file
        /// saturates alone; eight files behave like Balanced (no global
        /// over-parallelization collapse on capped paths). Dominance within a
        /// share still emerges via adaptation + the shared gate.
        /// </summary>
        public static int SharedWorkerCap(int activeCount)
            => Math.Clamp(MaximumPerFileConnections / Math.Max(1, activeCount), 8, 32);

        /// <summary>Raises a target to the next ladder rung (≤ cap). Never lowers.</summary>
        public static int NextLadderStep(int current, int cap)
        {
            cap = Math.Clamp(cap, 1, 64);
            foreach (var rung in ConcurrencyLadder)
            {
                if (rung > current)
                    return Math.Min(rung, cap);
            }
            return Math.Min(current, cap);
        }

        /// <summary>Lowers a target one ladder rung (≥ 1). Gradual backoff (§9).</summary>
        public static int PrevLadderStep(int current)
        {
            int prev = 1;
            foreach (var rung in ConcurrencyLadder)
            {
                if (rung >= current)
                    return Math.Max(1, prev);
                prev = rung;
            }
            return Math.Max(1, prev);
        }
    }
}
