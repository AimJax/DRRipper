using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DRRipper.BrowserBridge
{
    /// <summary>
    /// Native Messaging framing (§10): 4-byte little-endian length + UTF-8 JSON.
    /// Shared by the native host (stdio) and the named-pipe hop so both
    /// boundaries enforce identical limits.
    /// </summary>
    public static class NativeMessageFraming
    {
        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static byte[] Encode<T>(T message)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            if (json.Length > BrowserBridgeProtocol.MaxMessageBytes)
                throw new InvalidOperationException($"Message too large: {json.Length} bytes.");
            var frame = new byte[4 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
            json.CopyTo(frame, 4);
            return frame;
        }

        public static async Task<T> ReadAsync<T>(Stream input, CancellationToken ct)
        {
            var json = await ReadPayloadAsync(input, ct).ConfigureAwait(false);
            T? message;
            try
            {
                message = JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Malformed JSON payload.", ex);
            }
            if (message == null)
                throw new InvalidDataException("Empty JSON payload.");
            return message;
        }

        public static async Task<byte[]> ReadPayloadAsync(Stream input, CancellationToken ct)
        {
            var lenBuf = new byte[4];
            await ReadExactAsync(input, lenBuf, ct).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (length <= 0)
                throw new InvalidDataException($"Zero or negative message length: {length}.");
            if (length > BrowserBridgeProtocol.MaxMessageBytes)
                throw new InvalidDataException($"Message length {length} exceeds 1 MB cap.");
            var payload = new byte[length];
            await ReadExactAsync(input, payload, ct).ConfigureAwait(false);
            return payload;
        }

        private static async Task ReadExactAsync(Stream input, byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await input.ReadAsync(buffer, offset, buffer.Length - offset, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Truncated message frame.");
                offset += read;
            }
        }

        public static async Task WriteAsync<T>(Stream output, T message, CancellationToken ct)
        {
            var frame = Encode(message);
            await output.WriteAsync(frame, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }

        public static string DecodeUtf8(byte[] payload) => Encoding.UTF8.GetString(payload);
    }
}
