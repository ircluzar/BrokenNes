using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The cartridge's save state: its RAM (the battery RAM of a plain board; the Super FX's and the SA-1's work RAM live in the same array, which their chips use directly) and the count of CPU writes to it. The ROM is the game
/// itself and is not part of a state (a state only loads into the game it was saved from). Loading a state replaces the RAM with the saved one, as in every emulator; the battery file on disk is not touched until the app next saves it.
/// </summary>
public sealed partial class SnesCartridge
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        w.Write(Sram.Length);
        w.Write(Sram);
        w.Write(SramWrites);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "cartridge");
        SnesStateIo.ExpectLength(r, Sram.Length, "cartridge RAM");
        SnesStateIo.Get(r, Sram);
        SramWrites = r.ReadInt64();
    }
}
