using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NesEmulator.Debugging.Movies;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

// =====================================================================================================================================
//  T5: hotpatch and resume (docs/projects/brokendev-timeflow-plan.md, "Hotpatch and resume" and "T5 as built").
//
//  From a rewound frame of a timeline, the game goes on with a changed ROM:
//    * byte patches: a patch list (an address in the debugger's specs: a symbol, a CPU address read through the banks mapped at that frame,
//      a banked address, a ROM offset; the old bytes; the new bytes) applied to the restored machine's ROM, refused as a whole when any old
//      byte does not match;
//    * rebuild: the project's new build, the frame's RAM and registers loaded into it, after the symbol-map check (the PC and every return
//      address on the call stack must be the same function and offset in both builds, else refused with the reason and the nearest safe resume
//      point offered: the next frame start where the check holds, in a function of the manifest's safe list when it names one).
//  Then the recorded inputs replay on the patched machine (recorded as a branch of the timeline with its patches, so it is reproducible) and the
//  new future is compared with the old one: the first frame whose picture or work RAM differs, the first instruction where the CPUs part
//  (D5's divergence finder, run in lockstep from the rewound frame) and the variables that differ. The verdicts (TimeflowVerdict.cs) are the
//  tests the agent or the user wrote, evaluated over the replayed span.
// =====================================================================================================================================

/// <summary>One byte patch: where (a debugger address spec on a CPU), the bytes there now and the bytes to put.</summary>
public sealed class RomPatch
{
    public string At = "";
    public string Cpu = "main";
    public byte[] Old = Array.Empty<byte>(), New = Array.Empty<byte>();

    public static string Help => "AT=OLD>NEW: AT a symbol (ft_limit+1), a CPU address ($C073, read through the banks mapped at that frame), a banked address (02:4000) or a ROM offset (rom:$73 / prg:$73); OLD and NEW hex bytes of the same length (\"21>10\", \"E0 21>E0 10\"); [cpu:ID@] before AT for another CPU";

    /// <summary>"AT=OLD>NEW" ("ft_limit+1=21>10", "rom:$72=E0 21>E0 10", "z80@$0100=00>C9").</summary>
    public static RomPatch Parse(string text)
    {
        text = (text ?? "").Trim();
        int eq = text.LastIndexOf('=');
        int gt = eq > 0 ? text.IndexOf('>', eq) : -1;
        if (eq <= 0 || gt < 0) throw new FormatException($"'{text}' is not a byte patch ({Help})");
        var p = new RomPatch { At = text[..eq].Trim() };
        int atSign = p.At.IndexOf('@');
        if (atSign > 0 && !p.At[..atSign].Contains(':')) { p.Cpu = p.At[..atSign].Trim(); p.At = p.At[(atSign + 1)..].Trim(); }
        p.Old = Hex(text[(eq + 1)..gt], text);
        p.New = Hex(text[(gt + 1)..], text);
        if (p.Old.Length == 0) throw new FormatException($"'{text}': the old bytes are needed (a patch is refused when the ROM does not hold them)");
        if (p.Old.Length != p.New.Length) throw new FormatException($"'{text}': the old bytes ({p.Old.Length}) and the new bytes ({p.New.Length}) must be as many (a patch replaces bytes, it never moves code)");
        if (p.At.Length == 0) throw new FormatException($"'{text}': no address ({Help})");
        return p;
    }

