// A typed / clicked input script for the x86 PC (through PcSession.HostKey / HostMouse, like the real keyboard and mouse), frame-paced like the Workshop's x86boot --type: plain text is typed (make + break, two frames a key),
// {m:dx,dy[,buttons]} moves the PS/2 mouse (buttons 1 = left, 2 = right, 4 = middle; held until the next {m:}), {wait:N} waits N frames,
// {key} / {mod+key} presses a named key (enter, esc, tab, space, bs, f1-f12, up/down/left/right, home, end, pgup, pgdn, del, ins, win, menu, a single character),
// {down:KEY} / {up:KEY} press or release a key and leave it, {{ is a literal '{'. A held click = {m:0,0,1}{wait:10}{m:0,0,0}. Written for BrokenNes.

using System;
using System.Collections.Generic;

namespace NesEmulator.X86;

public sealed class PcInputScript
{
    private readonly Queue<(Action<PcSession>? Act, int Frames)> _steps = new();
    private int _waiting;

    /// <summary>Steps not yet run (0 = done).</summary>
    public int Pending => _steps.Count + (_waiting > 0 ? 1 : 0);

    /// <summary>Compiles a script (throws ArgumentException on a bad token, before anything is queued).</summary>
    public static PcInputScript Parse(string text)
    {
        var s = new PcInputScript();
        for (int ci = 0; ci < text.Length; ci++)
        {
            char ch = text[ci];
            if (ch == '{' && ci + 1 < text.Length && text[ci + 1] == '{') { ci++; s.Char('{'); continue; }
            if (ch == '{')
            {
                int ce = text.IndexOf('}', ci);
                if (ce < 0) throw new ArgumentException("an unclosed '{' at " + ci);
                string tok = text[(ci + 1)..ce].Trim().ToLowerInvariant(); ci = ce;
                if (tok.StartsWith("m:"))
                {
                    var v = tok[2..].Split(',');
                    if (v.Length is < 2 or > 3 || !int.TryParse(v[0], out int dx) || !int.TryParse(v[1], out int dy)) throw new ArgumentException("{m:dx,dy[,buttons]}: " + tok);
                    int b = v.Length > 2 && int.TryParse(v[2], out int bb) ? bb : 0;
                    s.Add(m => m.HostMouse(dx, dy, b), 2);
                    continue;
                }
                if (tok.StartsWith("wait:")) { if (!int.TryParse(tok[5..], out int n) || n < 0) throw new ArgumentException("{wait:N}: " + tok); s.Add(null, n); continue; }
                if (tok.StartsWith("down:")) { var k = Key(tok[5..]); s.Add(m => Press(m, k, true), 1); continue; }
                if (tok.StartsWith("up:")) { var k = Key(tok[3..]); s.Add(m => Press(m, k, false), 1); continue; }
                var parts = tok.Split('+');
                var mods = new List<byte[]>();
                foreach (var p in parts[..^1]) mods.Add(p switch { "ctrl" => new byte[] { 0x1d }, "alt" => new byte[] { 0x38 }, "shift" => new byte[] { 0x2a }, "win" => new byte[] { 0xe0, 0x5b }, _ => throw new ArgumentException("key modifier " + p) });
                var key = Key(parts[^1]);
                foreach (var md in mods) s.Add(m => Press(m, md, true), 1);
                s.Add(m => Press(m, key, true), 1);
                s.Add(m => Press(m, key, false), 1);
                for (int q = mods.Count - 1; q >= 0; q--) { var mb = mods[q]; s.Add(m => Press(m, mb, false), 1); }
                s.Add(null, 1);
                continue;
            }
            s.Char(ch);
        }
        return s;
    }

    private void Add(Action<PcSession>? a, int frames) => _steps.Enqueue((a, frames));

    private void Char(char ch)
    {
        if (ch == '\r') return;
        if (!Ascii.TryGetValue(ch, out var k)) throw new ArgumentException($"no key types '{ch}' (U+{(int)ch:X4})");
        Add(m => { if (k.Shift) m.HostKey(0x2a, false, true); m.HostKey(k.Code, false, true); m.HostKey(k.Code, false, false); if (k.Shift) m.HostKey(0x2a, false, false); }, 2);
    }

    /// <summary>The emulation thread, before each frame: runs the steps that are due.</summary>
    public void BeforeFrame(PcSession m)
    {
        if (_waiting > 0) { _waiting--; return; }
        while (_steps.TryDequeue(out var st))
        {
            st.Act?.Invoke(m);
            if (st.Frames > 0) { _waiting = st.Frames - 1; return; }
        }
    }

    // through the session's host keyboard / mouse (PcSession.HostKey / HostMouse), the same path as the real ones, so the bug capture's input tap records it
    private static void Press(PcSession m, byte[] k, bool down) => m.HostKey(k[^1], k.Length > 1, down);

    private static byte[] Key(string k)
    {
        if (k.Length > 1 && k[0] == 'f' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 12) return new[] { (byte)(fn <= 10 ? 0x3a + fn : 0x57 + fn - 11) };
        return k switch
        {
            "win" => new byte[] { 0xe0, 0x5b }, "menu" => new byte[] { 0xe0, 0x5d }, "esc" => new byte[] { 0x01 }, "tab" => new byte[] { 0x0f },
            "enter" => new byte[] { 0x1c }, "space" => new byte[] { 0x39 }, "bs" => new byte[] { 0x0e }, "alt" => new byte[] { 0x38 },
            "ctrl" => new byte[] { 0x1d }, "shift" => new byte[] { 0x2a },
            "up" => new byte[] { 0xe0, 0x48 }, "down" => new byte[] { 0xe0, 0x50 }, "left" => new byte[] { 0xe0, 0x4b }, "right" => new byte[] { 0xe0, 0x4d },
            "home" => new byte[] { 0xe0, 0x47 }, "end" => new byte[] { 0xe0, 0x4f }, "pgup" => new byte[] { 0xe0, 0x49 }, "pgdn" => new byte[] { 0xe0, 0x51 },
            "del" => new byte[] { 0xe0, 0x53 }, "ins" => new byte[] { 0xe0, 0x52 },
            _ when k.Length == 1 && Ascii.TryGetValue(k[0], out var kk) => new[] { kk.Code },
            _ => throw new ArgumentException("key " + k),
        };
    }

    // set-1 make codes for typing ASCII (the same table as the Workshop's x86boot)
    private static readonly Dictionary<char, (byte Code, bool Shift)> Ascii = BuildAscii();
    private static Dictionary<char, (byte, bool)> BuildAscii()
    {
        var d = new Dictionary<char, (byte, bool)>();
        const string normal = "\0\x1b" + "1234567890-=\b\t" + "qwertyuiop[]\n\0" + "asdfghjkl;'`\0\\" + "zxcvbnm,./";
        const string shifted = "\0\x1b" + "!@#$%^&*()_+\b\0" + "QWERTYUIOP{}\n\0" + "ASDFGHJKL:\"~\0|" + "ZXCVBNM<>?";
        for (int i = 1; i < normal.Length; i++) { if (normal[i] != '\0') d.TryAdd(normal[i], ((byte)i, false)); if (shifted[i] != '\0') d.TryAdd(shifted[i], ((byte)i, true)); }
        d[' '] = (0x39, false);
        return d;
    }
}
