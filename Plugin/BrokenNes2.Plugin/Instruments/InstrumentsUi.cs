// Round 6 (imodel): the real model behind the Instruments tab. It implements Ui/Contracts.cs (IInstrumentModelProvider) over the instrument model, the corruptor and the history, and registers itself through
// the UiBoot seam (Ui/UiBoot.cs), in this file, without editing a shared one.
//
// One InstrumentSession per Emulator (shared by every instance's editor, so the histories, locks and the working copy agree). A session belongs to one working copy of the frozen game's sound memory; when that copy is
// replaced by something that did not come through the session (Runaway switched off and on, a project restored, a test hook), the session forgets its histories and its Version moves, so the tab starts again from what is there.
// Every manual edit and every corruption goes: model -> history (clean + operations) -> Emulator.ApplyInstrument (the Runaway copy; each instance's sampler is prepared on this thread) -> the notes FL plays.
using System.Runtime.CompilerServices;
using BrokenNes2.Instruments;
using Im = BrokenNes2.Instruments;
using IInstrument = BrokenNes2.Instruments.Instrument;
using MBlob = BrokenNes2.Instruments.Blob;
using MField = BrokenNes2.Instruments.Field;

namespace BrokenNes2.Ui;

internal static partial class UiBoot
{
    static partial void RegisterInstrumentModel() => UiServices.Instruments = emulator => new EmulatorInstrumentProvider(InstrumentSession.For(emulator));
}

/// <summary>What the Instruments tab hands the preset library for an instrument (<see cref="PresetDraft.Payload"/>): the instrument as it is now, the clean one, and the history that led from one to the other, all in the saved forms of
/// InstrumentCodec / InstrumentHistory, plus the facts a preset engine needs besides the instrument itself (<see cref="Context"/>).</summary>
/// <param name="Packed">The instrument as it is now (<see cref="InstrumentCodec.Pack"/>).</param>
/// <param name="CleanPacked">The instrument as the game had it.</param>
/// <param name="HistoryBytes">The operations (seeds included) from the clean instrument to this one (<see cref="InstrumentHistory.Serialize"/>), cursor at the state shown.</param>
/// <param name="Context">SNES: the 128 S-DSP registers of the working copy (the echo, noise rate and master volume the sampler plays with); Master System / Genesis: PSG clock (i32), PSG variant (u8), YM2612 clock (i32); Game Boy: model (1 = Color); NES: empty.</param>
public sealed record InstrumentPayload(byte[] Packed, byte[] CleanPacked, byte[] HistoryBytes, ConsoleId Console, int Index, byte[] Context);

/// <summary>The hook "Push into the game" calls with the emulator, the edited instrument and the instrument as the game had it (null when it was never edited). The `inject` work sets it (Instruments/InstrumentPushHandler.cs); with none set the tab says it cannot.</summary>
public static class InstrumentPush
{
    public static Func<Emulator, IInstrument, IInstrument?, ActionResult>? Handler { get; set; } = EmulatorPush.Handle;
}

internal sealed class InstrumentSession
{
    private static readonly ConditionalWeakTable<Emulator, InstrumentSession> Table = new();
    public static InstrumentSession For(Emulator e) => Table.GetValue(e, x => new InstrumentSession(x));

    public sealed class Entry
    {
        public InstrumentHistory History;
        /// <summary>What the user has locked, as the tab shows it. The history gets these as lock operations when a corruption tool is next used.</summary>
        public readonly HashSet<string> Wanted = new();
        public Entry(IInstrument clean) { History = new InstrumentHistory(clean); }
    }

    public readonly Emulator Emu;
    private object? known;
    private long version = 1;
    private IReadOnlyList<IInstrument>? cache;
    private readonly Dictionary<string, Entry> entries = new();

    private InstrumentSession(Emulator emu) { Emu = emu; }

    private object? CurrentSnapshot() => Emu.Runaway ? Emu.Snapshot ?? (object?)Emu.GbState ?? Emu.SegaState : null;

    /// <summary>Starts again when the working copy is not the one this session has been editing (or watching).</summary>
    public void Sync()
    {
        var cur = CurrentSnapshot();
        if (ReferenceEquals(cur, known)) return;
        entries.Clear(); cache = null; known = cur; version++;
    }

    public long Version { get { Sync(); return version; } }