    private static byte[] Hex(string s, string whole)
    {
        string h = Regex.Replace(s, @"[\s,$]|0x", "", RegexOptions.IgnoreCase);
        if (h.Length % 2 != 0 || !Regex.IsMatch(h, "^[0-9A-Fa-f]*$")) throw new FormatException($"'{whole}': '{s.Trim()}' is not hex bytes");
        return Enumerable.Range(0, h.Length / 2).Select(i => byte.Parse(h.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
    }

    public static string Bytes(IEnumerable<byte> b) => string.Join(" ", b.Select(x => x.ToString("X2")));
    public override string ToString() => $"{(Cpu != "main" ? Cpu + "@" : "")}{At}={Bytes(Old)}>{Bytes(New)}";
}

/// <summary>A patch as it was applied: where it landed in the ROM (and the ROM file), what was there.</summary>
public sealed class AppliedPatch
{
    public string Text = "", Resolved = "";
    public uint RomOffset;
    public long FileOffset;
    public string Old = "", New = "";
    public JsonObject ToJson() => new() { ["patch"] = Text, ["resolved"] = Resolved, ["romOffset"] = $"${RomOffset:X}", ["fileOffset"] = FileOffset, ["old"] = Old, ["new"] = New };
}

/// <summary>A machine the hotpatch works with (opened by the host: Workshop's movie machine, the desktop's <see cref="ProfileMachine"/>): the timeline's console and cores on a ROM.</summary>
public sealed class HotpatchMachine : IDisposable
{
    public DebugMachine Machine { get; init; } = null!;
    public Debugger Debugger { get; init; } = null!;
    public TimeflowSource Source { get; init; } = null!;
    public Action<PadButtons, PadButtons> RunFrame { get; init; } = null!;
    public DebugPort? Port { get; init; }
    public object Game { get; init; } = null!;
    /// <summary>The ROM file the machine was opened on (as the store keeps it: with its header) and its sha256.</summary>
    public byte[] RomFile { get; init; } = Array.Empty<byte>();
    public string RomSha256 { get; init; } = "";
    public Action? OnDispose { get; init; }
    public bool Ran { get; set; }

    public ProfileTarget Target(bool poweredOn) => new() { Machine = Machine, Debugger = Debugger, Source = Source, RunFrame = Run, Port = Port, PoweredOn = poweredOn && !Ran };
    public SearchMachine ForSearch() => new() { Machine = Machine, Debugger = Debugger, Source = Source, RunFrame = Run, Port = Port, Game = Game, RomSha256 = RomSha256, OnDispose = OnDispose };
    public void Run(PadButtons a, PadButtons b) { Ran = true; RunFrame(a, b); }
    public void Dispose() { try { OnDispose?.Invoke(); } catch (Exception) { } }
}

/// <summary>Opens a machine of a timeline's console, cores and power-on, on <paramref name="romFile"/> (null: the timeline's own stored ROM), with the debugger and these symbols.</summary>
public delegate HotpatchMachine HotpatchOpener(string timelineDir, TimelineMeta meta, byte[]? romFile, IReadOnlyList<string> symbols);

/// <summary>The rebuild's symbol-map check at one frame start: the PC and each return address, named in both builds.</summary>
public sealed class ResumeCheck
{
    public long Frame;
    public bool Safe;
    public string Why = "";
    public List<JsonObject> Places = new();
    public string? Function;
    public JsonObject ToJson() => new()
    {
        ["frame"] = Frame, ["safe"] = Safe, ["why"] = Why.Length > 0 ? Why : null, ["function"] = Function,
        ["places"] = new JsonArray(Places.Select(p => (JsonNode)p.DeepClone()).ToArray()),
    };
}

/// <summary>What to hotpatch, from where, and what to check afterwards.</summary>
public sealed class HotpatchRequest
{
    public string TimelineDir = "";
    /// <summary>The rewound frame: the patched game starts there (its state at the frame's start).</summary>
    public long Frame;
    /// <summary>The last frame replayed (inclusive); null: the timeline's last frame.</summary>
    public long? To;
    /// <summary>Byte patches (mode "bytes"), or a new build (mode "rebuild": <see cref="NewRom"/>).</summary>
    public List<RomPatch> Patches = new();
    public byte[]? NewRom;
    public string NewRomName = "";
    public List<string> NewSymbols = new();
    /// <summary>What was built (the project and its build record), for the branch's description.</summary>
    public string? Build;
    /// <summary>The timeline's (old build's) symbols.</summary>
    public List<string> Symbols = new();
    /// <summary>Rebuild: the functions a resume may start in when the rewound frame is refused (the manifest's <c>timeflow.safe</c>); empty: any frame start that passes.</summary>
    public List<string> SafeFunctions = new();
    public int SafeScanFrames = 600;
    /// <summary>Rebuild: when the frame is refused and a safe point is found, resume there (said) instead of only offering it.</summary>
    public bool ResumeAtSafePoint;
    /// <summary>Record the new future as a branch of the timeline (needed for verdicts: they are evaluated over the recorded branch).</summary>
    public bool Branch = true;
    /// <summary>The branch's index: coverage and access counts (the verdicts' searches use them).</summary>
    public bool Index = true;
    public bool Divergence = true;
    public List<VerdictTest> Verdicts = new();
    /// <summary>Also evaluate each verdict on the old future (the timeline itself, the same frames): the comparison the fix is judged by.</summary>
    public bool VerdictOnOld = true;
    public HotpatchOpener Open = null!;
    public Func<bool>? Cancel;
    public Action<string>? Progress;
}

/// <summary>What a hotpatch did: the patches (or the rebuild and its check), the resume, the branch, the divergence, the verdicts.</summary>
public sealed class HotpatchReport
{
    public string Mode = "", TimelineId = "", Console = "";
    public long Frame, To, ResumedAt;
    public bool Resumed;
    public string? Refused;
    public List<AppliedPatch> Applied = new();
    public ResumeCheck? Check;
    public ResumeCheck? SafePoint;
    public string? Branch, BranchDir;
    public string PatchedRomSha256 = "";
    public long FramesReplayed;
    public JsonObject? Divergence;
    public List<VerdictResult> Verdicts = new();
    public List<string> Notes = new();
    public double Ms;

    /// <summary>Every verdict passed on the patched future (false with no verdicts, said by <see cref="VerdictSummary"/>).</summary>
    public bool VerdictsPass => Verdicts.Count > 0 && Verdicts.All(v => v.Pass);
    public string VerdictSummary => Verdicts.Count == 0 ? "no verdict was asked for" : $"{Verdicts.Count(v => v.Pass)} of {Verdicts.Count} verdict(s) pass on the patched future";

    public JsonObject ToJson()
    {
        var j = new JsonObject
        {
            ["mode"] = Mode, ["timeline"] = TimelineId, ["console"] = Console, ["frame"] = Frame, ["to"] = To,
            ["resumed"] = Resumed, ["resumedAt"] = Resumed ? ResumedAt : null, ["refused"] = Refused,
            ["patches"] = new JsonArray(Applied.Select(a => (JsonNode)a.ToJson()).ToArray()),
            ["check"] = Check?.ToJson(), ["safeResume"] = SafePoint?.ToJson(),
            ["branch"] = Branch, ["branchDir"] = BranchDir, ["patchedRomSha256"] = PatchedRomSha256.Length > 0 ? PatchedRomSha256 : null,
            ["framesReplayed"] = FramesReplayed, ["divergence"] = Divergence?.DeepClone(),
            ["verdicts"] = new JsonArray(Verdicts.Select(v => (JsonNode)v.ToJson()).ToArray()),
            ["verdictsPass"] = Verdicts.Count > 0 ? VerdictsPass : null, ["verdictSummary"] = VerdictSummary,
            ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()), ["ms"] = Math.Round(Ms),
        };
        return j;
    }
}

public static class TimeflowHotpatch
{
    private const int RingSize = 1 << 18;

    // ------------------------------------------------------------------------------------------------ byte patches

