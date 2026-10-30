using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NesEmulator.Corruption;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>
/// A machine state in the timeline's ROM-less form (BrokenDev Timeflow, milestone T1 of docs/projects/brokendev-timeflow-plan.md): a small
/// <see cref="Skeleton"/> (the registers, flags and timing of every chip: everything but the big memories), stored whole every frame, and the
/// big memories as <see cref="Sections"/>, which the timeline cuts into pages and stores only where a page changed. The ROM is never in it: a
/// timeline names its ROM by sha256 and keeps the file once (<see cref="TimeflowStore"/>).
/// </summary>
public sealed class TimeflowState
{
    public byte[] Skeleton = Array.Empty<byte>();
    public byte[][] Sections = Array.Empty<byte[]>();
    /// <summary>What each section is ("ram", "mapper.flashOverlay", "payload").</summary>
    public string[] Names = Array.Empty<string>();

    public long Bytes => Skeleton.Length + Sections.Sum(s => (long)s.Length);

    /// <summary>The state's hash (<see cref="TimeflowHash"/> over the skeleton and every section, with their lengths): the restore-equals-record check compares it.</summary>
    public ulong Hash()
    {
        ulong h = TimeflowHash.Basis;
        h = TimeflowHash.Add(h, Skeleton);
        foreach (var s in Sections) h = TimeflowHash.Add(h, s);
        return h;
    }

