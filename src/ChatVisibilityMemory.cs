using System.Security.Cryptography;

namespace SpendMeMaybe;

// Adapted from Plugin Chat Overlay (MIT), Copyright 2026 Minasis / min.
internal static class ChatVisibilityMemory
{
    private static readonly byte[] ExpectedHash = Convert.FromHexString("405954B4C96003CF1F1406C0F5F9E62E6ED2383A74B3E17A7868AD7C8ED16BF6");
    internal static bool MatchesCode(ReadOnlySpan<byte> code)
    {
        Span<byte> hash = stackalloc byte[32];
        return TryHashCode(code, hash) && hash.SequenceEqual(ExpectedHash);
    }
    internal static bool TryHashCode(ReadOnlySpan<byte> code, Span<byte> hash)
    {
        if (code.Length != 181 || code[90] != 0xE8 || hash.Length < 32) return false;
        Span<byte> normalized = stackalloc byte[181];
        code.CopyTo(normalized);
        normalized.Slice(91, 4).Clear();
        SHA256.HashData(normalized, hash);
        return true;
    }
    internal static bool ReadExpanded(nint addon, int zoomOffset, int flagsOffset)
    {
        if (addon == 0 || zoomOffset < 0 || AddonVisibilityReader.ReadVisible(addon, flagsOffset, 1u << 21) != true) return false;
        Span<byte> flag = stackalloc byte[1];
        return AddonVisibilityReader.Read(addon + zoomOffset, flag) && flag[0] == 1;
    }
}