    /// <summary>
    /// Applies byte patches to a machine's ROM image (it is at the rewound frame: a CPU address is read through the banks mapped now). Every patch is
    /// resolved and every old byte checked before anything is written: one mismatch refuses them all, with each mismatch said. Returns the applied
    /// patches and the patched ROM file (the machine's own file with the same bytes changed, for the branch's store).
    /// </summary>
    public static (List<AppliedPatch> Applied, byte[] File) ApplyBytes(HotpatchMachine m, IReadOnlyList<RomPatch> patches)
    {
        if (patches.Count == 0) throw new ArgumentException("no byte patch given (" + RomPatch.Help + ")");
        var plan = new List<(RomPatch P, DebugCpu Cpu, int Space, uint Off, string Resolved)>();
        var wrong = new List<string>();
        foreach (var p in patches)
        {
            var cpu = m.Machine.Cpu(p.Cpu) ?? throw new FormatException($"'{p}': no CPU '{p.Cpu}' on the {m.Machine.Console} (CPUs: {string.Join(", ", m.Machine.Cpus.Select(c => c.Id))})");
            var rom = cpu.Spaces.FirstOrDefault(s => s.Is("rom")) ?? throw new NotSupportedException($"the {cpu.Name} has no ROM space: nothing to patch");
            var ranges = Debugger.ResolveSpec(cpu, m.Debugger.Symbols, p.At, AccessKind.Read, out string resolved);
            if (ranges.Count == 0) throw new FormatException($"'{p}': {p.At} resolves to nothing");
            var r0 = ranges[0];
            uint off;
            if (r0.Space == rom.Id) off = r0.Start;
            else if (r0.Space == 0)
            {
                // a CPU address: the ROM byte it reads now (the banks mapped at the rewound frame); every byte of the patch must be ROM, contiguous
                Span<SpaceKey> keys = stackalloc SpaceKey[4];
                long first = -1;
                for (int i = 0; i < p.Old.Length; i++)
                {
                    uint a = cpu.NextAddress(r0.Start, i);
                    int n = cpu.AccessKeys(a, false, keys);
                    long found = -1;
                    for (int k = 0; k < n; k++) if (keys[k].Space == rom.Id) found = keys[k].Offset;
                    if (found < 0) throw new FormatException($"'{p}': {cpu.FormatAddress(a)} is not ROM at this frame ({resolved}): a byte patch changes the ROM (RAM is changed with a memory write)");
                    if (i == 0) first = found;
                    else if (found != first + i) throw new FormatException($"'{p}': the patch's bytes are not contiguous in the ROM at this frame ({cpu.FormatAddress(a)} is rom:${found:X}, not ${first + i:X}): split it at the bank boundary");
                }
                off = (uint)first;
                resolved = $"{resolved} = rom:${off:X} (the banks mapped at the frame)";
            }
            else throw new FormatException($"'{p}': {p.At} is {resolved}, not ROM: a byte patch changes the ROM (RAM is changed with a memory write)");
            if (off + (uint)p.Old.Length > rom.Size) throw new FormatException($"'{p}': rom:${off:X} + {p.Old.Length} bytes is past the ROM's end (${rom.Size:X})");
            var now = Enumerable.Range(0, p.Old.Length).Select(i => cpu.PeekSpace(rom.Id, off + (uint)i)).ToArray();
            if (!now.SequenceEqual(p.Old)) wrong.Add($"{p.At} ({resolved}) holds {RomPatch.Bytes(now)}, not {RomPatch.Bytes(p.Old)}");
            plan.Add((p, cpu, rom.Id, off, resolved));
        }
        if (wrong.Count > 0) throw new InvalidDataException("refused, nothing applied: the old bytes do not match: " + string.Join("; ", wrong));
        // where each patch is in the ROM file (the store's file, with its header): the offset whose bytes are the patch's old bytes, a header length the
        // console's files have (0, 16 for iNES, 512 for a copier header, 16 + 512 with a trainer, or the file's size less the ROM's)
        byte[] file = (byte[])m.RomFile.Clone();
        var applied = new List<AppliedPatch>();
        foreach (var (p, cpu, space, off, resolved) in plan)
        {
            uint romSize = cpu.Spaces.First(s => s.Id == space).Size;
            long at = FileOffset(m.RomFile, romSize, off, p.Old);
            if (at < 0) throw new InvalidDataException($"refused, nothing applied: '{p}' (rom:${off:X}) has no place in the ROM file that holds its old bytes (an interleaved or packed file): byte patches need a plain ROM file");
            applied.Add(new AppliedPatch { Text = p.ToString(), Resolved = resolved, RomOffset = off, FileOffset = at, Old = RomPatch.Bytes(p.Old), New = RomPatch.Bytes(p.New) });
        }
        for (int k = 0; k < plan.Count; k++)
        {
            var (p, cpu, space, off, _) = plan[k];
            for (int i = 0; i < p.New.Length; i++)
            {
                if (!cpu.PokeSpace(space, off + (uint)i, p.New[i])) throw new InvalidOperationException($"the {cpu.Name}'s ROM space refused a write at rom:${off + i:X}");
                file[applied[k].FileOffset + i] = p.New[i];
            }
            var back = Enumerable.Range(0, p.New.Length).Select(i => cpu.PeekSpace(space, off + (uint)i)).ToArray();
            if (!back.SequenceEqual(p.New)) throw new InvalidOperationException($"'{p}': the ROM reads back {RomPatch.Bytes(back)} after the patch");
        }
        return (applied, file);
    }

    /// <summary>The file offset of ROM offset <paramref name="off"/>: a header length the console's files have whose bytes there are <paramref name="old"/> (and, when several do, the one whose surroundings match most).</summary>
    public static long FileOffset(byte[] file, uint romSize, uint off, byte[] old)
    {
        var heads = new List<long> { 0, 16, 512, 528, file.Length - (long)romSize };
        foreach (long h in heads.Distinct())
        {
            if (h < 0 || h + off + old.Length > file.Length) continue;
            if (file.AsSpan((int)(h + off), old.Length).SequenceEqual(old)) return h + off;
        }
        return -1;
    }

    // ------------------------------------------------------------------------------------------------ the rebuild's symbol-map check

