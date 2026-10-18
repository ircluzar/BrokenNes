// Round 7 (midi): the test hooks of "Test MIDI export" (Emulator.Song.cs, Music/). Plugin hooks 600-619 (Fpd.TestBase + n); the window's own hooks are the editor's 620-629 (Bn2Editor.Song.cs).
using BrokenNes2.Music;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    /// <summary>600 open the MIDI file at the UTF-16 path <c>value</c> (converted on a worker; 0 started, 1 refused), 601 busy (1/0), 602 the song: 1 it is the running game, 2 its game is built and waits for the audio side, 0 held but another game runs, -1 none held,
    /// 603 copy a text into the buffer at <c>value</c>: field in the high bits of <c>index</c> (0 result, 1 the facts' lines joined by '\n', 2 the note about the game it replaced, 3 the song's name, 4 the status, 5 why the template cannot be used),
    /// the max chars in the low 16; returns the length (-1 none), 604 Play, 605 Restart, 606 Stop, 607 Reload (0 started, 1 refused), 608 follow FL's transport: <c>index</c> 1 sets it to <c>value</c>; returns it, 609 stopped (1/0),
    /// 610 write the running song's ROM to the UTF-16 path <c>value</c> (1 written, 0 not the song), 611 run exactly <c>value</c> frames and copy the APU register latch ($4000-$4017, 24 bytes) after each into the buffer at <c>index</c>
    /// (a restart asked for is applied between frames, as on the audio thread; returns the frames run, -1 no game), 612 the built-in template is usable (1) or refused (0), 613 tests: the template is read from the manifest at the UTF-16 path
    /// <c>value</c> and the ROM beside it (same name, .nes; 0 = the built-in one again; returns 0, or 1 when they cannot be read), 614 the window's stamp (counts every result), 615 a number: <c>index</c> 0 notes, 1 dropped, 2 thinned,
    /// 3 loop frames, 4 truncated channels (bit mask), 5-8 bytes per channel as patched, 9 restarts done, 10 FL's transport as last told (-1 unknown), 11 a restart copy is ready (1/0), 12 pitch bends, 13 tempo changes ignored,
    /// 616 the deliberate-bug mask (MusicMutate) = <c>value</c>, 617 frames the running game has run, 618 the song's generation (counts every load), 619 the bytes of the song section this instance would save (-1 none).</summary>
    private nint SongTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 600: { var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); return emu == null || path == null ? 1 : emu.OpenSong(path) == null ? 0 : 1; }
            case 601: return emu?.SongUi.Busy == true ? 1 : 0;
            case 602: return emu == null ? -1 : emu.SongRunning ? 1 : emu.SongPending ? 2 : emu.Song == null ? -1 : 0;
            case 603:
            {
                if (emu == null) return -1;
                int field = (int)(index >> 16), max = (int)(index & 0xFFFF);
                string? text = field switch
                {
                    0 => emu.SongUi.Result,
                    1 => emu.Song is { } s ? string.Join("\n", s.Facts.Lines()) : null,
                    2 => emu.SongUi.Note,
                    3 => emu.Song?.Name,
                    4 => emu.SongUi.Status,
                    5 => MusicTemplate.Builtin(out var why) == null ? why : "",
                    _ => null,
                };
                if (text == null) return -1;
                if (value != 0 && max > 0) text.AsSpan(0, Math.Min(text.Length, max)).CopyTo(new Span<char>((void*)value, max));
                return text.Length;
            }
            case 604: emu?.PlaySong(); return 0;
            case 605: emu?.RestartSong(); return 0;
            case 606: emu?.StopSong(); return 0;
            case 607: return emu?.ReloadSong() == null ? 0 : 1;
            case 608: if (emu == null) return -1; if (index == 1) emu.SongUi.Follow = value != 0; return emu.SongUi.Follow ? 1 : 0;
            case 609: return emu?.SongStopped == true ? 1 : 0;
            case 610:
            {
                var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value);
                var rom = emu?.SongRomForTests();
                if (rom == null || path == null) return 0;
                File.WriteAllBytes(path, rom);
                return 1;
            }
            case 611: return emu == null ? -1 : (nint)emu.RunFramesLatchForTests((int)value, (byte*)index);
            case 612: return MusicTemplate.Builtin(out _) != null ? 1 : 0;
            case 613:
            {
                if (value == 0) { MusicTemplate.Override = null; return 0; }
                var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value);
                try { MusicTemplate.Override = (File.ReadAllBytes(Path.ChangeExtension(path!, ".nes")), File.ReadAllText(path!)); return 0; }
                catch (Exception) { return 1; }
            }
            case 614: return emu?.SongUi.ResultStamp ?? 0;
            case 615:
            {
                if (index == 9) return emu?.SongRestarts ?? 0;
                if (index == 10) return emu?.Transport ?? -1;
                if (index == 11) return emu?.StandbyReady == true ? 1 : 0;
                var f = emu?.Song?.Facts;
                if (f == null) return -1;
                return index switch
                {
                    0 => f.NotesTotal, 1 => f.NotesDropped, 2 => f.ThinnedTotal, 3 => f.LoopFrames, 4 => (f.Truncated[0] ? 1 : 0) | (f.Truncated[1] ? 2 : 0) | (f.Truncated[2] ? 4 : 0) | (f.Truncated[3] ? 8 : 0),
                    >= 5 and <= 8 => f.FinalBytes[index - 5], 12 => f.PitchBends, 13 => f.TempoChangesIgnored, _ => -1,
                };
            }
            case 616: MusicMutate.Mask = (int)value; return 0;
            case 617: return (nint)(emu?.FramesRun ?? -1);
            case 618: return (nint)(emu?.Song?.Generation ?? 0);
            case 619: return emu?.CaptureSongSection()?.Length ?? -1;
        }
        return 0;
    }
}
