using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DRRipper
{
    /// <summary>
    /// Durable file flush abstraction (Ticket #004.1 §11). Production use must
    /// go through <see cref="OsFileFlusher"/>, which validates the Win32 result.
    /// Tests substitute faulting/delaying fakes. Internal only — never public API.
    /// </summary>
    internal interface IDurableFileFlusher
    {
        /// <summary>
        /// Establishes OS-level durability for data already written to
        /// <paramref name="handle"/>. Throws on any failure; never silent.
        /// </summary>
        void Flush(SafeFileHandle handle);
    }

    /// <summary>
    /// Production flusher: Win32 FlushFileBuffers with the boolean return value
    /// checked (Ticket #004.1 §6). A false return becomes a
    /// <see cref="Win32Exception"/> preserving the original error code, so no
    /// metadata is ever published for unflushed ranges.
    /// </summary>
    internal sealed class OsFileFlusher : IDurableFileFlusher
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(SafeFileHandle hFile);

        public void Flush(SafeFileHandle handle)
        {
            if (handle == null || handle.IsInvalid)
                throw new IOException("Cannot establish data durability: destination file handle is invalid.");
            if (!FlushFileBuffers(handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "FlushFileBuffers reported failure; data durability not established.");
        }
    }

    /// <summary>
    /// Recovery-metadata temp publication abstraction (Ticket #004.1 §11).
    /// Implementations write the snapshot to the given UNIQUE temp path and
    /// flush it; the session performs the generation authority check and the
    /// atomic replace itself. Tests substitute gating/failing fakes for
    /// deterministic out-of-order and fault scenarios.
    /// </summary>
    internal interface IMetadataPublisher
    {
        /// <summary>Serialize <paramref name="meta"/> to <paramref name="tempPath"/> and flush it. Throws on failure.</summary>
        void WriteTemp(RecoveryMetadata meta, string tempPath, CancellationToken ct);
    }

    /// <summary>Production publisher: compact JSON + OS-flushed temp file.</summary>
    internal sealed class AtomicFilePublisher : IMetadataPublisher
    {
        public void WriteTemp(RecoveryMetadata meta, string tempPath, CancellationToken ct)
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                JsonSerializer.Serialize(fs, meta, RecoveryMetadata.JsonOptions);
                // Flush(true) flushes OS buffers for the temp file itself; a torn
                // temp can never become canonical because only complete, flushed
                // temps are ever renamed (and only by the authoritative generation).
                fs.Flush(true);
            }
        }
    }
}
