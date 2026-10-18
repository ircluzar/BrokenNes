// Round 8 (record, P2): live MIDI out of the emulator's SNES game (Record/RecLive.cs does the notes). Switched on in the Record window; the notes are made inside the game's frames (the
// audio thread, allocation-free) and handed to FL by the instance that pulls the game's sound (Bn2Plugin: FL's MIDIOut_Delayed, the port in the message), so they go out with the audio
// they belong to. A worker thread estimates the root of each sample the first time it is keyed. Switched off, or the game replaced: every sounding note gets its note-off.
using BrokenNes2.Record;

namespace BrokenNes2;

public sealed partial class Emulator
{
    private volatile RecLive? live;
    private Action<byte, byte, long>? liveTap;
    private SessionGame? liveGame;
    private Action<NesEmulator.Snes.APU_SFC>? unitChangedTap;
    private volatile int livePort = 1;

    /// <summary>The live MIDI out (null: off).</summary>
    internal RecLive? Live => live;
    /// <summary>The MIDI port the live notes go to (FL routes MIDI by port: the receiving channels listen on the same number).</summary>
    internal int LivePort { get => livePort; set { livePort = Math.Clamp(value, 0, 255); if (live is { } l) l.Port = livePort; } }

    /// <summary>Live MIDI out on (UI thread). Null when on, else why not.</summary>
    internal string? StartLive()
    {
        if (live is { Ended: false }) return null;
        if (live is { Ended: true, Pending: > 0 }) return "the last live notes' note-offs are still waiting for FL to pull the sound: try again in a moment";
        if (Mode != 1) return "live MIDI out needs ROM mode and a SNES game: load one in the Game list";
        if (Console != Cx.Snes) return $"live MIDI out works on SNES games for now (this emulator runs the {Cx.Names[Console]})";
        var l = new RecLive { Port = livePort };
        liveTap = l.OnWrite;                           // (made here, on the UI thread: the audio thread only sets it)
        unitChangedTap ??= OnUnitChanged;
        lock (romLock)
        {
            ApplyPending();
            live = l;
            liveGame = null;
        }
        var worker = new Thread(() =>
        {
            // first the roots of the samples loaded now (the live out listens once they are known), then each new sample's at its first key-on
            byte[]? unitAram = null; int dirReg = 0;
            lock (romLock) { if (game is SessionGame sg && sg.SpcUnit is { } spc) { unitAram = spc.Aram; dirReg = spc.Dsp.Regs[0x5D]; } }
            try { l.Prime(unitAram, dirReg); } catch (Exception e) { l.Primed = true; BrokenNes.Fruity.Diag.Log("live MIDI roots: " + e.Message); }
            while (ReferenceEquals(live, l) && !l.Ended)
            {
                try { l.EstimatePending(); } catch (Exception e) { BrokenNes.Fruity.Diag.Log("live MIDI roots: " + e.Message); }
                Thread.Sleep(40);
            }
        }) { IsBackground = true, Name = "BrokenNes2 live MIDI roots" };
        worker.Start();
        return null;
    }

    /// <summary>Live MIDI out off (UI thread): the sounding notes get their note-offs, sent by the next audio block.</summary>
    internal void StopLive()
    {
        var l = live;
        if (l == null) return;
        lock (romLock)
        {
            if (liveGame?.SpcLog is { } log && ReferenceEquals(log.LiveOut, liveTap)) log.LiveOut = null;
            liveGame = null;
            l.AllOff();
            l.Ended = true;
            if (l.Pending == 0) live = null;
        }
    }

    /// <summary>Between two frames, under romLock (allocation-free): the live out follows the game that runs (a new game: the old one's notes off, the new one listened to).</summary>
    private void LiveService(IGame? g)
    {
        var l = live;
        if (l == null || l.Ended || !l.Primed || ReferenceEquals(g, liveGame)) return;
        if (liveGame != null)
        {
            if (liveGame.SpcLog is { } old && ReferenceEquals(old.LiveOut, liveTap)) old.LiveOut = null;
            l.AllOff();
            l.ForgetRoots();                           // (the new game's samples may sit where the old one's did)
            liveGame = null;
        }
        if (g is not SessionGame sg || sg.SpcUnit is not { } spc || sg.SpcLog is not { } log) return;
        l.Attach(spc.Aram, spc.Dsp.Regs);
        log.LiveOut = liveTap;
        sg.UnitChanged = unitChangedTap;
        liveGame = sg;
    }

    private void LiveFrameEnd(IGame g)
    {
        if (live is { Ended: false } l && ReferenceEquals(g, liveGame)) l.EndFrame();
    }

    /// <summary>The game's sound unit was swapped for another (a sound-chip change), under romLock: the capture and the live out take their memory from the new one.</summary>
    private void OnUnitChanged(NesEmulator.Snes.APU_SFC spc)
    {
        live?.Rebind(spc.Aram);
        recorder?.Rebind(spc.Aram, spc.Dsp.Regs);
    }

    /// <summary>The instance that pulled the game's sound hands the queued live notes to FL (the audio thread, after the frames it asked for).</summary>
    internal void DrainLiveMidi(Bn2Plugin who)
    {
        var l = live;
        bool silent = Runaway || Mode != 1;
        if (l == null || l.Pending == 0 && !(silent && l.Sounding)) return;
        lock (romLock)
        {
            if (silent) l.AllOff();                    // a frozen game (or none) is silent: so are its live notes
            int port = l.Port;
            while (l.TryDequeue(out uint m)) who.SendMidiOut(m | (uint)port << 24);
            if (l.Ended && ReferenceEquals(live, l)) live = null;
        }
    }
}
