using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DRRipper.Scheduler;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Persistent job store using SQLite with WAL mode.
    /// All SQL is parameterized; no string concatenation with user data.
    /// Schema is versioned for future migrations.
    /// </summary>
    public sealed class JobStore : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _disposed;
        private long _writeCount;

        /// <summary>Total SQL write commands executed (benchmarks/diagnostics).</summary>
        public long WriteCount => Interlocked.Read(ref _writeCount);

        // Current schema version
        private const int CurrentSchemaVersion = 1;

        /// <summary>
        /// Opens or creates the job store database at the given path.
        /// </summary>
        public static async Task<JobStore> CreateAsync(string databasePath, CancellationToken ct = default)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            };

            var connection = new SqliteConnection(csb.ConnectionString);
            await connection.OpenAsync(ct);

            var store = new JobStore(connection);
            await store.InitializeAsync(ct);
            return store;
        }

        private JobStore(SqliteConnection connection)
        {
            _connection = connection;
        }

        /// <summary>
        /// Initializes the database: enables WAL, foreign keys, creates schema if needed, runs migrations.
        /// </summary>
        private async Task InitializeAsync(CancellationToken ct)
        {
            // Enable WAL mode and foreign keys
            await ExecuteNonQueryAsync("PRAGMA journal_mode = WAL;", ct);
            await ExecuteNonQueryAsync("PRAGMA foreign_keys = ON;", ct);
            await ExecuteNonQueryAsync("PRAGMA busy_timeout = 5000;", ct);
            await ExecuteNonQueryAsync("PRAGMA synchronous = NORMAL;", ct);

            // Create schema version table
            await ExecuteNonQueryAsync(@"
                CREATE TABLE IF NOT EXISTS SchemaVersion (
                    Version INTEGER NOT NULL,
                    AppliedUtc TEXT NOT NULL
                );", ct);

            // Get current version
            int version = 0;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result != null && result != DBNull.Value)
                    version = Convert.ToInt32(result);
            }

            // Run migrations
            if (version < 1)
            {
                await MigrateToV1Async(ct);
                await ExecuteNonQueryAsync(
                    "INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES (1, @utc);",
                    ct,
                    new SqliteParameter("@utc", DateTimeOffset.UtcNow.ToString("o")));
            }

            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Database schema version {version} is newer than supported version {CurrentSchemaVersion}. " +
                    "Please update DRRipper to a newer version.");
            }
        }

        /// <summary>
        /// Migration to schema version 1: initial job table.
        /// </summary>
        private async Task MigrateToV1Async(CancellationToken ct)
        {
            await ExecuteNonQueryAsync(@"
                CREATE TABLE Jobs (
                    JobId TEXT NOT NULL PRIMARY KEY,           -- GUID as string
                    OriginalUrl TEXT NOT NULL,
                    TargetDirectory TEXT NOT NULL,
                    RequestedFileName TEXT,
                    ResolvedFileName TEXT,
                    ResolvedFinalPath TEXT,
                    CreatedUtc TEXT NOT NULL,                  -- ISO 8601
                    UpdatedUtc TEXT NOT NULL,                  -- ISO 8601
                    QueuePosition INTEGER NOT NULL,
                    Priority INTEGER NOT NULL DEFAULT 0,
                    State INTEGER NOT NULL DEFAULT 0,          -- JobState enum
                    FailureReason TEXT,
                    AttemptCount INTEGER NOT NULL DEFAULT 0,
                    ConnectionsPerFile INTEGER NOT NULL DEFAULT 8,
                    TotalBytes INTEGER NOT NULL DEFAULT -1,
                    CompletedBytes INTEGER NOT NULL DEFAULT 0,
                    HostKey TEXT,
                    LastStartedUtc TEXT,
                    CompletedUtc TEXT
                );", ct);

            // Indexes for common queries
            await ExecuteNonQueryAsync(@"
                CREATE INDEX IF NOT EXISTS IX_Jobs_State_Priority_QueuePos
                ON Jobs (State, Priority, QueuePosition);", ct);

            await ExecuteNonQueryAsync(@"
                CREATE INDEX IF NOT EXISTS IX_Jobs_HostKey
                ON Jobs (HostKey);", ct);
        }

        /// <summary>
        /// Executes a non-query command with parameters.
        /// </summary>
        private async Task ExecuteNonQueryAsync(string sql, CancellationToken ct, params SqliteParameter[] parameters)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                if (parameters.Length > 0)
                    cmd.Parameters.AddRange(parameters);
                await cmd.ExecuteNonQueryAsync(ct);
                Interlocked.Increment(ref _writeCount);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Executes a scalar query.
        /// </summary>
        private async Task<object?> ExecuteScalarAsync(string sql, CancellationToken ct, params SqliteParameter[] parameters)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                if (parameters.Length > 0)
                    cmd.Parameters.AddRange(parameters);
                return await cmd.ExecuteScalarAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ---- Job CRUD ----

        /// <summary>
        /// Adds a single job to the queue.
        /// </summary>
        public async Task AddAsync(DownloadJob job, CancellationToken ct = default)
        {
            var sql = @"
                INSERT INTO Jobs (
                    JobId, OriginalUrl, TargetDirectory, RequestedFileName, ResolvedFileName, ResolvedFinalPath,
                    CreatedUtc, UpdatedUtc, QueuePosition, Priority, State, FailureReason,
                    AttemptCount, ConnectionsPerFile, TotalBytes, CompletedBytes, HostKey,
                    LastStartedUtc, CompletedUtc
                ) VALUES (
                    @JobId, @OriginalUrl, @TargetDirectory, @RequestedFileName, @ResolvedFileName, @ResolvedFinalPath,
                    @CreatedUtc, @UpdatedUtc, @QueuePosition, @Priority, @State, @FailureReason,
                    @AttemptCount, @ConnectionsPerFile, @TotalBytes, @CompletedBytes, @HostKey,
                    @LastStartedUtc, @CompletedUtc
                );";

            var parameters = JobToParameters(job);
            await ExecuteNonQueryAsync(sql, ct, parameters);
        }

        /// <summary>
        /// Adds multiple jobs in a single transaction (bulk insert).
        /// </summary>
        public async Task AddBatchAsync(IEnumerable<DownloadJob> jobs, CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
                try
                {
                    var sql = @"
                        INSERT INTO Jobs (
                            JobId, OriginalUrl, TargetDirectory, RequestedFileName, ResolvedFileName, ResolvedFinalPath,
                            CreatedUtc, UpdatedUtc, QueuePosition, Priority, State, FailureReason,
                            AttemptCount, ConnectionsPerFile, TotalBytes, CompletedBytes, HostKey,
                            LastStartedUtc, CompletedUtc
                        ) VALUES (
                            @JobId, @OriginalUrl, @TargetDirectory, @RequestedFileName, @ResolvedFileName, @ResolvedFinalPath,
                            @CreatedUtc, @UpdatedUtc, @QueuePosition, @Priority, @State, @FailureReason,
                            @AttemptCount, @ConnectionsPerFile, @TotalBytes, @CompletedBytes, @HostKey,
                            @LastStartedUtc, @CompletedUtc
                        );";

                    foreach (var job in jobs)
                    {
                        using var cmd = _connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = sql;
                        cmd.Parameters.AddRange(JobToParameters(job));
                        await cmd.ExecuteNonQueryAsync(ct);
                        Interlocked.Increment(ref _writeCount);
                    }
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Gets a job by ID.
        /// </summary>
        public async Task<DownloadJob?> GetAsync(Guid jobId, CancellationToken ct = default)
        {
            var sql = "SELECT * FROM Jobs WHERE JobId = @JobId;";
            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@JobId", jobId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    return ReadJob(reader);
                return null;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Gets jobs eligible to be started (Queued, Interrupted, Retrying) ordered by priority and queue position.
        /// </summary>
        public async Task<List<DownloadJob>> GetEligibleJobsAsync(int limit, CancellationToken ct = default)
        {
            var sql = @"
                SELECT * FROM Jobs
                WHERE State IN (0, 4, 8)  -- Queued, Retrying, Interrupted
                ORDER BY Priority, QueuePosition
                LIMIT @Limit;";

            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@Limit", limit);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                var jobs = new List<DownloadJob>();
                while (await reader.ReadAsync(ct))
                    jobs.Add(ReadJob(reader));
                return jobs;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Gets all non-terminal jobs (for startup recovery).
        /// </summary>
        public async Task<List<DownloadJob>> GetNonTerminalJobsAsync(CancellationToken ct = default)
        {
            var sql = @"
                SELECT * FROM Jobs
                WHERE State NOT IN (5, 6, 7)  -- not Completed, Failed, Cancelled
                ORDER BY Priority, QueuePosition;";

            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                using var reader = await cmd.ExecuteReaderAsync(ct);
                var jobs = new List<DownloadJob>();
                while (await reader.ReadAsync(ct))
                    jobs.Add(ReadJob(reader));
                return jobs;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Updates job state and optional fields atomically.
        /// </summary>
        public async Task UpdateStateAsync(
            Guid jobId,
            JobState newState,
            string? failureReason = null,
            int? attemptCount = null,
            long? completedBytes = null,
            long? totalBytes = null,
            string? resolvedFileName = null,
            string? resolvedFinalPath = null,
            string? hostKey = null,
            DateTimeOffset? lastStartedUtc = null,
            DateTimeOffset? completedUtc = null,
            CancellationToken ct = default)
        {
            var sets = new List<string> { "State = @State", "UpdatedUtc = @UpdatedUtc" };
            var parameters = new List<SqliteParameter>
            {
                new("@JobId", jobId.ToString()),
                new("@State", (int)newState),
                new("@UpdatedUtc", DateTimeOffset.UtcNow.ToString("o")),
            };

            if (failureReason != null) { sets.Add("FailureReason = @FailureReason"); parameters.Add(new("@FailureReason", failureReason)); }
            if (attemptCount.HasValue) { sets.Add("AttemptCount = @AttemptCount"); parameters.Add(new("@AttemptCount", attemptCount.Value)); }
            if (completedBytes.HasValue) { sets.Add("CompletedBytes = @CompletedBytes"); parameters.Add(new("@CompletedBytes", completedBytes.Value)); }
            if (totalBytes.HasValue) { sets.Add("TotalBytes = @TotalBytes"); parameters.Add(new("@TotalBytes", totalBytes.Value)); }
            if (resolvedFileName != null) { sets.Add("ResolvedFileName = @ResolvedFileName"); parameters.Add(new("@ResolvedFileName", resolvedFileName)); }
            if (resolvedFinalPath != null) { sets.Add("ResolvedFinalPath = @ResolvedFinalPath"); parameters.Add(new("@ResolvedFinalPath", resolvedFinalPath)); }
            if (hostKey != null) { sets.Add("HostKey = @HostKey"); parameters.Add(new("@HostKey", hostKey)); }
            if (lastStartedUtc.HasValue) { sets.Add("LastStartedUtc = @LastStartedUtc"); parameters.Add(new("@LastStartedUtc", lastStartedUtc.Value.ToString("o"))); }
            if (completedUtc.HasValue) { sets.Add("CompletedUtc = @CompletedUtc"); parameters.Add(new("@CompletedUtc", completedUtc.Value.ToString("o"))); }

            var sql = $"UPDATE Jobs SET {string.Join(", ", sets)} WHERE JobId = @JobId;";
            await ExecuteNonQueryAsync(sql, ct, parameters.ToArray());
        }

        /// <summary>
        /// Updates only the progress fields (CompletedBytes, TotalBytes) for a job.
        /// Used for periodic progress persistence without full state change.
        /// </summary>
        public async Task UpdateProgressAsync(Guid jobId, long completedBytes, long? totalBytes = null, CancellationToken ct = default)
        {
            var sql = totalBytes.HasValue
                ? "UPDATE Jobs SET CompletedBytes = @CompletedBytes, TotalBytes = @TotalBytes, UpdatedUtc = @UpdatedUtc WHERE JobId = @JobId;"
                : "UPDATE Jobs SET CompletedBytes = @CompletedBytes, UpdatedUtc = @UpdatedUtc WHERE JobId = @JobId;";

            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@JobId", jobId.ToString());
                cmd.Parameters.AddWithValue("@CompletedBytes", completedBytes);
                cmd.Parameters.AddWithValue("@UpdatedUtc", DateTimeOffset.UtcNow.ToString("o"));
                if (totalBytes.HasValue)
                    cmd.Parameters.AddWithValue("@TotalBytes", totalBytes.Value);
                await cmd.ExecuteNonQueryAsync(ct);
                Interlocked.Increment(ref _writeCount);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Reorders queued jobs by assigning new QueuePosition values.
        /// </summary>
        public async Task ReorderAsync(IReadOnlyList<Guid> jobIdsInOrder, CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
                try
                {
                    for (int i = 0; i < jobIdsInOrder.Count; i++)
                    {
                        using var cmd = _connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "UPDATE Jobs SET QueuePosition = @Pos, UpdatedUtc = @Utc WHERE JobId = @JobId;";
                        cmd.Parameters.AddWithValue("@Pos", i);
                        cmd.Parameters.AddWithValue("@Utc", DateTimeOffset.UtcNow.ToString("o"));
                        cmd.Parameters.AddWithValue("@JobId", jobIdsInOrder[i].ToString());
                        await cmd.ExecuteNonQueryAsync(ct);
                        Interlocked.Increment(ref _writeCount);
                    }
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Removes a job from the queue.
        /// </summary>
        public async Task RemoveAsync(Guid jobId, CancellationToken ct = default)
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM Jobs WHERE JobId = @JobId;",
                ct,
                new SqliteParameter("@JobId", jobId.ToString()));
        }

        /// <summary>
        /// Normalizes interrupted active states on startup: Downloading/Pausing/Retrying -> Interrupted.
        /// </summary>
        public async Task NormalizeInterruptedStatesAsync(CancellationToken ct = default)
        {
            await ExecuteNonQueryAsync(@"
                UPDATE Jobs
                SET State = @Interrupted, UpdatedUtc = @Utc
                WHERE State IN (1, 2, 4);  -- Downloading, Pausing, Retrying
                ",
                ct,
                new SqliteParameter("@Interrupted", (int)JobState.Interrupted),
                new SqliteParameter("@Utc", DateTimeOffset.UtcNow.ToString("o")));
        }

        /// <summary>
        /// Gets the maximum queue position currently in use.
        /// </summary>
        public async Task<long> GetMaxQueuePositionAsync(CancellationToken ct = default)
        {
            var result = await ExecuteScalarAsync("SELECT COALESCE(MAX(QueuePosition), -1) FROM Jobs;", ct);
            return result != null && result != DBNull.Value ? Convert.ToInt64(result) : -1;
        }

        /// <summary>
        /// Counts active (downloading) jobs.
        /// </summary>
        public async Task<int> CountActiveJobsAsync(CancellationToken ct = default)
        {
            var result = await ExecuteScalarAsync("SELECT COUNT(*) FROM Jobs WHERE State = 1;", ct); // Downloading
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
        }

        /// <summary>
        /// Counts active jobs for a specific host key.
        /// </summary>
        public async Task<int> CountActiveJobsForHostAsync(string hostKey, CancellationToken ct = default)
        {
            var result = await ExecuteScalarAsync(
                "SELECT COUNT(*) FROM Jobs WHERE State = 1 AND HostKey = @HostKey;",
                ct,
                new SqliteParameter("@HostKey", hostKey));
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
        }

        /// <summary>
        /// Gets all jobs for UI display (with optional state filter).
        /// </summary>
        public async Task<List<DownloadJob>> GetAllJobsAsync(JobState? stateFilter = null, CancellationToken ct = default)
        {
            string sql;
            SqliteParameter? param = null;

            if (stateFilter.HasValue)
            {
                sql = "SELECT * FROM Jobs WHERE State = @State ORDER BY Priority, QueuePosition;";
                param = new SqliteParameter("@State", (int)stateFilter.Value);
            }
            else
            {
                sql = "SELECT * FROM Jobs ORDER BY Priority, QueuePosition;";
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                if (param != null) cmd.Parameters.Add(param);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                var jobs = new List<DownloadJob>();
                while (await reader.ReadAsync(ct))
                    jobs.Add(ReadJob(reader));
                return jobs;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static SqliteParameter[] JobToParameters(DownloadJob job)
        {
            return new[]
            {
                new SqliteParameter("@JobId", job.JobId.ToString()),
                new SqliteParameter("@OriginalUrl", job.OriginalUrl),
                new SqliteParameter("@TargetDirectory", job.TargetDirectory),
                new SqliteParameter("@RequestedFileName", job.RequestedFileName ?? (object)DBNull.Value),
                new SqliteParameter("@ResolvedFileName", job.ResolvedFileName ?? (object)DBNull.Value),
                new SqliteParameter("@ResolvedFinalPath", job.ResolvedFinalPath ?? (object)DBNull.Value),
                new SqliteParameter("@CreatedUtc", job.CreatedUtc.ToString("o")),
                new SqliteParameter("@UpdatedUtc", job.UpdatedUtc.ToString("o")),
                new SqliteParameter("@QueuePosition", job.QueuePosition),
                new SqliteParameter("@Priority", job.Priority),
                new SqliteParameter("@State", (int)job.State),
                new SqliteParameter("@FailureReason", job.FailureReason ?? (object)DBNull.Value),
                new SqliteParameter("@AttemptCount", job.AttemptCount),
                new SqliteParameter("@ConnectionsPerFile", job.ConnectionsPerFile),
                new SqliteParameter("@TotalBytes", job.TotalBytes),
                new SqliteParameter("@CompletedBytes", job.CompletedBytes),
                new SqliteParameter("@HostKey", job.HostKey ?? (object)DBNull.Value),
                new SqliteParameter("@LastStartedUtc", job.LastStartedUtc?.ToString("o") ?? (object)DBNull.Value),
                new SqliteParameter("@CompletedUtc", job.CompletedUtc?.ToString("o") ?? (object)DBNull.Value),
            };
        }

        private static DownloadJob ReadJob(DbDataReader reader)
        {
            return new DownloadJob
            {
                JobId = Guid.Parse(reader.GetString(reader.GetOrdinal("JobId"))),
                OriginalUrl = reader.GetString(reader.GetOrdinal("OriginalUrl")),
                TargetDirectory = reader.GetString(reader.GetOrdinal("TargetDirectory")),
                RequestedFileName = reader.IsDBNull(reader.GetOrdinal("RequestedFileName")) ? null : reader.GetString(reader.GetOrdinal("RequestedFileName")),
                ResolvedFileName = reader.IsDBNull(reader.GetOrdinal("ResolvedFileName")) ? null : reader.GetString(reader.GetOrdinal("ResolvedFileName")),
                ResolvedFinalPath = reader.IsDBNull(reader.GetOrdinal("ResolvedFinalPath")) ? null : reader.GetString(reader.GetOrdinal("ResolvedFinalPath")),
                CreatedUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedUtc"))),
                UpdatedUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedUtc"))),
                QueuePosition = reader.GetInt64(reader.GetOrdinal("QueuePosition")),
                Priority = reader.GetInt32(reader.GetOrdinal("Priority")),
                State = (JobState)reader.GetInt32(reader.GetOrdinal("State")),
                FailureReason = reader.IsDBNull(reader.GetOrdinal("FailureReason")) ? null : reader.GetString(reader.GetOrdinal("FailureReason")),
                AttemptCount = reader.GetInt32(reader.GetOrdinal("AttemptCount")),
                ConnectionsPerFile = reader.GetInt32(reader.GetOrdinal("ConnectionsPerFile")),
                TotalBytes = reader.GetInt64(reader.GetOrdinal("TotalBytes")),
                CompletedBytes = reader.GetInt64(reader.GetOrdinal("CompletedBytes")),
                HostKey = reader.IsDBNull(reader.GetOrdinal("HostKey")) ? null : reader.GetString(reader.GetOrdinal("HostKey")),
                LastStartedUtc = reader.IsDBNull(reader.GetOrdinal("LastStartedUtc")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("LastStartedUtc"))),
                CompletedUtc = reader.IsDBNull(reader.GetOrdinal("CompletedUtc")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CompletedUtc"))),
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _writeLock.Dispose();
                _connection.Dispose();
            }
        }
    }
}