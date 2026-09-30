using System.Security.Cryptography;

namespace DRRipper.TestServer;

/// <summary>
/// Generates deterministic test content without touching the filesystem.
/// Byte at absolute offset i for a given seed: b(i) = (i*31 + seed*101 + (i>>8)) &amp; 0xFF.
/// Tests recompute the same function to obtain expected bytes / hashes.
/// </summary>
public static class DeterministicContent
{
    public static byte At(long index, int seed, bool constant = false, byte constantByte = 0x41)
        => constant ? constantByte : (byte)((index * 31 + seed * 101 + (index >> 8)) & 0xFF);

    public static void FillSlice(long offset, Span<byte> buffer, int seed, bool constant = false, byte constantByte = 0x41)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = At(offset + i, seed, constant, constantByte);
    }

    public static byte[] Slice(long offset, int count, int seed, bool constant = false, byte constantByte = 0x41)
    {
        var buf = new byte[count];
        FillSlice(offset, buf, seed, constant, constantByte);
        return buf;
    }

    public static string Sha256Hex(long size, int seed, bool constant = false, byte constantByte = 0x41)
    {
        using var sha = SHA256.Create();
        const int chunk = 1 << 20;
        var buf = new byte[chunk];
        long remaining = size;
        long offset = 0;
        while (remaining > 0)
        {
            int n = (int)Math.Min(chunk, remaining);
            FillSlice(offset, buf.AsSpan(0, n), seed, constant, constantByte);
            sha.TransformBlock(buf, 0, n, null, 0);
            offset += n;
            remaining -= n;
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public static string Sha256HexFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
