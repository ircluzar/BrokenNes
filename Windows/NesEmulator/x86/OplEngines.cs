// The AdLib's alternative sound engines (the APU harvest, docs/projects/apu-harvest-catalog.md): other emulators' OPL2 cores behind the card's ports. Written for BrokenNes.
// The card (Opl2: ports 388h/389h, timers, status) stays ymfm's; an engine only hears the register writes and draws the sound at its own rate.

using System;
using System.Collections.Generic;

namespace NesEmulator.X86;

/// <summary>An OPL2 sound engine ported from another emulator: register writes in, mono samples out at <see cref="Rate"/>.</summary>
public interface IOplEngine
{
    /// <summary>The engine's id on the x86 APU menu ("D072", "NUK174", ...).</summary>
    string Id { get; }
    /// <summary>The rate the engine draws at (its original's default mixer rate).</summary>
    int Rate { get; }
    /// <summary>The original applied writes only between blocks of this many samples (its mixer's callback); 0 = at once.</summary>
    int BlockSamples { get; }
    void Reset();
    /// <summary>Register <paramref name="reg"/> (0-FFh for OPL2) gets <paramref name="value"/>.</summary>
    void Write(int reg, byte value);
    /// <summary>Draws <paramref name="count"/> mono samples into <paramref name="dest"/>.</summary>
    void Generate(short[] dest, int count);
}

/// <summary>MAME 0.36's fmopl.c (already ported for the Master System FM, sega/Mame036Opl.cs) as an AdLib engine.</summary>
public sealed class Mame036OplEngine : IOplEngine
{
    private NesEmulator.Sega.Mame036Opl _chip = new(3579545, 44100);
    public string Id => "M036";
    public int Rate => 44100;
    public int BlockSamples => 0;
    public void Reset() => _chip = new NesEmulator.Sega.Mame036Opl(3579545, 44100);
    public void Write(int reg, byte value) { _chip.Write(0, reg); _chip.Write(1, value); }
    public void Generate(short[] dest, int count) { for (int i = 0; i < count; i++) dest[i] = _chip.Update(); }
}

/// <summary>The AdLib engines on the x86 APU menu: id -> factory (null = the card's own ymfm engine).</summary>
public static class OplEngines
{
    /// <summary>Every engine class in the assembly (a public sealed IOplEngine with a parameterless constructor), by its <see cref="IOplEngine.Id"/>: a new port registers itself by existing.</summary>
    public static readonly Dictionary<string, Func<IOplEngine>> Factories = Discover();

    private static Dictionary<string, Func<IOplEngine>> Discover()
    {
        var d = new Dictionary<string, Func<IOplEngine>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in typeof(IOplEngine).Assembly.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract || !typeof(IOplEngine).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) == null) continue;
            var tt = t;
            var id = ((IOplEngine)Activator.CreateInstance(tt)!).Id;
            d[id] = () => (IOplEngine)Activator.CreateInstance(tt)!;
        }
        return d;
    }
}
