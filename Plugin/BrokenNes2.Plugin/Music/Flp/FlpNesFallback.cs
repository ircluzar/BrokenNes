// NES is the inter-console standard (the user's decision 2026-11-10, built 2026-11-12): a back end for another console (CTW SMS, Vaults GB / GG, Vaults DOS / 95 through Vaults GG)
// that finds no instance of its own in a song reads the song's NES instances (Direct mode, notes, channel pulse 1 / pulse 2 / triangle / noise, not a preset chip) as its own channels
// of the same number, and the fence says so in a note line. A song that has an instance for the console is read as before (its NES instances stay out, said by the console's fence).
// The song model is never changed: the back end builds from a copy. Pure.
namespace BrokenNes2.Music.Flp;

public static class FlpNesFallback
{
    /// <summary>Deliberate bug for flp-test: the fallback is never applied (a NES-only song is NOTHING for the other consoles, as before).</summary>
    public const int OffMutant = 1 << 31;

    /// <summary>The song <paramref name="song"/> as the back end of <paramref name="game"/> reads it: when no BrokenNes2 instance with notes is <paramref name="native"/> (a Direct instance on
    /// the console's own chip, or a preset instance the back end plays), a copy whose NES Direct instances sit on console <paramref name="to"/>; the note line goes to <paramref name="fence"/>.</summary>
    public static FlpSongModel Apply(FlpSongModel song, Func<FlpBn2, bool> native, int to, string toName, string game, List<FenceLine> fence)
    {
        if (FlpMutants.On(OffMutant)) return song;
        static bool Preset(FlpBn2 b) => b.Core is >= FlpPresetSound.FirstPresetChip and <= FlpPresetSound.LastPresetChip;
        if (song.Instruments.Any(i => i.Notes.Count > 0 && i.Bn2 is { Mode: "direct" } b && (native(b) || Preset(b)))) return song;
        bool Moves(FlpInstrument i) => i.Notes.Count > 0 && i.Bn2 is { Console: 0, Mode: "direct" } b && b.Channel is >= 0 and <= 3 && !Preset(b);
        var moved = song.Instruments.Where(Moves).ToList();
        if (moved.Count == 0) return song;
        var copy = new FlpSongModel
        {
            FlVersion = song.FlVersion, Title = song.Title, Sha256 = song.Sha256, TimelineMode = song.TimelineMode, Arrangement = song.Arrangement,
            Ppq = song.Ppq, TempoMilli = song.TempoMilli, Bytes = song.Bytes, End = song.End, Caveats = song.Caveats,
        };
        foreach (var i in song.Instruments)
        {
            if (!Moves(i)) { copy.Instruments.Add(i); continue; }
            var b = i.Bn2!;
            copy.Instruments.Add(new FlpInstrument
            {
                Rack = i.Rack, Type = i.Type, Name = i.Name, Plugin = i.Plugin, Levels = i.Levels, Notes = i.Notes,
                Bn2 = new FlpBn2 { Version = b.Version, Params = b.Params, Console = to, ConsoleName = toName, Core = b.Core, Channel = b.Channel, Emulator = b.Emulator, Mode = b.Mode, RomPath = b.RomPath, Sections = b.Sections, Preset = b.Preset },
            });
        }
        string[] nes = ["pulse 1", "pulse 2", "triangle", "noise"];
        fence.Add(new FenceLine("console", FenceLevel.Note, $"the song has no {toName} instance: its {moved.Count} NES instance{(moved.Count == 1 ? "" : "s")} ({string.Join(", ", moved.Select(i => $"\"{i.Name}\" {nes[i.Bn2!.Channel]}"))}) play on {game}'s channels of the same number (NES is the inter-console standard)"));
        return copy;
    }
}
