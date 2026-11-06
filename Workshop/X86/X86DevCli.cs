using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86dev</c>: the x86 console's board (<c>NesEmulator.X86.PcMachine</c>): PIT, PIC, keyboard controller, CMOS clock, VGA (text,
/// mode 13h, Mode X), the C# BIOS (INT 10h / 13h / 15h / 16h / 1Ah) and the disk. Each test is a small boot-sector program
/// (NASM source below, assembled with the pinned NASM in C:\BrokenNes-tools\dos) booted from a floppy image on a fresh machine, run
/// for a while (with keys typed by the host where the test says so), then checked: memory it stored its results in, the VGA memory
/// and the rendered picture.
/// <c>--x86dev [--only name] [--shots dir] [--nasm exe]</c>. Exit code 0 = all pass.
/// </summary>
internal static class X86DevCli
{
    private sealed record DevTest(string Name, string Asm, Action<PcMachine> Drive, Action<PcMachine, Checker2> Check, byte[]? Extra = null);

    public sealed class Checker2
    {
        public int Total, Failed;
        private readonly string _name;
        public Checker2(string name) { _name = name; }
        public void Eq(string what, long want, long got)
        {
            Total++;
            if (want != got) { Failed++; Console.WriteLine($"    {_name}: {what}: want {want:X} got {got:X}"); }
        }
        public void True(string what, bool ok, string detail = "")
        {
            Total++;
            if (!ok) { Failed++; Console.WriteLine($"    {_name}: {what} FAILED {detail}"); }
        }
    }

    public static string DefaultNasm = @"C:\BrokenNes-tools\dos\nasm\nasm-3.02\nasm.exe";