    /// <summary>
    /// The symbol-map check at the old machine's current frame start: the PC and every return address of the call stack (the debugger's, tracked by the
    /// caller) must be the same function and offset in the old build and the new one (looked up at the same place in the ROM: the banks mapped now; the
    /// function is the nearest code symbol at or before it, however far, up to the next symbol), and the code there must be the same bytes (the instruction
    /// at the PC; the call before a return address): a function that kept its place but whose code before the place changed is not the same code.
    /// A place no symbol names in either build is the same only when the ROM bytes around it are. <paramref name="safe"/> (optional) also needs the PC in one
    /// of these functions.
    /// </summary>
    public static ResumeCheck Check(HotpatchMachine oldM, HotpatchMachine newM, long frame, IReadOnlyList<string>? safe = null)
    {
        var r = new ResumeCheck { Frame = frame, Safe = true };
        var why = new List<string>();
        for (int ci = 0; ci < oldM.Machine.Cpus.Count; ci++)
        {
            var oc = oldM.Machine.Cpus[ci];
            var nc = newM.Machine.Cpu(oc.Id);
            if (nc == null) { why.Add($"the new build's machine has no CPU {oc.Id}"); r.Safe = false; continue; }
            var places = new List<(string What, uint Addr, bool Return)> { ("pc", oc.PC, false) };
            foreach (var f in oldM.Debugger.CallStack(oc.Id).Reverse()) places.Add(($"return of {f.Kind} at {oc.FormatAddress(f.CallerPc)}", f.ReturnPc, true));
            foreach (var (what, addr, isReturn) in places)
            {
                Span<SpaceKey> keys = stackalloc SpaceKey[4];
                int n = oc.AccessKeys(addr, false, keys);
                string? a = FunctionAt(oldM.Debugger, oc, keys[..n]), b = FunctionAt(newM.Debugger, nc, keys[..n]);
                var o = new JsonObject { ["cpu"] = oc.Id, ["what"] = what, ["address"] = oc.FormatAddress(addr), ["old"] = a, ["new"] = b };
                bool same;
                string? whyNot = null;
                if (a == null && b == null)
                {
                    // code no symbol names in either build (a toolchain's start-up, a library routine): the same only when the bytes around it are, said
                    same = SameBytes(oc, nc, keys[..n], -16, 16, out string how);
                    o["by"] = same ? how : null;
                    if (!same) whyNot = $"{oc.Id} {what} {oc.FormatAddress(addr)} has no symbol in either build and the ROM bytes around it differ (the code moved or changed)";
                }
                else if (a != null && a == b)
                {
                    // the same function and offset: and the same code there (the call that returns here, or the instruction at the PC)
                    same = SameBytes(oc, nc, keys[..n], isReturn ? -3 : 0, isReturn ? 0 : 3, out _);
                    if (!same) whyNot = $"{oc.Id} {what} {oc.FormatAddress(addr)} is {a} in both builds, but the code {(isReturn ? "before it (the call that returns there)" : "there")} differs: the function's code before that point changed";
                }
                else
                {
                    same = false;
                    if (a == null) whyNot = $"{oc.Id} {what} {oc.FormatAddress(addr)} has no symbol in the old build (the new one names it {b})";
                    else
                    {
                        string moved;
                        if (newM.Debugger.Symbols.TryFind(Base(a)!, out var sym, out _) && nc.BindSymbol(sym, out var k)) moved = $"; {Base(a)} is at {Describe(nc, k)} in the new build";
                        else moved = $"; {Base(a)} is not in the new build's symbols";
                        whyNot = $"{oc.Id} {what} {oc.FormatAddress(addr)} is {a} in the old build but {(b ?? "no symbol")} in the new one{moved}";
                    }
                }
                o["same"] = same;
                r.Places.Add(o);
                if (what == "pc" && ci == 0) r.Function = Base(a);
                if (same) continue;
                r.Safe = false;
                why.Add(whyNot!);
            }
        }
        if (r.Safe && safe != null && safe.Count > 0 && !safe.Contains(r.Function ?? "", StringComparer.Ordinal))
        {
            r.Safe = false;
            why.Add($"the PC is in {r.Function ?? "no function"}, not in a safe function ({string.Join(", ", safe)})");
        }
        r.Why = string.Join("; ", why);
        return r;
    }

    /// <summary>The function a place is in, by name and offset: the nearest symbol at or before it in the memory it maps to (then the CPU address), up to the next symbol (no size cap: a long function's far end is still it).</summary>
    private static string? FunctionAt(Debugger d, DebugCpu c, ReadOnlySpan<SpaceKey> keys)
    {
        var idx = FunctionIndex(d, c);
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            if (!idx.TryGetValue(keys[i].Space, out var t)) continue;
            int j = Array.BinarySearch(t.At, keys[i].Offset);
            if (j < 0) j = ~j - 1;
            else while (j > 0 && t.At[j - 1] == keys[i].Offset) j--;
            if (j < 0) continue;
            uint delta = keys[i].Offset - t.At[j];
            return delta == 0 ? t.Name[j] : $"{t.Name[j]}+${delta:X}";
        }
        return null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Debugger, Dictionary<(int, int), Dictionary<int, (uint[] At, string[] Name)>>> functionIndexes = new();

    private static Dictionary<int, (uint[] At, string[] Name)> FunctionIndex(Debugger d, DebugCpu c)
    {
        var byCpu = functionIndexes.GetValue(d, _ => new());
        lock (byCpu)
        {
            var key = (c.Machine.Cpus.ToList().IndexOf(c), d.Symbols.Version);
            if (byCpu.TryGetValue(key, out var have)) return have;
            var lists = new Dictionary<int, List<(uint, string)>>();
            foreach (var s in d.Symbols.All)
            {
                if (s.Name.StartsWith("SECTIONSTART_") || s.Name.StartsWith("SECTIONEND_")) continue;
                if (!c.BindSymbol(s, out var k)) continue;
                if (!lists.TryGetValue(k.Space, out var l)) lists[k.Space] = l = new();
                l.Add((k.Offset, s.Name));
            }
            var idx = new Dictionary<int, (uint[] At, string[] Name)>();
            foreach (var (sp, l) in lists)
            {
                var sorted = l.OrderBy(x => x.Item1).ToList();
                idx[sp] = (sorted.Select(x => x.Item1).ToArray(), sorted.Select(x => x.Item2).ToArray());
            }
            byCpu[key] = idx;
            return idx;
        }
    }
    /// <summary>The ROM bytes from <paramref name="before"/> to <paramref name="after"/> around a place are identical in both builds (at the same ROM offset).</summary>
    private static bool SameBytes(DebugCpu oc, DebugCpu nc, ReadOnlySpan<SpaceKey> keys, int before, int after, out string how)
    {
        how = "";
        var rom = oc.Spaces.FirstOrDefault(s => s.Is("rom"));
        var nrom = nc.Spaces.FirstOrDefault(s => s.Is("rom"));
        if (rom == null || nrom == null) return false;
        foreach (var k in keys)
        {
            if (k.Space != rom.Id) continue;
            long from = Math.Max(0, (long)k.Offset + before), to = Math.Min(Math.Min(rom.Size, nrom.Size), (long)k.Offset + after);
            if (to <= from) return false;
            for (long i = from; i < to; i++) if (oc.PeekSpace(rom.Id, (uint)i) != nc.PeekSpace(nrom.Id, (uint)i)) return false;
            how = $"no symbol in either build: the {to - from} ROM bytes around rom:${k.Offset:X} are identical in both";
            return true;
        }
        return false;
    }

    private static string? Base(string? s) { if (s == null) return null; int p = s.IndexOf("+$", StringComparison.Ordinal); return p > 0 ? s[..p] : s; }
    private static string Describe(DebugCpu c, SpaceKey k) => $"{c.Spaces.FirstOrDefault(s => s.Id == k.Space)?.Name ?? k.Space.ToString()}:${k.Offset:X}";