    public IReadOnlyList<IInstrument> List()
    {
        Sync();
        return cache ??= Emu.InstrumentSource?.List() ?? Array.Empty<IInstrument>();
    }

    public Entry EntryFor(IInstrument inst)
    {
        string key = $"{inst.Kind}:{inst.Index}";
        if (!entries.TryGetValue(key, out var e)) entries[key] = e = new Entry(inst);
        return e;
    }

    public Entry? ExistingEntry(IInstrument inst) => entries.TryGetValue($"{inst.Kind}:{inst.Index}", out var e) ? e : null;

    /// <summary>The working copy changed through this session: keep the histories, show the new copy.</summary>
    public void Applied() { known = CurrentSnapshot(); cache = null; version++; }

    public void Touch() => version++;

    public static ulong NewSeed() => (ulong)Random.Shared.NextInt64() ^ (ulong)Environment.TickCount64 << 20;
}

internal sealed class EmulatorInstrumentProvider : IInstrumentModelProvider
{
    private readonly InstrumentSession s;
    public EmulatorInstrumentProvider(InstrumentSession session) { s = session; }

    public string? Unavailable => s.Emu.InstrumentSource == null ? "This game has no instruments BrokenNes2 can edit: a NES game is frozen and silenced by Instrument Runaway, but nothing plays its sound registers yet" : null;
    public long Version => s.Version;

    // ---- reading ----

    private IInstrument? Get(int i, out string? why)
    {
        var list = s.List();
        if ((uint)i >= (uint)list.Count) { why = "no such instrument"; return null; }
        why = null;
        return list[i];
    }

    public IReadOnlyList<InstrumentSummary> Instruments
    {
        get
        {
            var list = s.List();
            var r = new InstrumentSummary[list.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var inst = list[i];
                var e = s.ExistingEntry(inst);
                bool edited = e != null && e.History.Modified;
                r[i] = new InstrumentSummary(inst.Name, Detail(inst) + (edited ? ", edited" : "") + (s.Emu.IsPushed(inst) ? ", in the game" : "") + (inst.Editable ? "" : ", read-only"), inst.Sounding);
            }
            return r;
        }
    }

    private static string Detail(IInstrument inst)
    {
        switch (inst.Kind)
        {
            case InstrumentKind.SnesSample:
            {
                int blocks = inst.Blobs[0].Data.Length / 9;
                bool loops = blocks > 0 && (inst.Blobs[0].Data[^9] & 2) != 0;
                return $"${inst.Origin:X4}, {blocks} blocks, {blocks * 0.5:0.#} ms, {(loops ? "loops" : "one-shot")}";
            }
            case InstrumentKind.OpnDac: return inst.Facts.Count > 0 ? inst.Facts[0].Text : "DAC sound";
            case InstrumentKind.OpnPatch: return $"algorithm {inst.Get("alg")}, feedback {inst.Get("fb")}";
            case InstrumentKind.OpllUser: return "8 registers, played by the Instrument parameter's first entry";
            case InstrumentKind.OpllRom: return "built into the YM2413";
            case InstrumentKind.PsgTones or InstrumentKind.PsgNoise: return "no audible fields: the sampler plays the PSG from FL's pitch and level";
            case InstrumentKind.GbWave: return "32 four-bit samples";
            default: return inst.Description;
        }
    }

    public IReadOnlyList<InstrumentField> Fields(int instrument)
    {
        var inst = Get(instrument, out _);
        if (inst == null) return [];
        var wanted = s.ExistingEntry(inst)?.Wanted;
        var r = new InstrumentField[inst.Fields.Length];
        for (int k = 0; k < r.Length; k++)
        {
            var f = inst.Fields[k];
            var kind = f.Kind switch { Im.FieldKind.Enum => FieldKind.Enum, Im.FieldKind.Bits => FieldKind.BitField, Im.FieldKind.Signed => FieldKind.Signed, _ => FieldKind.Range };
            string why = f.Explanation + (f.Audible == Audible.No ? $" Not played today: {f.AudibleNote}" : "");
            r[k] = new InstrumentField(f.Id, f.Name, kind, f.Min, f.Max, f.Value, why, wanted?.Contains(f.Id) == true, f.Names, f.Group);
        }
        return r;
    }

