using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SpendMeMaybe;

// Safe-read primitive adapted from Plugin Chat Overlay (MIT), Copyright 2026 Minasis and min.
internal static class AddonVisibilityReader
{
    public static bool? ReadVisible(nint addon, int flagsOffset, uint showMask)
    {
        if (addon == 0 || flagsOffset < 0 || showMask == 0) return null;
        Span<byte> flags = stackalloc byte[4];
        if (!Read(addon + flagsOffset, flags)) return null;
        return (BinaryPrimitives.ReadUInt32LittleEndian(flags) & showMask) != 0;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);
    internal static unsafe bool Read(nint address, Span<byte> buffer)
    {
        if (address == 0 || buffer.IsEmpty) return false;
        fixed (byte* pointer = buffer)
            return ReadProcessMemory(new nint(-1), address, pointer, (nuint)buffer.Length, out var read) && read == (nuint)buffer.Length;
    }
}