    /// <summary>The ROM image a CPU's ROM space shows (every byte, by offset).</summary>
    private static byte[] RomImage(DebugCpu c)
    {
        var rom = c.Spaces.First(s => s.Is("rom"));
        var b = new byte[rom.Size];
        for (uint i = 0; i < rom.Size; i++) b[i] = c.PeekSpace(rom.Id, i);
        return b;
    }

    // ------------------------------------------------------------------------------------------------ the whole run

    public static HotpatchReport Run(HotpatchRequest q)
    {
        var sw = Stopwatch.StartNew();
        using var pb = new TimeflowPlayback(q.TimelineDir);
        var meta = pb.Meta; var rd = pb.Reader;
        bool rebuild = q.NewRom != null;
        if (rebuild == (q.Patches.Count > 0)) throw new ArgumentException(rebuild ? "a rebuild and byte patches at once: one or the other" : "nothing to hotpatch: give byte patches or a new build");
        if (!rd.Has(q.Frame)) throw new ArgumentOutOfRangeException(nameof(q.Frame), $"frame {q.Frame} is not in timeline {meta.Id} (it holds frames {rd.StartFrame}-{rd.EndFrame - 1})");
        long to = Math.Min(q.To ?? rd.EndFrame - 1, rd.EndFrame - 1);
        if (to < q.Frame) throw new ArgumentException($"the replayed span ends ({to}) before the rewound frame {q.Frame}");
        if (q.Verdicts.Count > 0 && !q.Branch) throw new ArgumentException("verdicts are evaluated over the recorded branch: they need the branch (leave branch on)");
        var rep = new HotpatchReport { Mode = rebuild ? "rebuild" : "bytes", TimelineId = meta.Id, Console = meta.Console, Frame = q.Frame, To = to };
        void Say(string s) { q.Progress?.Invoke(s); }

        using var a = q.Open(q.TimelineDir, meta, null, q.Symbols);
        pb.CheckMachine(a.Source, a.RomSha256);
        HotpatchMachine? b = null;
        long start = q.Frame;
        try
        {
            byte[] patchedFile;
            if (!rebuild)
            {
                Say($"jump to frame {q.Frame}");
                pb.Jump(a.Source, q.Frame); a.Ran = true;
                // the patches are resolved and checked on a machine of the timeline's ROM at the frame (its banks), which gives the patched ROM file;
                // the patched game is then a machine of that file with the frame's state, as a rebuild is (so its states are its own ROM's: a NES state
                // names its ROM by hash, and a jump into the branch later opens the branch's stored, patched ROM)
                using (var b0 = q.Open(q.TimelineDir, meta, null, q.Symbols))
                {
                    pb.Jump(b0.Source, q.Frame); b0.Ran = true;
                    try { (rep.Applied, patchedFile) = ApplyBytes(b0, q.Patches); }
                    catch (Exception e) when (e is InvalidDataException or FormatException)
                    {
                        rep.Refused = e.Message; rep.Ms = sw.Elapsed.TotalMilliseconds;
                        return rep;
                    }
                }
                Say($"{rep.Applied.Count} patch(es) applied at frame {q.Frame}");
                b = q.Open(q.TimelineDir, meta, patchedFile, q.Symbols);
                LoadFrame(q, meta, rd, b, start, rep);
            }
            else
            {
                if (q.NewSymbols.Count == 0 || q.Symbols.Count == 0)
                    throw new ArgumentException("a rebuild-resume needs both builds' symbols (the check maps the PC and the stack's return addresses to functions in each)");
                // the old machine at the frame with its call stack known: tracked from the timeline's start (or 600 frames before when far)
                long k0 = q.Frame - rd.StartFrame <= 36000 ? rd.StartFrame : Math.Max(rd.StartFrame, q.Frame - 600);
                while (k0 > rd.StartFrame && !rd.Row(k0).IsKey) k0--;
                Say($"the old build from frame {k0} to {q.Frame}, call stack tracked");
                pb.Jump(a.Source, k0); a.Ran = true;
                a.Debugger.TrackCallStack = true;
                var pre = pb.Replay(a.Source, k0, q.Frame, a.Run);
                if (!pre.InSync) throw new InvalidDataException($"the old build does not replay to frame {q.Frame}: off at {pre.OffFrame} ({pre.OffWhat})");
                if (k0 > rd.StartFrame || !meta.PowerOnStart) rep.Notes.Add($"the call stack is known from frame {k0}: calls made before it are not on it, so their return addresses are not checked");
                b = q.Open(q.TimelineDir, meta, q.NewRom, q.NewSymbols);
                if (!string.Equals(b.Source.ConsoleKey, meta.Console, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"the new build runs on the {b.Source.ConsoleKey}, the timeline on the {meta.Console}");
                rep.Check = Check(a, b, q.Frame);
                if (!rep.Check.Safe)
                {
                    // scan forward on the old build for the nearest safe frame start
                    Say("the rewound frame is not safe: scanning for a safe resume point");
                    long end = Math.Min(rd.EndFrame - 1, q.Frame + Math.Max(1, q.SafeScanFrames));
                    for (long f = q.Frame; f < end; f++)
                    {
                        if (q.Cancel?.Invoke() == true) break;
                        var row = rd.Row(f);
                        a.Run((PadButtons)row.P1, (PadButtons)row.P2);
                        if (a.Source.RamHash() != row.Ram) { rep.Notes.Add($"the old build went off the recording at frame {f} while scanning: the scan stopped"); break; }
                        var c = Check(a, b, f + 1, q.SafeFunctions);
                        if (c.Safe) { rep.SafePoint = c; break; }
                    }
                    string offer = rep.SafePoint != null ? $"; the nearest safe resume point is frame {rep.SafePoint.Frame} (PC in {rep.SafePoint.Function ?? "?"})" :
                                   $"; no safe frame start in the {end - q.Frame} frames after it{(q.SafeFunctions.Count > 0 ? $" in a safe function ({string.Join(", ", q.SafeFunctions)})" : "")}";
                    if (rep.SafePoint == null || !q.ResumeAtSafePoint)
                    {
                        rep.Refused = $"refused: the new build does not have the old one's code where the machine is at frame {q.Frame}: {rep.Check.Why}{offer}";
                        rep.Ms = sw.Elapsed.TotalMilliseconds;
                        return rep;
                    }
                    start = rep.SafePoint.Frame;
                    rep.Notes.Add($"resumed at the safe point, frame {start}, instead of frame {q.Frame} (asked for: resume at the safe point): {rep.Check.Why}");
                    rep.Frame = start;
                    if (start > to) throw new ArgumentException($"the safe point {start} is after the replayed span's end {to}");
                }
                // the new build with the frame's RAM and registers
                Say($"the new build at frame {start}");
                LoadFrame(q, meta, rd, b, start, rep);
                patchedFile = q.NewRom!;
            }
            rep.ResumedAt = start; rep.Resumed = true;
            rep.PatchedRomSha256 = TimeflowStore.Sha256(patchedFile);

            // ---- the new future: the recorded pads on the patched machine, recorded as a branch, compared with the old future frame by frame
            TimeflowRecorder? rec = null;
            FrameCoverage? cov = null; FrameAccesses? acc = null;
            if (q.Branch)
            {
                var o = pb.BranchOptions(start, patchedFile);
                if (rebuild && q.NewRomName.Length > 0) o.RomName = q.NewRomName;
                o.Source = "hotpatch"; o.Hotpatch = rep.Mode; o.Patches = rep.Applied.Select(x => x.Text).ToList(); o.BaseRomSha256 = meta.RomSha256; o.Build = q.Build;
                o.Note = rebuild ? $"rebuild-resume at frame {start}{(q.Build != null ? ": " + q.Build : "")}" : $"byte patches at frame {start}: {string.Join("; ", o.Patches)}";
                o.VarNames = new List<string>();   // the parent's watched variables are expressions of its host; the branch keeps none
                if (q.Index) { cov = FrameCoverage.Attach(b.Debugger); acc = FrameAccesses.Attach(b.Debugger); o.Coverage = cov; o.Accesses = acc; cov.TakeFrame(new()); acc.TakeFrame(); }
                rec = TimeflowRecorder.Start(b.Source, o);
            }
            JsonObject? div = null;
            try
            {
                div = Future(q, pb, a, b, start, to, rec, rep, rebuild);
            }
            finally
            {
                if (rec != null)
                {
                    rep.Branch = rec.Id; rep.BranchDir = rec.Dir;
                    string? stop = rec.Running ? null : rec.StopReason;
                    rec.Stop();
                    if (stop != null) rep.Notes.Add($"the branch's recording stopped: {stop}");
                }
                cov?.Detach(); acc?.Detach();
            }
            rep.Divergence = div;

            // ---- the verdicts: over the recorded branch (the patched future) and, for comparison, the old future
            if (q.Verdicts.Count > 0 && rep.BranchDir != null)
            {
                foreach (var t in q.Verdicts)
                {
                    if (q.Cancel?.Invoke() == true) break;
                    Say($"verdict: {t.Text}");
                    var v = TimeflowVerdict.Evaluate(t, rep.BranchDir, start, to, q.Open, rebuild ? q.NewSymbols : q.Symbols);
                    if (q.VerdictOnOld) v.Old = TimeflowVerdict.Evaluate(t, q.TimelineDir, start, to, q.Open, q.Symbols);
                    rep.Verdicts.Add(v);
                }
            }
        }
        finally
        {
            b?.Dispose();
        }
        rep.Ms = sw.Elapsed.TotalMilliseconds;
        return rep;
    }

    /// <summary>
    /// Loads frame <paramref name="frame"/>'s recorded state (its RAM and registers) into <paramref name="b"/>, a machine of the patched or rebuilt ROM. A
    /// state that carries its own copy of the ROM (a self-flashing board's flash, the NES mapper 30's) would bring the old code back: there the old
    /// build's bytes are replaced by the new ROM's, and the bytes the game flashed itself (where the state differs from the old ROM) are kept, said.
    /// </summary>
    private static void LoadFrame(HotpatchRequest q, TimelineMeta meta, TimelineReader rd, HotpatchMachine b, long frame, HotpatchReport rep)
    {
        var oldImg = RomImage(OpenFresh(q, meta, null, out var fresh).Machine.Main); fresh.Dispose();
        var newImg = RomImage(b.Machine.Main);
        b.Source.ForgetHeader();
        try { b.Source.Restore(rd.State(frame)); }
        catch (Exception e) when (e is InvalidDataException or FormatException or ArgumentException or IndexOutOfRangeException or System.Text.Json.JsonException)
        { throw new InvalidDataException($"the patched machine does not take frame {frame}'s state ({e.Message}): its memory layout changed (a different RAM or mapper layout)"); }
        b.Ran = true;
        var cur = RomImage(b.Machine.Main);
        var romSpace = b.Machine.Main.Spaces.First(s => s.Is("rom"));
        int restoredBytes = 0, kept = 0;
        for (int i = 0; i < Math.Min(cur.Length, newImg.Length); i++)
        {
            if (cur[i] == newImg[i]) continue;
            if (i >= oldImg.Length || cur[i] == oldImg[i]) { b.Machine.Main.PokeSpace(romSpace.Id, (uint)i, newImg[i]); restoredBytes++; }
            else kept++;
        }
        if (restoredBytes > 0 || kept > 0) rep.Notes.Add($"the state carries its own copy of the ROM (a self-flashing board): {restoredBytes} byte(s) of the old code in it replaced by the new ROM's, {kept} byte(s) the game flashed itself kept");
    }

    private static HotpatchMachine OpenFresh(HotpatchRequest q, TimelineMeta meta, byte[]? rom, out HotpatchMachine m) { m = q.Open(q.TimelineDir, meta, rom, Array.Empty<string>()); return m; }

    /// <summary>
    /// The replayed span on the patched machine <paramref name="b"/> beside the old machine <paramref name="a"/> (the recording), in lockstep:
    /// each frame's picture and work RAM against the recording (the first that differs is the divergence frame), both CPUs' traces compared until
    /// the first instruction that differs (D5's comparison: PC, bytes, registers, the byte read), the variables that differ after the divergence frame.
    /// </summary>
    private static JsonObject? Future(HotpatchRequest q, TimeflowPlayback pb, HotpatchMachine a, HotpatchMachine b, long start, long to, TimeflowRecorder? rec, HotpatchReport rep, bool rebuild)
    {
        var rd = pb.Reader; var meta = pb.Meta;
        bool lockstep = q.Divergence;
        if (lockstep)
        {
            // the old machine at the resume frame (a rebuild's old machine is there already, after its check or its scan)
            // a byte patch's old machine was jumped to the frame; a rebuild's is there after its check, or after its scan when it resumes at the safe point
            if (rebuild && start != q.Frame) pb.Jump(a.Source, start);
            a.Debugger.TrackCallStack = false;
            foreach (var m in new[] { a, b }) { m.Debugger.TraceRingSize = RingSize; m.Debugger.TraceRing = true; }
        }
        long frameOff = -1; string what = "";
        JsonObject? firstInstr = null; long instrFrame = -1;
        var otherCpus = new JsonObject();
        bool aOff = false;
        Dictionary<int, byte[]>? varsA = null, varsB = null;
        long ran = 0;
        try
        {
            for (long f = start; f <= to; f++)
            {
                if (q.Cancel?.Invoke() == true) { rep.Notes.Add($"cancelled at frame {f}"); break; }
                var row = rd.Row(f);
                var p1 = (PadButtons)row.P1; var p2 = (PadButtons)row.P2;
                bool tracing = lockstep && firstInstr == null;
                long ia = tracing ? a.Debugger.InstructionCount() : 0, ib = tracing ? b.Debugger.InstructionCount() : 0;
                var oc = tracing ? a.Machine.Cpus.Select(c => a.Debugger.InstructionCount(c.Id)).ToArray() : null;
                var nc = tracing ? b.Machine.Cpus.Select(c => b.Debugger.InstructionCount(c.Id)).ToArray() : null;
                rec?.BeforeFrame(p1, p2);
                b.Run(p1, p2);
                rec?.AfterFrame();
                bool runA = lockstep && frameOff < 0;
                if (runA) a.Run(p1, p2);
                ran++;
                if (tracing)
                {
                    for (int ci = 0; ci < a.Machine.Cpus.Count && ci < b.Machine.Cpus.Count; ci++)
                    {
                        string id = a.Machine.Cpus[ci].Id;
                        int na = (int)Math.Min(RingSize, a.Debugger.InstructionCount(id) - oc![ci]), nb = (int)Math.Min(RingSize, b.Debugger.InstructionCount(id) - nc![ci]);
                        var ta = a.Debugger.TraceTail(na, id); var tb = b.Debugger.TraceTail(nb, id);
                        int n = Math.Min(ta.Length, tb.Length), at = -1;
                        for (int i = 0; i < n; i++) if (!SameData(ta[i], tb[i])) { at = i; break; }
                        if (at < 0 && ta.Length != tb.Length) at = n;
                        if (at < 0) continue;
                        if (ci == 0)
                        {
                            firstInstr = new JsonObject
                            {
                                ["frame"] = f, ["index"] = at, ["cpu"] = id,
                                ["old"] = Ctx(ta, a.Debugger, at, id), ["new"] = Ctx(tb, b.Debugger, at, id),
                                ["pcSymbolOld"] = at < ta.Length ? a.Debugger.SymbolAt(a.Machine.Cpus[ci], ta[at].Pc) : null,
                                ["pcSymbolNew"] = at < tb.Length ? b.Debugger.SymbolAt(b.Machine.Cpus[ci], tb[at].Pc) : null,
                            };
                            if (at == n) firstInstr["why"] = $"one side ran more instructions in the frame ({ta.Length} against {tb.Length}): the same {n}, then one went on";
                            instrFrame = f;
                        }
                        else if (!otherCpus.ContainsKey(id))
                            otherCpus[id] = $"frame {f}, instruction {at}: old {(at < ta.Length ? a.Debugger.FormatTrace(ta[at], id) : "(ended)")} | new {(at < tb.Length ? b.Debugger.FormatTrace(tb[at], id) : "(ended)")}";
                    }
                    if (firstInstr != null) foreach (var m in new[] { a, b }) m.Debugger.TraceRing = false;
                }
                if (runA && !aOff && a.Source.RamHash() != row.Ram) { aOff = true; rep.Notes.Add($"the old build itself went off its recording at frame {f}: the instruction comparison after it is not against the recorded game"); }
                if (frameOff < 0)
                {
                    ulong pic = b.Source.PictureHash(), ram = b.Source.RamHash();
                    bool held = f == start && meta.Console == "nes" && pic != row.Picture && ram == row.Ram;
                    if (!held && (pic != row.Picture || ram != row.Ram))
                    {
                        frameOff = f;
                        what = string.Join(" and ", new[] { pic != row.Picture ? "the picture" : null, ram != row.Ram ? "the work RAM" : null }.Where(x => x != null));
                        if (lockstep) { varsA = SymbolDiff.Capture(a.Machine.Main); varsB = SymbolDiff.Capture(b.Machine.Main); }
                    }
                }
            }
        }
        finally
        {
            if (lockstep) foreach (var m in new[] { a, b }) m.Debugger.TraceRing = false;
        }
        rep.FramesReplayed = ran;
        var j = new JsonObject { ["from"] = start, ["to"] = to, ["framesCompared"] = ran };
        if (frameOff < 0 && firstInstr == null)
        {
            j["diverged"] = false;
            j["summary"] = $"no divergence: the patched future's picture and work RAM equal the recording's after every one of the {ran} frames{(lockstep ? ", and the CPUs ran the same instructions" : "")}";
            return j;
        }
        j["diverged"] = true;
        j["frame"] = frameOff >= 0 ? frameOff : null;
        j["what"] = frameOff >= 0 ? what : null;
        if (frameOff < 0) j["frameNote"] = $"the picture and work RAM stayed equal to the recording over all {ran} frames: only the instructions differ";
        if (firstInstr != null) j["firstInstruction"] = firstInstr;
        else if (lockstep) j["cpuSame"] = "the main CPU ran the same instructions with the same values up to the divergence frame: the difference comes from elsewhere (the picture or sound chip, a DMA, another CPU)";
        if (otherCpus.Count > 0) j["otherCpus"] = otherCpus;
        if (varsA != null && varsB != null)
        {
            var d = SymbolDiff.Compare(a.Machine.Main, a.Debugger.Symbols, varsA, b.Machine.Main, b.Debugger.Symbols, varsB);
            j["variablesAfter"] = DiffJson(d);
        }
        string where = firstInstr != null ? $"; the CPUs part at frame {instrFrame}, instruction {firstInstr["index"]} ({firstInstr["pcSymbolOld"] ?? firstInstr["old"]?.AsArray().LastOrDefault()?.ToString()})" : "";
        j["summary"] = (frameOff >= 0 ? $"the new future diverges at frame {frameOff} ({what} after the frame)" : "the picture and work RAM never differ") + where +
                       (j["variablesAfter"] is JsonObject va ? $"; {va["variablesDiffering"]} variable(s) differ after frame {frameOff}" : "");
        return j;
    }

    private static JsonArray Ctx(TraceRecord[] t, Debugger d, int i, string cpu)
    {
        var arr = new JsonArray();
        for (int k = Math.Max(0, i - 3); k <= Math.Min(t.Length - 1, i); k++) arr.Add($"#{k} " + d.FormatTrace(t[k], cpu));
        return arr;
    }

    // D5's comparison (MovieDiverge): the record keeps 4 bytes from the PC; only the instruction's own are the instruction
    private static uint OwnBytes(in TraceRecord r) => r.Length >= 4 ? r.Bytes : r.Bytes & ((1u << (8 * Math.Max(1, (int)r.Length))) - 1);
    private static bool SameData(in TraceRecord x, in TraceRecord y) =>
        x.Pc == y.Pc && x.Length == y.Length && OwnBytes(x) == OwnBytes(y) && x.Kind == y.Kind && x.R0 == y.R0 && x.R1 == y.R1 && x.R2 == y.R2 && x.R3 == y.R3 && x.R4 == y.R4
        && x.P == y.P && x.R5 == y.R5 && x.R6 == y.R6 && x.Ea == y.Ea && x.EaValue == y.EaValue;

    public static JsonObject DiffJson(SymbolDiff.Result r) => new()
    {
        ["variablesDiffering"] = r.VariablesDiffering, ["symbols"] = r.Symbols,
        ["variables"] = new JsonArray(r.Variables.Take(40).Select(v => (JsonNode)new JsonObject
        {
            ["variable"] = v.Name, ["at"] = v.Place, ["old"] = string.Join(" ", v.A.Select(x => x.ToString("X2"))), ["new"] = string.Join(" ", v.B.Select(x => x.ToString("X2"))),
            ["valueOld"] = v.ValueA, ["valueNew"] = v.ValueB,
        }).ToArray()),
        ["spaces"] = new JsonArray(r.Spaces.Select(s => (JsonNode)new JsonObject { ["space"] = s.Space, ["bytesDiffering"] = s.Differing, ["withoutSymbol"] = s.WithoutSymbol }).ToArray()),
    };

    /// <summary>
    /// A recorded hotpatch branch against its parent, in lockstep: the parent's machine (its ROM) and the branch's (its patched ROM) put at the branch's
    /// first frame and run with the parent's recorded pads (a hotpatch branch replayed them), compared as <see cref="Run"/> does.
    /// </summary>
    public static JsonObject DivergeRecorded(string branchDir, HotpatchOpener open, IReadOnlyList<string> symbols, IReadOnlyList<string> newSymbols)
    {
        var bm = TimelineMeta.Load(branchDir);
        if (bm.ParentId == null) throw new InvalidDataException($"timeline {bm.Id} is not a branch: nothing to compare it with");
        string parentDir = TimeflowStore.Find(bm.ParentId) ?? throw new FileNotFoundException($"branch {bm.Id}'s parent {bm.ParentId} is gone");
        using var pp = new TimeflowPlayback(parentDir);
        using var bp = new TimeflowPlayback(branchDir);
        long start = bm.StartFrame, to = Math.Min(bp.Reader.EndFrame, pp.Reader.EndFrame) - 1;
        using var a = open(parentDir, pp.Meta, null, symbols);
        using var b = open(branchDir, bm, null, newSymbols);
        pp.Jump(a.Source, start); a.Ran = true;
        bp.Jump(b.Source, start); b.Ran = true;
        var rep = new HotpatchReport();
        var j = Future(new HotpatchRequest { Divergence = true, Frame = start }, pp, a, b, start, to, null, rep, false) ?? new JsonObject();
        if (rep.Notes.Count > 0) j["notes"] = new JsonArray(rep.Notes.Select(n => (JsonNode)n).ToArray());
        return j;
    }

    /// <summary>A recorded branch against its parent (the divergence after the fact): the first frame whose picture or RAM differs, from the two recordings' hashes.</summary>
    public static JsonObject CompareRecorded(string branchDir)
    {
        using var br = new TimelineReader(branchDir);
        var meta = br.Meta;
        if (meta.ParentId == null || meta.ParentFrame == null) throw new InvalidDataException($"timeline {meta.Id} is not a branch: nothing to compare it with");
        string parentDir = TimeflowStore.Find(meta.ParentId) ?? throw new FileNotFoundException($"branch {meta.Id}'s parent {meta.ParentId} is gone");
        using var pr = new TimelineReader(parentDir);
        long from = br.StartFrame, to = Math.Min(br.EndFrame, pr.EndFrame) - 1;
        long off = -1; string what = "";
        for (long f = from; f <= to; f++)
        {
            var x = br.Row(f); var y = pr.Row(f);
            if (x.Picture != y.Picture || x.Ram != y.Ram)
            {
                bool held = f == from && meta.Console == "nes" && x.Ram == y.Ram;
                if (held) continue;
                off = f; what = string.Join(" and ", new[] { x.Picture != y.Picture ? "the picture" : null, x.Ram != y.Ram ? "the work RAM" : null }.Where(s => s != null)); break;
            }
        }
        return new JsonObject
        {
            ["branch"] = meta.Id, ["parent"] = meta.ParentId, ["from"] = from, ["to"] = to, ["hotpatch"] = meta.Hotpatch, ["patches"] = meta.Patches is { } ps ? new JsonArray(ps.Select(p => (JsonNode)p).ToArray()) : null,
            ["diverged"] = off >= 0, ["frame"] = off >= 0 ? off : null, ["what"] = off >= 0 ? what : null,
            ["summary"] = off >= 0 ? $"the branch diverges from its parent at frame {off} ({what} after the frame)" : $"no divergence over frames {from}-{to} (the recordings' picture and RAM hashes)",
        };
    }
}