    public IReadOnlyList<InstrumentBlob> Blobs(int instrument)
    {
        var inst = Get(instrument, out _);
        if (inst == null) return [];
        var wanted = s.ExistingEntry(inst)?.Wanted;
        var r = new InstrumentBlob[inst.Blobs.Length];
        for (int k = 0; k < r.Length; k++)
        {
            var b = inst.Blobs[k];
            var (samples, rate, ls, le) = InstrumentView.Decode(inst, b);
            r[k] = new InstrumentBlob(b.Id, b.Name, InstrumentView.Kind(b.Kind), b.Data, samples, rate, ls, le, inst.Editable, wanted?.Contains(b.Id) == true, b.Audible == Audible.No ? $"Not played today: {b.AudibleNote}" : null);
        }
        return r;
    }

    // ---- editing ----

    private (IInstrument? Inst, InstrumentSession.Entry? Entry, ActionResult Refusal) Open(int instrument)
    {
        var inst = Get(instrument, out string? why);
        if (inst == null) return (null, null, ActionResult.Refused(why!));
        if (!inst.Editable) return (null, null, ActionResult.Refused(inst.ReadOnlyReason ?? "this instrument cannot be changed"));
        return (inst, s.EntryFor(inst), default);
    }

    /// <summary>Runs one operation on the history and writes the result into the Runaway copy; if the copy refuses it, or a random tool changed nothing, the history goes back to where it was and the reason is the answer.</summary>
    private ActionResult Run(int instrument, bool randomTool, Func<InstrumentHistory, InstrumentSession.Entry, OpResult> op)
    {
        var (inst, entry, refusal) = Open(instrument);
        if (inst == null) return refusal;
        var h = entry!.History;
        int before = h.Cursor;
        var r = op(h, entry);
        if (!r.Changed)
        {
            h.GoTo(before); h.DropRedo();
            return ActionResult.Refused(r.Summary.Length > 0 ? r.Summary : "nothing changed");
        }
        // (InstrumentMutants.ProviderSkipsApply is a test-only bug: the edit stays in the history and never reaches the game, which the tab scenario must notice)
        var w = InstrumentMutants.On(InstrumentMutants.ProviderSkipsApply) ? WriteResult.Success(h.Current, []) : s.Emu.ApplyInstrument(h.Current);
        if (!w.Ok)
        {
            h.GoTo(before); h.DropRedo();
            return ActionResult.Refused(w.Reason ?? "the game's sound memory refused the change");
        }
        s.Applied();
        return ActionResult.Done(r.Summary);
    }

    private static void SyncLocks(InstrumentHistory h, HashSet<string> wanted)
    {
        foreach (var id in wanted) if (!h.Locks.Contains(id)) h.Lock(id);
        foreach (var id in h.Locks.ToArray()) if (!wanted.Contains(id)) h.Unlock(id);
    }

    public ActionResult SetField(int instrument, string fieldId, int value) =>
        Run(instrument, false, (h, e) =>
        {
            var f = h.Current.Field(fieldId);
            if (f == null) return new OpResult(false, "no such field");
            var r = h.SetCoalesced(fieldId, value);
            return new OpResult(true, $"{f.Name} is now {h.Current.Field(fieldId)!.Text}");
        });

    public ActionResult SetFieldLocked(int instrument, string fieldId, bool locked)
    {
        var inst = Get(instrument, out string? why);
        if (inst == null) return ActionResult.Refused(why!);
        if (inst.Field(fieldId) == null) return ActionResult.Refused("no such field");
        var e = s.EntryFor(inst);
        if (locked) e.Wanted.Add(fieldId); else e.Wanted.Remove(fieldId);
        s.Touch();
        return ActionResult.Done($"{inst.Field(fieldId)!.Name} {(locked ? "locked: Randomise and Nudge leave it alone" : "unlocked")}");
    }

    public ActionResult SetBlobByte(int instrument, string blobId, int offset, byte value) =>
        Run(instrument, false, (h, e) =>
        {
            var b = h.Current.Blob(blobId);
            if (b == null) return new OpResult(false, "no such blob");
            if (offset < 0 || offset >= b.Data.Length) return new OpResult(false, $"byte {offset} is outside the {b.Data.Length}-byte {b.Name}");
            var r = h.SetBytes(blobId, offset, [value]);
            return new OpResult(true, $"byte {offset:X4} of the {b.Name} is now {value:X2}");
        });