    public static int Run(string[] args)
    {
        string? only = Arg(args, "--only"), shots = Arg(args, "--shots");
        string nasm = Arg(args, "--nasm") ?? DefaultNasm;
        if (!File.Exists(nasm)) { Console.WriteLine($"x86dev: NASM not found at {nasm}"); return 1; }
        var tests = Tests();
        if (only != null) tests = tests.Where(t => only.Split(',').Any(o => t.Name.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
        string tmp = Path.Combine(Path.GetTempPath(), "x86dev_" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        int checks = 0, failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var t in tests)
        {
            var chk = new Checker2(t.Name);
            try
            {
                byte[] boot = Assemble(nasm, tmp, t.Name, t.Asm);
                var img = new byte[1474560];
                Array.Copy(boot, img, boot.Length);
                if (t.Extra != null) Array.Copy(t.Extra, 0, img, 512 * 22, t.Extra.Length);
                var m = new PcMachine();
                m.Floppy[0] = new DiskImage(img, true);
                m.PowerOn();
                t.Drive(m);
                if (m.Stopped != null) chk.True("machine running", false, m.Stopped);
                t.Check(m, chk);
                if (shots != null) { Directory.CreateDirectory(shots); X86Shots.SavePng(m.Vga, Path.Combine(shots, t.Name + ".png")); }
            }
            catch (Exception e) { chk.True("ran", false, e.GetType().Name + ": " + e.Message); }
            checks += chk.Total; failed += chk.Failed;
            Console.WriteLine($"  {(chk.Failed == 0 ? "PASS" : "FAIL")} {t.Name,-14} {chk.Total - chk.Failed}/{chk.Total}");
        }
        try { Directory.Delete(tmp, true); } catch { }
        Console.WriteLine($"x86dev: {tests.Count} tests, checks {checks - failed}/{checks} pass ({sw.Elapsed.TotalSeconds:F1}s)");
        return failed == 0 ? 0 : 1;
    }

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    public static byte[] Assemble(string nasm, string dir, string name, string asm)
    {
        string src = Path.Combine(dir, name + ".asm"), bin = Path.Combine(dir, name + ".bin"), lst = Path.Combine(dir, name + ".lst");
        File.WriteAllText(src, asm);
        var psi = new ProcessStartInfo(nasm, $"-f bin -o \"{bin}\" -l \"{lst}\" \"{src}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("nasm: " + err.Trim());
        return File.ReadAllBytes(bin);
    }

    private static void Frames(PcMachine m, int n) { for (int i = 0; i < n && m.RunFrame(); i++) { } }
    private static uint W(PcMachine m, uint a) => m.Phys16(a);
    private static uint D(PcMachine m, uint a) => m.Phys32(a);
    public static string Hash(int[] px) { var b = new byte[px.Length * 4]; Buffer.BlockCopy(px, 0, b, 0, b.Length); return Convert.ToHexString(SHA256.HashData(b))[..16]; }

    // The programs: org 7C00h, results stored from 0x9000 up, 0xD00E written to [0x8FFE] when done.
    private const string Head = "bits 16\norg 0x7c00\n    cli\n    xor ax, ax\n    mov ds, ax\n    mov es, ax\n    mov ss, ax\n    mov sp, 0x7c00\n    sti\n";
    private const string Done = "    mov word [0x8ffe], 0xd00e\n.hang:\n    hlt\n    jmp .hang\n";

    private static List<DevTest> Tests() => new()
    {
        // ---- PIT + PIC: reprogram channel 0 to 1 kHz, count IRQ 0 in our own handler for 100 ticks; time it on the machine clock
        new("pit_pic", Head + @"
    cli
    mov word [8*4], irq0
    mov [8*4+2], cs
    mov al, 0x34            ; ch0, lo/hi, mode 2
    out 0x43, al
    mov ax, 1193
    out 0x40, al
    mov al, ah
    out 0x40, al
    sti
.wait:
    hlt
    cmp word [count], 100
    jb .wait
    cli
    ; latch and read channel 0 twice: the count must go down
    mov al, 0x00
    out 0x43, al
    in al, 0x40
    mov ah, al
    in al, 0x40
    xchg al, ah
    mov [0x9000], ax
    mov cx, 50
.d: loop .d
    mov al, 0x00
    out 0x43, al
    in al, 0x40
    mov ah, al
    in al, 0x40
    xchg al, ah
    mov [0x9002], ax
    ; read-back status of channel 0: mode 2, lo/hi
    mov al, 0xe2
    out 0x43, al
    in al, 0x40
    mov [0x9004], al
    in al, 0x21
    mov [0x9005], al
" + Done + @"
irq0:
    inc word [count]
    push ax
    mov al, 0x20
    out 0x20, al
    pop ax
    iret
count: dw 0
times 510-($-$$) db 0
dw 0xaa55
", m =>
        {
            long c0 = m.PitNow;
            for (int i = 0; i < 20 && m.Phys16(0x8ffe) != 0xd00e; i++) m.RunFrame();
            m.Teletype.Append($"[pit {m.PitNow - c0}]");
        }, (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            uint a = W(m, 0x9000), b = W(m, 0x9002);
            c.True("count goes down", a > b && a - b < 1193, $"{a} then {b}");
            c.Eq("read-back status (mode 2, lo/hi)", 0x34, m.Phys8(0x9004) & 0x3f);
            c.Eq("IMR as the BIOS left it", 0xb8, m.Phys8(0x9005));
        }),

        // ---- 100 IRQ 0 at 1 kHz must take 100 ms of machine time
        new("pit_rate", Head + @"
    cli
    mov word [8*4], irq0
    mov [8*4+2], cs
    mov al, 0x34
    out 0x43, al
    mov ax, 1193
    out 0x40, al
    mov al, ah
    out 0x40, al
    mov word [0x8ffe], 0x5747     ; 'started'
    sti
.wait:
    hlt
    cmp word [count], 100
    jb .wait
" + Done + @"
irq0:
    inc word [count]
    push ax
    mov al, 0x20
    out 0x20, al
    pop ax
    iret
count: dw 0
times 510-($-$$) db 0
dw 0xaa55
", m =>
        {
            while (m.Phys16(0x8ffe) != 0x5747 && m.Stopped == null) m.RunClock(100);
            long t0 = m.PitNow;
            while (m.Phys16(0x8ffe) != 0xd00e && m.Stopped == null && m.PitNow - t0 < 1193182) m.RunClock(100);
            m.PhysW32(0x9100, (uint)(m.PitNow - t0));
        }, (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            long t = D(m, 0x9100);
            c.True("100 ticks of 1193 = 119300 PIT ticks (+- one tick and a batch)", Math.Abs(t - 119300) < 1300, $"{t}");
        }),

        // ---- the keyboard through the BIOS: INT 16h waits (HLT inside) for keys the host types; shift, E0 arrows, INT 16h AH=01
        new("kbd_bios", Head + @"
    mov di, 0x9000
    mov cx, 4
.k: xor ah, ah
    int 0x16
    stosw
    loop .k
    mov ah, 0x10
    int 0x16
    stosw
    mov ah, 0x01
    int 0x16
    jz .empty
    mov word [0x9020], 0xbad
.empty:
    mov ah, 0x02
    int 0x16
    mov [0x9022], al
" + Done + "times 510-($-$$) db 0\ndw 0xaa55\n", m =>
        {
            Frames(m, 3);
            m.Kbc.KeyBytes(0x1e, 0x9e);                 // a
            Frames(m, 2);
            m.Kbc.KeyBytes(0x2a, 0x1e, 0x9e, 0xaa);     // shift + a
            Frames(m, 2);
            m.Kbc.KeyBytes(0x1c, 0x9c);                 // enter
            m.Kbc.KeyBytes(0xe0, 0x48, 0xe0, 0xc8);     // up (non-enhanced: 4800)
            Frames(m, 2);
            m.Kbc.KeyBytes(0xe0, 0x4b, 0xe0, 0xcb);     // left (enhanced: 4BE0)
            Frames(m, 4);
        }, (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            c.Eq("a", 0x1e61, W(m, 0x9000));
            c.Eq("A", 0x1e41, W(m, 0x9002));
            c.Eq("enter", 0x1c0d, W(m, 0x9004));
            c.Eq("up (AH=00)", 0x4800, W(m, 0x9006));
            c.Eq("left (AH=10)", 0x4be0, W(m, 0x9008));
            c.Eq("buffer empty", 0, W(m, 0x9020));
            c.Eq("no shift held", 0, m.Phys8(0x9022) & 3);
        }),

        // ---- the keyboard controller directly, the way games do: own INT 9 reads port 60h; status port; controller self test
        new("kbd_8042", Head + @"
    cli
    mov word [9*4], irq1
    mov [9*4+2], cs
    mov al, 0xaa            ; controller self test -> 55
    out 0x64, al
.w1: in al, 0x64
    test al, 1
    jz .w1
    in al, 0x60
    mov [0x9000], al
    mov al, 0xf2            ; keyboard identify -> FA AB 41 (taken by the IRQ handler)
    out 0x60, al
    sti
    mov word [0x8ffc], 0x5747
.wait:
    hlt
    cmp word [n], 7
    jb .wait
" + Done + @"
irq1:
    push ax
    push bx
    in al, 0x60
    mov bx, [n]
    mov [0x9010+bx], al
    inc word [n]
    mov al, 0x20
    out 0x20, al
    pop bx
    pop ax
    iret
n: dw 0
times 510-($-$$) db 0
dw 0xaa55
", m =>
        {
            for (int i = 0; i < 5 && W(m, 0x8ffc) != 0x5747; i++) m.RunFrame();
            m.Kbc.KeyBytes(0x39, 0xb9, 0xe0, 0x4d);      // space make/break, E0 right
            Frames(m, 4);
        }, (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            c.Eq("self test", 0x55, m.Phys8(0x9000));
            byte[] want = { 0xfa, 0xab, 0x41, 0x39, 0xb9, 0xe0, 0x4d };
            for (int i = 0; i < want.Length; i++) c.Eq($"byte {i}", want[i], m.Phys8(0x9010u + (uint)i));
        }),

        // ---- CMOS clock (deterministic start 1994-06-01 12:00) and the BIOS's INT 1Ah
        new("cmos_rtc", Head + @"
    mov al, 4
    out 0x70, al
    in al, 0x71
    mov [0x9000], al
    mov al, 9
    out 0x70, al
    in al, 0x71
    mov [0x9001], al
    mov al, 0x0b
    out 0x70, al
    in al, 0x71
    mov [0x9002], al
    mov ah, 2
    int 0x1a
    mov [0x9004], cx
    mov ah, 4
    int 0x1a
    mov [0x9006], cx
    mov [0x9008], dx
    xor ah, ah
    int 0x1a
    mov [0x900a], dx
    mov [0x900c], cx
" + Done + "times 510-($-$$) db 0\ndw 0xaa55\n", m => Frames(m, 3), (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            c.Eq("hour (BCD)", 0x12, m.Phys8(0x9000));
            c.Eq("year", 0x94, m.Phys8(0x9001));
            c.Eq("24 h, BCD", 0x02, m.Phys8(0x9002));
            c.Eq("INT 1Ah time CH:CL", 0x1200, W(m, 0x9004));
            c.Eq("INT 1Ah date CX", 0x1994, W(m, 0x9006));
            c.Eq("INT 1Ah date DX", 0x0601, W(m, 0x9008));
            uint ticks = W(m, 0x900a) | (W(m, 0x900c) << 16);
            c.True("ticks since midnight ~ 12 h", Math.Abs((long)ticks - 786432) < 100, $"{ticks}");
        }),

        // ---- text mode through INT 10h: teletype, scrolling, attributes, cursor
        new("vga_text", Head + @"
    mov ax, 0x0003
    int 0x10
    mov si, msg
.p: lodsb
    or al, al
    jz .e
    mov ah, 0x0e
    mov bx, 0x0007
    int 0x10
    jmp .p
.e: mov ah, 0x02           ; cursor to row 5 col 10
    xor bh, bh
    mov dx, 0x050a
    int 0x10
    mov ax, 0x0941          ; 'A' x3, attribute 1Eh
    mov bx, 0x001e
    mov cx, 3
    int 0x10
    mov ah, 0x03
    xor bh, bh
    int 0x10
    mov [0x9000], dx
    mov ah, 0x0f
    int 0x10
    mov [0x9002], ax
" + Done + "msg: db 'Hello, x86!', 13, 10, 'Line 2', 0\ntimes 510-($-$$) db 0\ndw 0xaa55\n", m => Frames(m, 5), (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            string row0 = new(Enumerable.Range(0, 11).Select(i => (char)m.Vga.Ram[i * 4]).ToArray());
            c.True("row 0 text", row0 == "Hello, x86!", row0);
            c.Eq("row 1 'L'", 'L', m.Vga.Ram[80 * 4]);
            c.Eq("attr 1E at 5,10", 0x1e, m.Vga.Ram[(5 * 80 + 10) * 4 + 1]);
            c.Eq("'A' at 5,12", 'A', m.Vga.Ram[(5 * 80 + 12) * 4]);
            c.Eq("cursor stays at 5,10", 0x050a, W(m, 0x9000));
            c.Eq("mode 3, 80 cols", 0x5003, W(m, 0x9002));
            c.Eq("picture 720x400", 720 * 400, m.Vga.Width * m.Vga.Height);
            // the 'H' at 0,0: row 2..? of the glyph has pixels in the light grey of attribute 07
            int grey = unchecked((int)0xffaaaaaa);
            int on = 0; for (int y = 0; y < 16; y++) for (int x = 0; x < 9; x++) if (m.Vga.Frame[y * 720 + x] == grey) on++;
            c.True("'H' drawn in grey", on > 15, $"{on} pixels");
            int yellow = unchecked((int)0xffffff55);
            int ya = 0; for (int y = 80; y < 96; y++) for (int x = 90; x < 99; x++) if (m.Vga.Frame[y * 720 + x] == yellow) ya++;
            c.True("'A' yellow on blue", ya > 15 && m.Vga.Frame[80 * 720 + 90] == unchecked((int)0xff0000aa), $"{ya}");
        }),

        // ---- mode 13h: the BIOS mode set, DAC writes, pixels through A000h, the picture
        new("vga_13h", Head + @"
    mov ax, 0x0013
    int 0x10
    mov dx, 0x3c8
    mov al, 1
    out dx, al
    inc dx
    mov al, 63
    out dx, al
    xor al, al
    out dx, al
    out dx, al              ; colour 1 = red
    mov ax, 0xa000
    mov es, ax
    xor di, di
    mov cx, 320
    mov al, 1
    rep stosb               ; top line red
    mov di, 100*320+160
    mov al, 0x0f
    stosb                   ; white dot in the middle (default palette 15)
    mov ah, 0x0c            ; BIOS pixel 2 at 10,20
    mov al, 2
    xor bh, bh
    mov cx, 10
    mov dx, 20
    int 0x10
    mov ah, 0x0d
    int 0x10
    mov [0x9000], al
    mov dx, 0x3c7
    mov al, 1
    out dx, al
    mov dx, 0x3c9
    in al, dx
    mov [0x9001], al
" + Done + "times 510-($-$$) db 0\ndw 0xaa55\n", m => Frames(m, 5), (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            c.Eq("picture 640x400", 640 * 400, m.Vga.Width * m.Vga.Height);
            c.Eq("top-left red", unchecked((int)0xffff0000), m.Vga.Frame[0]);
            c.Eq("line 0 doubled", unchecked((int)0xffff0000), m.Vga.Frame[640 + 639]);
            c.Eq("middle white", unchecked((int)0xffffffff), m.Vga.Frame[200 * 640 + 320]);
            c.Eq("its neighbour black", unchecked((int)0xff000000), m.Vga.Frame[200 * 640 + 322]);
            c.Eq("INT 10h AH=0Dh read back", 2, m.Phys8(0x9000));
            c.Eq("DAC read back", 63, m.Phys8(0x9001));
            c.Eq("pixel 10,20 green", unchecked((int)0xff00aa00), m.Vga.Frame[40 * 640 + 20]);
        }),

        // ---- Mode X: unchained 320x240, plane writes through the map mask, a page flip by the start address, the split screen off
        new("vga_modex", Head + @"
    mov ax, 0x0013
    int 0x10
    mov dx, 0x3c4
    mov ax, 0x0604          ; chain 4 off
    out dx, ax
    mov dx, 0x3d4
    mov ax, 0x0014          ; no dword mode
    out dx, ax
    mov ax, 0xe317          ; byte mode
    out dx, ax
    ; 320x240: the standard Mode X CRTC values
    mov dx, 0x3c2
    mov al, 0xe3
    out dx, al
    mov dx, 0x3d4
    mov ax, 0x2c11          ; unlock CR0-7
    out dx, ax
    mov si, crtc
    mov cx, 10
.c: lodsw
    out dx, ax
    loop .c
    ; clear all planes
    mov dx, 0x3c4
    mov ax, 0x0f02
    out dx, ax
    mov ax, 0xa000
    mov es, ax
    xor di, di
    xor ax, ax
    mov cx, 0x8000
    rep stosw
    ; pixel x=0..3 of line 0: plane n gets colour n+1 (white 15 for plane 3)
    mov ax, 0x0102
    out dx, ax
    mov byte [es:0], 1
    mov ax, 0x0202
    out dx, ax
    mov byte [es:0], 2
    mov ax, 0x0402
    out dx, ax
    mov byte [es:0], 4
    mov ax, 0x0802
    out dx, ax
    mov byte [es:0], 15
    ; pixel 5 of line 239 (address 239*80+1, plane 1) colour 15
    mov ax, 0x0202
    out dx, ax
    mov byte [es:239*80+1], 15
    ; second page at 0x4b00 (address 19200): its first pixel colour 4
    mov ax, 0x0102
    out dx, ax
    mov byte [es:19200], 4
    mov word [0x8ffc], 0x5747
.w: hlt
    cmp byte [flip], 0
    je .w
    mov dx, 0x3d4
    mov ax, 0x4b0c          ; start address 4B00h
    out dx, ax
    mov ax, 0x000d
    out dx, ax
" + Done + @"
crtc: dw 0x0d06, 0x3e07, 0x4109, 0xea10, 0xac11, 0xdf12, 0x0014, 0xe715, 0x0616, 0xe317
flip: db 0
times 510-($-$$) db 0
dw 0xaa55
", m =>
        {
            for (int i = 0; i < 6 && W(m, 0x8ffc) != 0x5747; i++) m.RunFrame();
            m.RunFrame();
            m.Teletype.Append(Hash(m.Vga.Frame));
            var f = (int[])m.Vga.Frame.Clone();
            m.PhysW32(0x9100, (uint)m.Vga.Width); m.PhysW32(0x9104, (uint)m.Vga.Height);
            for (int x = 0; x < 4; x++) m.PhysW32(0x9110u + (uint)x * 4, (uint)f[x * 2]);
            m.PhysW32(0x9120, (uint)f[(239 * 2) * 640 + 5 * 2]);
            uint flipAddr = 0;
            for (uint a = 0x7c00; a < 0x7e00; a++) if (m.Phys16(a) == 0xe317 && m.Phys8(a + 2) == 0 && m.Phys8(a + 3) == 0) { flipAddr = a + 2; break; }
            m.PhysW8(flipAddr, 1);
            Frames(m, 3);
        }, (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            c.Eq("640 wide", 640, D(m, 0x9100));
            c.Eq("480 lines (240 doubled)", 480, D(m, 0x9104));
            uint[] want = { 0xff0000aa, 0xff00aa00, 0xffaa0000, 0xffffffff };
            for (int x = 0; x < 4; x++) c.Eq($"pixel {x} from plane {x}", want[x], D(m, 0x9110u + (uint)x * 4));
            c.Eq("pixel 5 of line 239", 0xffffffff, D(m, 0x9120));
            c.Eq("after the flip: page 2's first pixel", unchecked((int)0xffaa0000), m.Vga.Frame[0]);
        }),

        // ---- vertical retrace on 3DAh: count retrace starts during 70 IRQ 0 at 70 Hz-ish (1/70 s each) -> about 70 frames per second
        new("vga_retrace", Head + @"
    cli
    mov word [8*4], irq0
    mov [8*4+2], cs
    mov al, 0x34
    out 0x43, al
    mov ax, 11932           ; 100 Hz
    out 0x40, al
    mov al, ah
    out 0x40, al
    sti
    mov dx, 0x3da
.a: in al, dx               ; wait for a retrace to start
    test al, 8
    jnz .a
.b: in al, dx
    test al, 8
    jz .b
    mov word [count], 0
    xor bx, bx
.loop:
.c: in al, dx
    test al, 8
    jnz .c
.d: in al, dx
    test al, 8
    jz .d
    inc bx
    cmp word [count], 100
    jb .loop
    mov [0x9000], bx
" + Done + @"
irq0:
    inc word [count]
    push ax
    mov al, 0x20
    out 0x20, al
    pop ax
    iret
count: dw 0
times 510-($-$$) db 0
dw 0xaa55
", m => Frames(m, 90), (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            c.True("70 retraces in a second", Math.Abs((int)W(m, 0x9000) - 70) <= 1, $"{W(m, 0x9000)}");
        }),

