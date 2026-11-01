// Round 8 (record-nes): `nesrec-test live`: the NES live MIDI out (Record/NesLive.cs) fed a game's writes in order and at their clocks (from a capture of the same frames: the very writes the game's
// tap hands it), its note-ons compared with the transcription's notes: every note (pulse, triangle, noise; not the ones sounding before it started) struck live within 400 cycles of where the
// transcription starts it, with the same key; every live note ended; nothing allocated per write. The DLL check (selftest) runs it on the game's own tap.
using BrokenNes2.Record;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static partial class NesRecChecks
{
    public static void Live(ImodelReport r)
    {
        foreach (var (pick, park, script) in new[] { ("VRUN", 60, ""), ("Adventures of Rad Gravity", 300, "300:start,4"), ("Alien 3", 300, "300:start,4"), ("Adventures of Bayou Billy", 300, "300:start,4") })
        {
            var g = pick == "VRUN" ? NesRecGame.Vrun() : NesRecGame.Find(pick, script);
            if (g == null) { r.Warn($"NOT RUN: {pick} is not in {NesRecGame.Roster}"); continue; }
            g.Run(park);
            var s = g.Record((int)(Math.Min(GameSeconds, 20) * NesAudioMachine.FramesPerSecond), "live");
            var tr = NesTranscribe.Run(s, new NesOptions { Name = "live" });
            var l = new NesLive { NoteLog = new int[200_000], NoteClock = new long[200_000] };
            l.AttachSaved(s.BaseState);
            int drained = 0;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();                          // (a collection inside the window would show in the count)
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < s.Count; i++)
            {
                s.Get(i, out uint t, out int reg, out int v);
                if (reg == NesSession.MarkerFrame) { l.EndFrame(t); while (l.TryDequeue(out _)) drained++; continue; }
                if (reg > 0x17) continue;
                l.OnWrite((ushort)(0x4000 + reg), (byte)v, t);
            }
            l.EndFrame(s.Length);
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            int ons = (int)Math.Min(l.NotesOn, l.NoteLog!.Length);
            var used = new bool[ons];
            var notes = tr.Notes.Where(n => n.Channel < 4 && !n.BeforeStart).ToList();
            int matched = 0;
            string? miss = null;
            foreach (var n in notes)
            {
                int key = n.Channel == 3 ? NesDirect.NoiseKey(n.Periods[0].V) : (int)Math.Round(NesDirect.MidiOf(n.Periods[0].V, n.Channel == 2));
                int k = -1;
                // (the triangle: the transcription starts it at its strike, the live out sends it when it sounds, at the quarter frame after: up to 7,458 cycles later)
                long late = n.Channel == 2 ? NesNotes.QuarterFrame : 0;
                for (int i = 0; i < ons && k < 0; i++)
                    if (!used[i] && l.NoteLog[i] >> 16 == n.Channel && l.NoteClock![i] - n.Start >= -400 && l.NoteClock[i] - n.Start <= 400 + late && (l.NoteLog[i] >> 8 & 0xFF) == key) k = i;
                if (k >= 0) { used[k] = true; matched++; }
                else miss ??= $"channel {n.Channel} at {n.StartSeconds:0.0000} s key {key}";
            }
            l.AllOff();
            r.Expect(matched == notes.Count, $"{g.Name}: {matched} of {notes.Count} notes of the transcription struck live at the same moment with the same key; first missed: {miss}");
            r.Expect(l.NotesOn == l.NotesOff, $"{g.Name}: {l.NotesOn} live note-ons, {l.NotesOff} note-offs after all notes off");
            r.Expect(alloc == 0, $"{g.Name}: the live out allocated {alloc:N0} bytes over {s.Count:N0} writes");
            r.Note($"{g.Name}: {matched} of {notes.Count} transcribed notes struck live at the same moment and key; {l.Describe()}; {drained:N0} messages; 0 bytes allocated");
        }
    }
}