    public ActionResult SetBlobLocked(int instrument, string blobId, bool locked)
    {
        var inst = Get(instrument, out string? why);
        if (inst == null) return ActionResult.Refused(why!);
        if (inst.Blob(blobId) == null) return ActionResult.Refused("no such blob");
        var e = s.EntryFor(inst);
        if (locked) e.Wanted.Add(blobId); else e.Wanted.Remove(blobId);
        s.Touch();
        return ActionResult.Done($"the {inst.Blob(blobId)!.Name} {(locked ? "locked: Randomise and Nudge leave it alone" : "unlocked")}");
    }

    public ActionResult Randomise(int instrument, double strength) =>
        Run(instrument, true, (h, e) =>
        {
            if (!(strength > 0)) return new OpResult(false, "strength 0 changes nothing");
            if (!InstrumentMutants.On(InstrumentMutants.ProviderIgnoresLocks)) SyncLocks(h, e.Wanted);
            bool keep = strength <= 0.5;               // above half strength the structure of a sample may break on purpose (the chance rises with the strength)
            var r = h.Randomise(strength, InstrumentSession.NewSeed(), keep);
            return r.Changed ? new OpResult(true, $"{r.Summary} at strength {strength:0%}{(keep ? "" : " (the sample's structure may break)")}") : r;
        });

    public ActionResult Nudge(int instrument, string? fieldId, int direction) =>
        Run(instrument, true, (h, e) =>
        {
            if (direction == 0) return new OpResult(false, "a nudge goes up or down");
            SyncLocks(h, e.Wanted);
            ulong seed = InstrumentSession.NewSeed();
            var cur = h.Current;
            string target;
            if (fieldId != null)
            {
                if (cur.Field(fieldId) == null) return new OpResult(false, "no such field");
                if (e.Wanted.Contains(fieldId)) return new OpResult(false, $"{cur.Field(fieldId)!.Name} is locked");
                target = fieldId;
            }
            else
            {
                // the model picks: one unlocked field or blob, drawn with the operation's own seed
                var pool = cur.Fields.Select(f => f.Id).Concat(cur.Blobs.Where(b => b.Data.Length > 0).Select(b => b.Id)).Where(id => !e.Wanted.Contains(id)).ToArray();
                if (pool.Length == 0) return new OpResult(false, "everything is locked");
                target = pool[new Prng(seed).Below((uint)pool.Length)];
            }
            var field = cur.Field(target);
            OpResult r;
            if (field == null) r = h.Nudge(target, Math.Sign(direction), seed, Math.Max(1, cur.Blob(target)!.Data.Length / 32), true);
            else if (field.Kind == Im.FieldKind.Bits) r = h.FlipBits(target, 1, seed);
            else r = h.Nudge(target, Math.Sign(direction) * Math.Max(1, (field.Max - field.Min) / 64), seed);
            return r;
        });

    // ---- history ----

    private static bool IsLock(InstrumentOp op) => op.Kind is OpKind.Lock or OpKind.Unlock;

    private InstrumentSession.Entry? EntryOf(int instrument) { var inst = Get(instrument, out _); return inst == null ? null : s.ExistingEntry(inst); }

    public bool CanUndo(int instrument)
    {
        var h = EntryOf(instrument)?.History;
        if (h == null) return false;
        for (int k = 0; k < h.Cursor; k++) if (!IsLock(h.Operations[k])) return true;
        return false;
    }

    public bool CanRedo(int instrument)
    {
        var h = EntryOf(instrument)?.History;
        if (h == null) return false;
        for (int k = h.Cursor; k < h.Operations.Count; k++) if (!IsLock(h.Operations[k])) return true;
        return false;
    }

    private ActionResult Move(int instrument, bool undo)
    {
        var (inst, entry, refusal) = Open(instrument);
        if (inst == null) return refusal;
        var h = entry!.History;
        if (undo ? !CanUndo(instrument) : !CanRedo(instrument)) return ActionResult.Refused(undo ? "nothing to undo" : "nothing to redo");
        int before = h.Cursor, c = before;
        if (undo)
        {
            do { c--; } while (c > 0 && IsLock(h.Operations[c]));              // the step undone, and a lock change that was only waiting for it
            while (c > 0 && IsLock(h.Operations[c - 1])) c--;
        }
        else
        {
            while (c < h.Operations.Count && IsLock(h.Operations[c])) c++;
            if (c < h.Operations.Count) c++;
        }
        h.GoTo(c);
        var w = undo && InstrumentMutants.On(InstrumentMutants.UndoNotApplied) ? WriteResult.Success(h.Current, []) : s.Emu.ApplyInstrument(h.Current);
        if (!w.Ok) { h.GoTo(before); return ActionResult.Refused(w.Reason ?? "the game's sound memory refused the change"); }
        s.Applied();
        return ActionResult.Done(undo ? "undone" : "redone");
    }

    public ActionResult Undo(int instrument) => Move(instrument, true);
    public ActionResult Redo(int instrument) => Move(instrument, false);

    public string HistoryText(int instrument)
    {
        var h = EntryOf(instrument)?.History;
        if (h == null) return "";
        int total = h.Operations.Count(o => !IsLock(o)), done = h.Operations.Take(h.Cursor).Count(o => !IsLock(o));
        return total == 0 ? "" : $"step {done} of {total}";
    }

    public string SeedText(int instrument)
    {
        var h = EntryOf(instrument)?.History;
        if (h == null) return "";
        for (int k = h.Cursor - 1; k >= 0; k--)
            if (h.Operations[k].Kind is OpKind.Randomise or OpKind.Nudge or OpKind.FlipBits) return h.Operations[k].Seed.ToString("X16");
        return "";
    }

    // ---- to the Presets tab and into the game ----

    public (PresetDraft? Draft, string? Refusal) TakeForPreset(int instrument)
    {
        var inst = Get(instrument, out string? why);
        if (inst == null) return (null, why);
        var h = s.ExistingEntry(inst)?.History ?? new InstrumentHistory(inst);
        var now = h.Current;
        var snap = s.Emu.Snapshot as object ?? (object?)s.Emu.GbState ?? s.Emu.SegaState;
        var payload = new InstrumentPayload(InstrumentCodec.Pack(now), InstrumentCodec.Pack(h.Clean), h.Serialize(), now.Console, now.Index, BrokenNes2.Presets.PresetContext.Of(snap, now));      // (what a preset engine needs besides the instrument: Presets/PresetPlay.cs)
        string console = now.Console switch { ConsoleId.Nes => "NES", ConsoleId.GameBoy => "GB", ConsoleId.Snes => "SNES", ConsoleId.MasterSystem => "SMS", _ => "MD" };
        string game = s.Emu.RomPath is { } p ? Path.GetFileName(p) : s.Emu.RomDisplayName;
        int steps = h.Operations.Take(h.Cursor).Count(o => !IsLock(o));
        string notes = steps == 0 ? "As the game had it." : $"Corrupted: {steps} step{(steps == 1 ? "" : "s")} from the game's own ({string.Join(", ", h.Operations.Take(h.Cursor).Where(o => !IsLock(o)).Take(6).Select(o => o.ToString()))}{(steps > 6 ? ", ..." : "")}).";
        return (new PresetDraft(steps == 0 ? now.Name : now.Name + " (corrupted)", console, game, notes, payload), null);
    }

    public ActionResult PushIntoGame(int instrument)
    {
        var inst = Get(instrument, out string? why);
        if (inst == null) return ActionResult.Refused(why!);
        var handler = InstrumentPush.Handler;
        if (handler == null) return ActionResult.Refused("Pushing an edit into the running game is not built yet. The edit is already in the frozen copy FL's notes play");
        var h = s.ExistingEntry(inst)?.History;
        var r = handler(s.Emu, h?.Current ?? inst, h?.Clean);
        s.Touch();           // the list marks the pushed instruments
        return r;
    }

    public bool IsPushed(int instrument) { var inst = Get(instrument, out _); return inst != null && s.Emu.IsPushed(inst); }

    public ActionResult RemoveFromGame(int instrument)
    {
        var inst = Get(instrument, out string? why);
        if (inst == null) return ActionResult.Refused(why!);
        var o = s.Emu.RemovePush(inst);
        s.Touch();
        return o.Ok ? ActionResult.Done(o.Message) : ActionResult.Refused(o.Message);
    }

    public string? Notice => s.Emu.VerifyPushes() ?? s.Emu.InjectNote ?? s.Emu.InstrumentNote;
}

/// <summary>What the tab can draw of an instrument's blobs, and the small translations between the model and the contract.</summary>
internal static class InstrumentView
{
    public static BlobKind Kind(Im.BlobKind k) => k switch { Im.BlobKind.Brr => BlobKind.Brr, Im.BlobKind.Pcm8 => BlobKind.Pcm8, Im.BlobKind.Nibbles => BlobKind.WaveNibbles, Im.BlobKind.Bytes => BlobKind.Dmc, _ => BlobKind.Other };

    /// <summary>The blob as 16-bit samples for the waveform, with the rate it plays at and the loop as sample indices (-1 for none).</summary>
    public static (short[] Samples, int Rate, int LoopStart, int LoopEnd) Decode(IInstrument inst, MBlob b)
    {
        switch (b.Kind)
        {
            case Im.BlobKind.Brr:
            {
                var s = DecodeBrr(b.Data);
                int blocks = b.Data.Length / 9;
                bool loops = blocks > 0 && (b.Data[^9] & 2) != 0;
                int rel = inst.Get("loop.addr") - inst.Origin;
                if (!loops || rel < 0 || rel % 9 != 0 || rel / 9 >= blocks) return (s, 32000, -1, -1);
                return (s, 32000, rel / 9 * 16, blocks * 16);
            }
            case Im.BlobKind.Pcm8:
            {
                var s = new short[b.Data.Length];
                for (int i = 0; i < s.Length; i++) s[i] = (short)((b.Data[i] - 128) * 256);
                bool loop = inst.Field("dac.loop") != null && inst.Get("dac.loop") != 0;
                return (s, inst.Field("dac.rate")?.Value ?? 8000, loop ? inst.Get("dac.loop.start") : -1, loop ? inst.Get("dac.loop.end") : -1);
            }
            case Im.BlobKind.Nibbles:
            {
                var s = new short[b.Data.Length * 2];
                for (int i = 0; i < s.Length; i++) s[i] = (short)(((i & 1) == 0 ? b.Data[i >> 1] >> 4 : b.Data[i >> 1] & 15) - 8 << 12);
                return (s, 8372, 0, s.Length);               // 32 samples per cycle: 8372 Hz is the rate at middle C
            }
            default:
            {
                // the NES DMC: one bit per step, least significant bit first, the output level 0-127 stepping by 2
                var s = new short[b.Data.Length * 8];
                int level = 64;
                for (int i = 0; i < s.Length; i++)
                {
                    level = Math.Clamp(level + ((b.Data[i >> 3] >> (i & 7) & 1) != 0 ? 2 : -2), 0, 126);
                    s[i] = (short)((level - 64) * 512);
                }
                int[] period = [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54];
                int rate = inst.Field("rate") != null ? 1789773 / period[Math.Clamp(inst.Get("rate"), 0, 15)] : 8000;
                return (s, rate, -1, -1);
            }
        }
    }

    /// <summary>BRR decoded with the standard filters, for display (the DSP's own path is the sampler's business, not the picture's).</summary>
    public static short[] DecodeBrr(byte[] d)
    {
        int blocks = d.Length / 9;
        var o = new short[blocks * 16];
        int p1 = 0, p2 = 0;
        for (int b = 0; b < blocks; b++)
        {
            int header = d[b * 9], shift = header >> 4, filter = header >> 2 & 3;
            for (int i = 0; i < 16; i++)
            {
                int n = (i & 1) == 0 ? d[b * 9 + 1 + i / 2] >> 4 : d[b * 9 + 1 + i / 2] & 15;
                int s = (n ^ 8) - 8;
                s = shift <= 12 ? (s << shift) >> 1 : s < 0 ? -2048 : 0;
                switch (filter)
                {
                    case 1: s += p1 - (p1 >> 4); break;
                    case 2: s += (p1 << 1) - (p1 * 3 >> 5) - p2 + (p2 >> 4); break;
                    case 3: s += (p1 << 1) - (p1 * 13 >> 6) - p2 + (p2 * 3 >> 4); break;
                }
                s = Math.Clamp(s, -16384, 16383);
                p2 = p1; p1 = s;
                o[b * 16 + i] = (short)(s * 2);
            }
        }
        return o;
    }

    public static byte[] ContextOf(object? snap)
    {
        switch (snap)
        {
            case SpcSnapshot s: return (byte[])s.Dsp.Clone();
            case SegaSnapshot g:
            {
                var b = new byte[9];
                BitConverter.TryWriteBytes(b.AsSpan(0), g.PsgClock); b[4] = (byte)g.PsgVariant; BitConverter.TryWriteBytes(b.AsSpan(5), g.OpnClock);
                return b;
            }
            case GbSnapshot gb: return [(byte)(gb.Cgb ? 1 : 0)];
            default: return [];
        }
    }
}