        // ---- INT 13h on the floppy: geometry, a CHS read, LBA calls refused on a floppy, write then read back
        new("disk_int13", Head + @"
    mov ah, 0x08
    xor dl, dl
    int 0x13
    mov [0x9000], cx
    mov [0x9002], dx
    mov [0x9004], bl
    ; read 4 sectors from LBA 22 = C0 H1 S5, into 0x2000
    xor ax, ax
    mov es, ax
    mov bx, 0x2000
    mov ax, 0x0204
    mov cx, 0x0005
    mov dx, 0x0100
    int 0x13
    setc [0x9006]
    mov [0x9007], al
    ; write sector LBA 100 (C2 H1 S11) from 0x2000, then read it back to 0x3000
    mov bx, 0x2000
    mov ax, 0x0301
    mov cx, 0x020b
    mov dx, 0x0100
    int 0x13
    setc [0x9008]
    mov bx, 0x3000
    mov ax, 0x0201
    mov cx, 0x020b
    mov dx, 0x0100
    int 0x13
    setc [0x9009]
    mov ah, 0x41
    mov bx, 0x55aa
    xor dl, dl
    int 0x13
    setc [0x900a]
    ; a missing hard disk
    mov ah, 0x02
    mov al, 1
    mov dl, 0x80
    mov cx, 1
    xor dh, dh
    int 0x13
    setc [0x900b]
    ; equipment and memory
    int 0x11
    mov [0x900c], ax
    int 0x12
    mov [0x900e], ax
    mov ah, 0x88
    int 0x15
    mov [0x9010], ax
" + Done + "times 510-($-$$) db 0\ndw 0xaa55\n", m => Frames(m, 3), (m, c) =>
        {
            c.Eq("done", 0xd00e, W(m, 0x8ffe));
            c.Eq("CX: 80 cylinders, 18 sectors", 0x4f12, W(m, 0x9000));
            c.Eq("DX: 2 heads, 1 drive", 0x0101, W(m, 0x9002));
            c.Eq("type 1.44 MB", 4, m.Phys8(0x9004));
            c.Eq("read ok", 0, m.Phys8(0x9006));
            c.Eq("4 read", 4, m.Phys8(0x9007));
            for (int i = 0; i < 4 * 512; i += 97) c.Eq($"data at +{i}", (byte)(i * 7 + 3), m.Phys8(0x2000u + (uint)i));
            c.Eq("write ok", 0, m.Phys8(0x9008));
            c.Eq("read back ok", 0, m.Phys8(0x9009));
            c.Eq("read back data", m.Phys8(0x2000 + 77), m.Phys8(0x3000 + 77));
            c.Eq("written into the image", unchecked((byte)(77 * 7 + 3)), m.Floppy[0]!.Data[100 * 512 + 77]);
            c.True("image dirty", m.Floppy[0]!.Dirty);
            c.Eq("no LBA calls on a floppy", 1, m.Phys8(0x900a));
            c.Eq("no hard disk", 1, m.Phys8(0x900b));
            c.Eq("equipment: 1 floppy, 80x25 colour", 0x0021, W(m, 0x900c));
            c.Eq("640 KB", 640, W(m, 0x900e));
            c.Eq("15 MB extended", 15360, W(m, 0x9010));
        }, Enumerable.Range(0, 4 * 512).Select(i => (byte)(i * 7 + 3)).ToArray()),

