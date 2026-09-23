using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Logzilla.Core.Common;

/// <summary>Byte-level lookup tables used by the scanners, so no decoding is needed.</summary>
public static class AsciiTable
{
    /// <summary>Maps each byte to its lower-case form; non-letters map to themselves.</summary>
    public static readonly byte[] Lower = BuildLower();

    /// <summary>True for bytes that can form part of an identifier.</summary>
    public static readonly bool[] IsWordByte = BuildWord();

    public static byte ToLowerAscii(byte value) => Lower[value];

    public static bool IsWord(byte value) => IsWordByte[value];

    /// <summary>
    /// Writes <paramref name="source"/> folded through <see cref="ToLowerAscii(byte)"/>
    /// into <paramref name="destination"/>: A–Z become a–z, every other byte (UTF-8
    /// continuation bytes included) is copied as it is. Vectorised, because the
    /// case-insensitive search folds every line it scans.
    /// </summary>
    public static void ToLowerAscii(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is shorter than the source.", nameof(destination));
        }
        var n = source.Length;
        var i = 0;
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        if (Vector256.IsHardwareAccelerated && n >= Vector256<byte>.Count)
        {
            var a = Vector256.Create((byte)'A');
            var letters = Vector256.Create((byte)26);
            var bit = Vector256.Create((byte)0x20);
            for (; i <= n - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                var v = Vector256.LoadUnsafe(ref s, (nuint)i);
                // Unsigned: anything below 'A' wraps round to a large value.
                var upper = Vector256.LessThan(v - a, letters);
                (v | (upper & bit)).StoreUnsafe(ref d, (nuint)i);
            }
        }
        else if (Vector128.IsHardwareAccelerated && n >= Vector128<byte>.Count)
        {
            var a = Vector128.Create((byte)'A');
            var letters = Vector128.Create((byte)26);
            var bit = Vector128.Create((byte)0x20);
            for (; i <= n - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)i);
                var upper = Vector128.LessThan(v - a, letters);
                (v | (upper & bit)).StoreUnsafe(ref d, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            destination[i] = Lower[source[i]];
        }
    }

    private static byte[] BuildLower()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = (byte)i;
        }

        for (var i = 'A'; i <= 'Z'; i++)
        {
            table[i] = (byte)(i + 32);
        }

        return table;
    }

    private static bool[] BuildWord()
    {
        var table = new bool[256];
        for (var i = '0'; i <= '9'; i++)
        {
            table[i] = true;
        }

        for (var i = 'A'; i <= 'Z'; i++)
        {
            table[i] = true;
        }

        for (var i = 'a'; i <= 'z'; i++)
        {
            table[i] = true;
        }

        table['_'] = true;
        return table;
    }
}