    /// <summary>The same layout (section count, names and lengths): a delta against it is possible.</summary>
    public bool SameLayout(TimeflowState o)
    {
        if (o.Sections.Length != Sections.Length) return false;
        for (int i = 0; i < Sections.Length; i++)
            if (Sections[i].Length != o.Sections[i].Length || !string.Equals(Names[i], o.Names[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public TimeflowState Clone() => new()
    {
        Skeleton = (byte[])Skeleton.Clone(),
        Sections = Sections.Select(s => (byte[])s.Clone()).ToArray(),
        Names = (string[])Names.Clone(),
    };
}

/// <summary>
/// The timeline's 64-bit hash: FNV-1a over 64-bit words (the length first, then 8 bytes at a time, the tail byte by byte). Not D5's byte-wise
/// FNV (those are the picture and RAM hashes, kept as they are): a state is up to a megabyte every frame, and this is several times faster.
/// </summary>
public static class TimeflowHash
{
    public const ulong Basis = 14695981039346656037UL, Prime = 1099511628211UL;

    public static ulong Add(ulong h, ReadOnlySpan<byte> data)
    {
        h = (h ^ (ulong)data.Length) * Prime;
        var words = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (ulong w in words) h = (h ^ w) * Prime;
        for (int i = words.Length * 8; i < data.Length; i++) h = (h ^ data[i]) * Prime;
        return h;
    }

    public static ulong Of(ReadOnlySpan<byte> data) => Add(Basis, data);
}

/// <summary>
/// What Timeflow reads of a running game, and how it puts a state back: one per console family. Every call is made between frames, on the
/// thread that runs them. Made by <see cref="For(NES, Func{string}?)"/> or <see cref="For(IConsoleSession, Func{string}?)"/>.
/// </summary>
public sealed class TimeflowSource
{
    /// <summary>The console key (nes, snes, wms, gb, gbc, sms, gg, md).</summary>
    public string ConsoleKey { get; }
    /// <summary>The machine's state in the ROM-less form; throws <see cref="NotSupportedException"/> when the machine has none (said by <see cref="NoStateWhy"/>).</summary>
    public Func<TimeflowState> Capture { get; }
    /// <summary>Puts a captured state back (throws <see cref="InvalidDataException"/> with the reason when it cannot).</summary>
    public Action<TimeflowState> Restore { get; }
    /// <summary>"" when states work; else why not (the timeline cannot record this machine, said).</summary>
    public string NoStateWhy { get; }
    /// <summary>The memory domains, in a fixed order (the per-frame index counts the changed bytes of each one that is not ROM).</summary>
    public IReadOnlyList<IMemoryDomain> Domains { get; }
    private readonly Func<ulong> picture;
    /// <summary>The cores the machine runs (CPU/PPU/APU), read at each keyframe: a change while recording ends the timeline, with the reason.</summary>
    public Func<string> Cores { get; }
    /// <summary>What a console session's state header says (game id, title, the parts it depends on): the restore wraps a payload with it. Null for the NES.</summary>
    public ConsoleStateInfo? Header { get; private set; }

    private TimeflowSource(string key, Func<TimeflowState> capture, Action<TimeflowState> restore, string noStateWhy, IReadOnlyList<IMemoryDomain> domains, Func<ulong> picture, Func<string> cores)
    {
        ConsoleKey = key; Capture = capture; Restore = restore; NoStateWhy = noStateWhy; Domains = domains; this.picture = picture; Cores = cores;
    }

    /// <summary>The D5 picture hash (the same one movies and verify use).</summary>
    public ulong PictureHash() => picture();
    /// <summary>The D5 work-RAM hash.</summary>
    public ulong RamHash() => MovieHash.Ram(Domains);

    /// <summary>The NES: its JSON state cut into the skeleton and the big arrays (<see cref="NesStateForm"/>).</summary>
    public static TimeflowSource For(NES nes, Func<string>? cores = null)
    {
        var domains = MachineDomains.For(nes);
        return new TimeflowSource("nes",
            () => NesStateForm.Split(nes.SaveState()),
            st => nes.LoadState(NesStateForm.Join(st)),
            "", domains,
            () => MovieHash.Picture(nes.GetFrameBuffer()),
            cores ?? (Func<string>)(() => NesEmulator.Debugging.Capture.BugCaptureSource.NesCores(nes) + (NesEmulator.Debugging.Capture.BugCaptureSource.NesNtsc(nes) ? " ntsc" : "")));
    }

    /// <summary>A console session: its payload is one section (its layout is fixed for a game, so its pages line up frame to frame); the header is kept to wrap it back.</summary>
    public static TimeflowSource For(IConsoleSession s, Func<string>? cores = null)
    {
        var domains = MachineDomains.For(s);
        string key = Consoles.Key(s.Console);
        TimeflowSource? me = null;
        me = new TimeflowSource(key,
            () =>
            {
                if (!s.SupportsState) throw new NotSupportedException(s.StateUnsupportedReason);
                me!.Header ??= ConsoleStateFile.Peek(s.SaveState());
                return new TimeflowState { Sections = new[] { s.SaveStatePayload() }, Names = new[] { "payload" } };
            },
            st =>
            {
                var h = me!.Header ?? ConsoleStateFile.Peek(s.SaveState());
                if (st.Sections.Length != 1) throw new InvalidDataException($"a {key} timeline state has one section (this one has {st.Sections.Length})");
                s.LoadState(ConsoleStateFile.Wrap(h.Console, h.GameId, h.Title, h.Units, st.Sections[0]));
            },
            s.SupportsState ? "" : s.StateUnsupportedReason, domains,
            () => MovieHash.Picture(s.Frame, s.FrameWidth * s.FrameHeight),
            cores ?? (Func<string>)(() => s.Description));
        return me;
    }

    /// <summary>Re-reads the session's state header (after a core swap a new one applies).</summary>
    public void ForgetHeader() => Header = null;
}

/// <summary>
/// The NES's state in the timeline's form. The NES saves itself as JSON text (<see cref="NES.SaveState"/>: the CPU, PPU, APU and mapper
/// states are JSON strings inside it, and every byte array longer than 64 bytes is base64). The text's length moves by a character whenever a
/// number gets a digit, so its pages would never line up frame to frame. <see cref="Split"/> takes every long base64 run out of the text (the
/// 2 KB of RAM, the cartridge's PRG and CHR RAM, the PPU's memories, the mapper's arrays: on a self-flashing mapper-30 board the 512 KB flash
/// shadow, the "whole ROM" a NES state carries) and keeps them as sections of fixed length; what is left (a few KB of registers and timing) is
/// the skeleton, stored whole. <see cref="Join"/> puts the text back exactly as it was: a run is only taken out when its base64 decodes and
/// encodes back to the same characters (else it stays in the text), and the result is checked against the state's hash by the timeline.
/// </summary>
public static class NesStateForm
{
    /// <summary>The shortest base64 run taken out: 64 bytes (88 characters), the serializer's own threshold for writing base64.</summary>
    public const int MinRun = 88;
    /// <summary>The marker a section leaves in the skeleton: a byte JSON text never contains, then the section's index (2 bytes).</summary>
    private const byte Mark = 0x01;

    private static readonly System.Buffers.SearchValues<char> B64Chars = System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

    public static TimeflowState Split(string json)
    {
        json = BlankRomPath(json);
        if (json.IndexOf('\u0001') >= 0) throw new InvalidDataException("the NES state holds a byte 0x01, which the timeline's NES form uses as its marker: it cannot be stored (never silently changed)");
        var text = json.AsSpan();
        var skel = new MemoryStream(8192);
        var sections = new List<byte[]>();
        var names = new List<string>();
        char[] check = System.Buffers.ArrayPool<char>.Shared.Rent(1024);
        try
        {
            int i = 0, copied = 0;
            while (i < text.Length)
            {
                int s = text[i..].IndexOfAny(B64Chars);
                if (s < 0) break;
                int start = i + s;
                int e = text[start..].IndexOfAnyExcept(B64Chars);
                i = e < 0 ? text.Length : start + e;
                int len = i - start;
                if (len < MinRun || len % 4 != 0 || sections.Count >= 0xFFFF) continue;
                var run = text.Slice(start, len);
                int pad = run[^1] == '=' ? run[^2] == '=' ? 2 : 1 : 0;
                if (run[..^pad].Contains('=')) continue;
                var bytes = new byte[len / 4 * 3 - pad];
                if (!Convert.TryFromBase64Chars(run, bytes, out int wrote) || wrote != bytes.Length) continue;
                // only a run that encodes back to the very same characters is taken out (the text must come back byte for byte)
                if (check.Length < len) { System.Buffers.ArrayPool<char>.Shared.Return(check); check = System.Buffers.ArrayPool<char>.Shared.Rent(len); }
                if (!Convert.TryToBase64Chars(bytes, check, out int cw) || cw != len || !check.AsSpan(0, len).SequenceEqual(run)) continue;
                WriteUtf8(skel, text[copied..start]);
                skel.WriteByte(Mark); skel.WriteByte((byte)sections.Count); skel.WriteByte((byte)(sections.Count >> 8));
                names.Add(NameBefore(text, start));
                sections.Add(bytes);
                copied = i;
            }
            WriteUtf8(skel, text[copied..]);
        }
        finally { System.Buffers.ArrayPool<char>.Shared.Return(check); }
        return new TimeflowState { Skeleton = skel.ToArray(), Sections = sections.ToArray(), Names = names.ToArray() };
    }

    private static void WriteUtf8(MemoryStream ms, ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;
        int n = Encoding.UTF8.GetByteCount(chars);
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
        try { int w = Encoding.UTF8.GetBytes(chars, buf); ms.Write(buf, 0, w); }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    public static string Join(TimeflowState st)
    {
        var sb = new StringBuilder(st.Skeleton.Length + st.Sections.Sum(s => s.Length * 4 / 3 + 4));
        var sk = st.Skeleton;
        int copied = 0;
        for (int i = 0; i < sk.Length; i++)
        {
            if (sk[i] != Mark) continue;
            if (i + 2 >= sk.Length) throw new InvalidDataException("a NES timeline state's skeleton is cut short");
            sb.Append(Encoding.UTF8.GetString(sk, copied, i - copied));
            int idx = sk[i + 1] | sk[i + 2] << 8;
            if (idx >= st.Sections.Length) throw new InvalidDataException($"a NES timeline state names section {idx} of {st.Sections.Length}");
            sb.Append(Convert.ToBase64String(st.Sections[idx]));
            i += 2; copied = i + 1;
        }
        sb.Append(Encoding.UTF8.GetString(sk, copied, sk.Length - copied));
        return sb.ToString();
    }

    /// <summary>
    /// The state's <c>romPath</c> and <c>romName</c> (where the ROM file was on the recording machine, and its name: labels for a menu) made empty: <see cref="NES.LoadState"/> never applies them, and
    /// a state must hash the same whichever folder the ROM was opened from (the desktop app's path, Workshop's scratch copy).
    /// </summary>
    /// <para>Also the APU's sound ring counters (<c>ringWrite</c>, <c>ringRead</c>, <c>ringCount</c>: how much of the host's output buffer is
    /// filled) made 0: they are the host's sound queue, not the machine, and <see cref="NES.LoadState"/> sets them to 0 itself (the queued sound
    /// is never restored), so a state taken after a load would otherwise never equal the one recorded. One pass, one copy of the text.</para>
    public static string BlankRomPath(string json)
    {
        var cuts = new List<(int From, int To, string With)>(6);
        foreach (string key in new[] { "\"romPath\":\"", "\"romName\":\"" })
        {
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) continue;
            int start = at + key.Length, i = start;
            while (i < json.Length) { if (json[i] == '\\') { i += 2; continue; } if (json[i] == '"') break; i++; }
            if (i > start) cuts.Add((start, Math.Min(i, json.Length), ""));
        }
        // only APU_FIX's own state, and only the fields its load resets (PostRestoreAudioResync): any other core's fields are kept as they are,
        // so a core whose load is not exact is said by the jump's check, never hidden
        int apu = json.IndexOf("\"apu\":\"", StringComparison.Ordinal);
        if (apu >= 0 && json.Contains("\"apuCoreId\":\"APU_FIX\"", StringComparison.Ordinal))
        {
            int a0 = apu + 7, a1 = a0;
            while (a1 < json.Length) { if (json[a1] == '\\') { a1 += 2; continue; } if (json[a1] == '"') break; a1++; }
            foreach (string key in RingKeys)
            {
                string k = "\\\"" + key + "\\\":";
                int at = json.IndexOf(k, a0, Math.Max(0, a1 - a0), StringComparison.Ordinal);
                if (at < 0) continue;
                int i = at + k.Length, d = i;
                while (d < a1 && (char.IsAsciiDigit(json[d]) || json[d] is '.' or '-' or '+' or 'e' or 'E')) d++;
                if (d > i && !(d == i + 1 && json[i] == '0')) cuts.Add((i, d, "0"));
            }
        }
        if (cuts.Count == 0) return json;
        cuts.Sort((a, b) => a.From.CompareTo(b.From));
        var sb = new StringBuilder(json.Length);
        int pos = 0;
        foreach (var (f, t, w) in cuts) { if (f < pos) continue; sb.Append(json, pos, f - pos).Append(w); pos = t; }
        sb.Append(json, pos, json.Length - pos);
        return sb.ToString();
    }

    /// <summary>APU_FIX's output side, which its load resets: the sound queue's counters, the resampler's phase and the output filters.</summary>
    private static readonly string[] RingKeys = { "ringWrite", "ringRead", "ringCount", "frac", "samplePhase", "lpLast", "dcLastIn", "dcLastOut" };

    /// <summary>The JSON key before a run ("ram", "flashOverlay"), for the section's name; "?" when there is none.</summary>
    private static string NameBefore(ReadOnlySpan<char> text, int start)
    {
        // ..."name":"<run>  or  ...\"name\":\"<run>
        int j = start - 1;
        while (j >= 0 && (text[j] == '"' || text[j] == '\\' || text[j] == ':')) j--;
        int end = j + 1;
        while (j >= 0 && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j--;
        return end > j + 1 ? new string(text[(j + 1)..end]) : "?";
    }
}