        // ---- save state: a running PIT program saved mid-way, loaded into a new machine, both must end identical
        new("savestate", Head + @"
    mov ax, 0x0013
    int 0x10
    mov ax, 0xa000
    mov es, ax
    xor di, di
.l: mov ah, 0
    int 0x1a
    mov al, dl
    stosb
    hlt
    cmp di, 64000
    jb .l
" + Done + "times 510-($-$$) db 0\ndw 0xaa55\n", m =>
        {
            Frames(m, 20);
            var st = m.SaveState();
            Frames(m, 30);
            string a = Hash(m.Vga.Frame);
            long ca = m.Clock;
            var m2 = new PcMachine();
            m2.LoadState(st);
            Frames(m2, 30);
            m.Teletype.Append(a == Hash(m2.Vga.Frame) && ca == m2.Clock ? "[same]" : $"[differ {a} {Hash(m2.Vga.Frame)} {ca} {m2.Clock}]");
        }, (m, c) => c.True("the loaded machine runs the same", m.Teletype.ToString().Contains("[same]"), m.Teletype.ToString())),
    };
}

internal static class X86Shots
{
    public static void SavePng(Vga v, string path)
    {
        using var bmp = new System.Drawing.Bitmap(v.Width, v.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var r = bmp.LockBits(new System.Drawing.Rectangle(0, 0, v.Width, v.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        System.Runtime.InteropServices.Marshal.Copy(v.Frame, 0, r.Scan0, v.Width * v.Height);
        bmp.UnlockBits(r);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
