using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NesEmulator.Snes;

/// <summary>
/// The few helpers every SNES component's save state shares (the components themselves: <c>CPU_SFC.State.cs</c>, <c>PPU_SFC.State.cs</c>, <c>BOARD_SFC.State.cs</c>, <c>SMP_SFC.State.cs</c>, <c>DSP_SFC.State.cs</c>,
/// <c>APU_SFC.State.cs</c>, <c>APU_HLE.State.cs</c>, <c>SnesCartridge.State.cs</c> and one file for each cartridge chip). Every component writes a version byte first and reads it back first: a component that reads more or
/// fewer bytes than its writer wrote shows up as the next component's version byte being wrong, which is an error with the component's name, never a machine quietly loaded from misaligned data.
/// </summary>
internal static class SnesStateIo
{
    public static void Begin(BinaryWriter w, byte version) => w.Write(version);

    public static void Expect(BinaryReader r, byte version, string component)
    {
        int got = r.ReadByte();
        if (got != version) throw new InvalidDataException($"The {component} part of the state is version {got}; this build reads version {version} (the state does not match the core, or its data is misaligned).");
    }

    /// <summary>Bulk write of a plain-data array (little-endian, as every target is).</summary>
    public static void Put<T>(BinaryWriter w, T[] a) where T : unmanaged => w.Write(MemoryMarshal.AsBytes(a.AsSpan()));

    /// <summary>Bulk read of a plain-data array: fills all of it or throws (a state that is cut short is an error, never a half-loaded machine).</summary>
    public static void Get<T>(BinaryReader r, T[] a) where T : unmanaged => Get(r, a.AsSpan());

    public static void Get<T>(BinaryReader r, Span<T> a) where T : unmanaged
    {
        var dst = MemoryMarshal.AsBytes(a);
        int got = 0;
        while (got < dst.Length)
        {
            int k = r.Read(dst[got..]);
            if (k <= 0) throw new EndOfStreamException("The state is cut short.");
            got += k;
        }
    }

    public static void Put(BinaryWriter w, bool[] a) { for (int i = 0; i < a.Length; i++) w.Write(a[i]); }
    public static void Get(BinaryReader r, bool[] a) { for (int i = 0; i < a.Length; i++) a[i] = r.ReadBoolean(); }

    /// <summary>A length that has to be what this machine has (the sizes of memories follow from the game): a state of another size is refused.</summary>
    public static void ExpectLength(BinaryReader r, int expected, string what)
    {
        int got = r.ReadInt32();
        if (got != expected) throw new InvalidDataException($"The state holds {got} bytes of {what}, this game has {expected}.");
    }
}
