using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86video [--only name] [--shots dir] [--nasm exe]</c>: the SVGA cards (x86/S3Trio64.cs) and our VBE 2.0 BIOS (PcMachine.Vbe.cs).
/// Each test is a boot-sector program (NASM, like <c>--x86dev</c>) on a fresh machine with the card it names, checked through memory
/// it stored results in, the card's video memory and the rendered picture. Exit code 0 = all pass.
/// </summary>
internal static class X86VideoCli
{
    private sealed record VTest(string Name, PcVideo Video, string Asm, Action<PcMachine, X86DevCli.Checker2> Check, int Frames = 10);

    public static int Run(string[] args)
    {
        string? only = Arg(args, "--only"), shots = Arg(args, "--shots");
        string nasm = Arg(args, "--nasm") ?? X86DevCli.DefaultNasm;
        if (!File.Exists(nasm)) { Console.WriteLine($"x86video: NASM not found at {nasm}"); return 1; }
        var tests = Tests();
        if (only != null) tests = tests.Where(t => only.Split(',').Any(o => t.Name.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
        string tmp = Path.Combine(Path.GetTempPath(), "x86video_" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        int checks = 0, failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var t in tests)
        {
            var chk = new X86DevCli.Checker2(t.Name);
            try
            {
                var m = Boot(nasm, tmp, t);
                if (m.Stopped != null) chk.True("machine running", false, m.Stopped);
                chk.Eq("done", 0xd00e, m.Phys16(0x8ffe));
                t.Check(m, chk);
                if (shots != null) { Directory.CreateDirectory(shots); X86Shots.SavePng(m.Vga, Path.Combine(shots, t.Name + ".png")); }
            }
            catch (Exception e) { chk.True("ran", false, e.GetType().Name + ": " + e.Message); }
            checks += chk.Total; failed += chk.Failed;
            Console.WriteLine($"  {(chk.Failed == 0 ? "PASS" : "FAIL")} {t.Name,-16} {chk.Total - chk.Failed}/{chk.Total}");
        }
        // save states: an S3 state round-trips; loading it on a plain-VGA machine is a visible error that leaves that machine as it was
        {
            var chk = new X86DevCli.Checker2("states");
            try
            {
                var t = tests.FirstOrDefault(x => x.Name == "banked_8bpp") ?? Tests().First(x => x.Name == "banked_8bpp");
                var m = Boot(nasm, tmp, t);
                var st = m.SaveState();
                string h1 = X86DevCli.Hash(m.Vga.Frame);
                var m2 = new PcMachine(PcMachine.DefaultMemory, video: PcVideo.S3Trio64);
                m2.LoadState(st); m2.RunFrame();
                m.RunFrame();
                chk.True("S3 state round trip: same picture", X86DevCli.Hash(m.Vga.Frame) == X86DevCli.Hash(m2.Vga.Frame) && m2.S3!.VbeMode == 0x101, $"{h1}");
                var v = new PcMachine();
                v.PowerOn(); v.RunFrame();
                var vs = v.SaveState();
                bool threw = false;
                try { v.LoadState(st); } catch (InvalidDataException) { threw = true; }
                chk.True("S3 state on a VGA machine: an error", threw);
                chk.True("...and the VGA machine kept its state", v.SaveState().AsSpan().SequenceEqual(vs));
                threw = false;
                try { m2.LoadState(vs); } catch (InvalidDataException) { threw = true; }
                chk.True("VGA state on an S3 machine: an error", threw && m2.S3!.VbeMode == 0x101);
            }
            catch (Exception e) { chk.True("ran", false, e.GetType().Name + ": " + e.Message); }
            checks += chk.Total; failed += chk.Failed;
            Console.WriteLine($"  {(chk.Failed == 0 ? "PASS" : "FAIL")} {"states",-16} {chk.Total - chk.Failed}/{chk.Total}");
        }
        // the ViRGE's S3d engine, driven straight through its MMIO registers (no guest code)
        {
            var chk = new X86DevCli.Checker2("virge_s3d");
            try { VirgeChecks(chk); }
            catch (Exception e) { chk.True("ran", false, e.GetType().Name + ": " + e.Message); }
            checks += chk.Total; failed += chk.Failed;
            Console.WriteLine($"  {(chk.Failed == 0 ? "PASS" : "FAIL")} {"virge_s3d",-16} {chk.Total - chk.Failed}/{chk.Total}");
        }
        try { Directory.Delete(tmp, true); } catch { }
        Console.WriteLine($"x86video: {tests.Count + 1} tests, checks {checks - failed}/{checks} pass ({sw.Elapsed.TotalSeconds:F1}s)");
        return failed == 0 ? 0 : 1;
    }

    private static void VirgeChecks(X86DevCli.Checker2 chk)
    {
        var m = new PcMachine(PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Virge);
        var s3 = m.S3!; var g = s3.Virge!; var ram = m.Vga.Ram; Array.Clear(ram);
        chk.Eq("PCI device 5631", 0x56315333u, s3.PciRead(0));
        chk.Eq("CR2D/2E", 0x5631, m.Vga.Cr[0x2d] << 8 | m.Vga.Cr[0x2e]);
        const int str = 1024 * 2;                                   // 1024 pixels at 16 bpp
        uint Cmd(int op, int rop, bool xp = true, bool yp = true) => 1u << 2 | (uint)rop << 17 | (xp ? 1u << 25 : 0) | (yp ? 1u << 26 : 0) | (uint)op << 27;
        ushort P(int x, int y) => (ushort)(ram[y * str + x * 2] | ram[y * str + x * 2 + 1] << 8);
        // rectangle fill 10x4 at (5,3) with colour 1234h (ROP = PATCOPY)
        g.Write(0xa4d8, 0, 32); g.Write(0xa4e4, (uint)(str << 16 | str), 32); g.Write(0xa4f4, 0x1234, 32);
        g.Write(0xa504, 9u << 16 | 4, 32); g.Write(0xa50c, 5u << 16 | 3, 32);
        g.Write(0xa500, Cmd(2, 0xf0), 32);
        chk.True("rect fill corners", P(5, 3) == 0x1234 && P(14, 6) == 0x1234 && P(15, 3) == 0 && P(5, 7) == 0 && P(4, 3) == 0, $"{P(5, 3):X} {P(14, 6):X} {P(15, 3):X} {P(5, 7):X} {P(4, 3):X}");
        // BitBLT (SRCCOPY) the filled block to (100,50)
        g.Write(0xa4d4, 0, 32); g.Write(0xa508, 5u << 16 | 3, 32); g.Write(0xa50c, 100u << 16 | 50, 32);
        g.Write(0xa500, Cmd(0, 0xcc), 32);
        chk.True("bitblt copy", P(100, 50) == 0x1234 && P(109, 53) == 0x1234 && P(110, 50) == 0, $"{P(100, 50):X} {P(109, 53):X} {P(110, 50):X}");
        // mono CPU data, byte aligned: 8x2, rows AAh / 0Fh, fg 7FFF bg 0001
        g.Write(0xa4fc, 0x7fff, 32); g.Write(0xa4f8, 0x0001, 32); g.Write(0xa504, 7u << 16 | 2, 32); g.Write(0xa50c, 200u << 16 | 10, 32);
        g.Write(0xa500, Cmd(0, 0xcc) | 1u << 6 | 1u << 7, 32);
        g.Write(0, 0x0faa, 16);
        chk.True("mono expansion", P(200, 10) == 0x7fff && P(201, 10) == 0x0001 && P(200, 11) == 0x0001 && P(207, 11) == 0x7fff, $"{P(200, 10):X} {P(201, 10):X} {P(200, 11):X} {P(207, 11):X}");
        // Gouraud triangle, flat red (R=255 in 8.7), 16 bpp (555), left to right: x 300..309 on lines 40 down to 31
        uint tc = 1u << 2 | 3u << 24;                               // 16 bpp, Z off, Gouraud
        g.Write(0xb4d8, 0, 32); g.Write(0xb4e4, (uint)(str << 16), 32);
        void T(uint a, int v) => g.Write(a, (uint)v, 32);
        T(0xb550, 255 << 7); T(0xb54c, 0); T(0xb540, 0); T(0xb53c, 0); T(0xb548, 0); T(0xb544, 0);
        T(0xb574, 300 << 20); T(0xb570, 0); T(0xb56c, 310 << 20); T(0xb568, 0); T(0xb564, 310 << 20); T(0xb560, 0); T(0xb578, 40);
        g.Write(0xb500, tc | 1, 32);                                   // autoexec: starts on B57C
        g.Write(0xb57c, 0x80000000u | 10u << 16, 32);
        chk.True("gouraud triangle (555 red)", P(300, 40) == 0x7c00 && P(309, 31) == 0x7c00 && P(310, 40) == 0 && P(300, 30) == 0 && P(299, 40) == 0, $"{P(300, 40):X} {P(309, 31):X} {P(310, 40):X}");
        chk.True("idle status", (g.Read32(0x8504) & 0x3000) == 0x3000);
        // state round trip on the ViRGE; a ViRGE state on a Trio64 machine is refused
        var st = m.SaveState();
        var m2 = new PcMachine(PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Virge); m2.LoadState(st);
        // (a fresh, never-run machine's first load/save is not byte-identical on main since f69ac695-era changes, Trio64 too: compare the 2nd generation)
        var st2 = m2.SaveState(); var m3 = new PcMachine(PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Virge); m3.LoadState(st2);
        chk.True("ViRGE state round trip", m3.SaveState().AsSpan().SequenceEqual(st2) && m3.Vga.Ram.AsSpan().SequenceEqual(m.Vga.Ram));
        bool threw = false;
        try { new PcMachine(PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Trio64).LoadState(st); } catch (InvalidDataException) { threw = true; }
        chk.True("ViRGE state on a Trio64: an error", threw);
    }

    private static PcMachine Boot(string nasm, string tmp, VTest t)
    {
        byte[] boot = X86DevCli.Assemble(nasm, tmp, t.Name, t.Asm);
        var img = new byte[1474560];
        Array.Copy(boot, img, boot.Length);
        var m = new PcMachine(PcMachine.DefaultMemory, video: t.Video);
        m.Floppy[0] = new DiskImage(img, true);
        m.PowerOn();
        for (int i = 0; i < t.Frames && m.RunFrame(); i++) { }
        return m;
    }

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    private const string Head = "bits 16\norg 0x7c00\n    cli\n    xor ax, ax\n    mov ds, ax\n    mov es, ax\n    mov ss, ax\n    mov sp, 0x7c00\n    sti\n";
    private const string Done = "    mov word [0x8ffe], 0xd00e\n.hang:\n    hlt\n    jmp .hang\n";
    private const string Tail = "times 510-($-$$) db 0\ndw 0xaa55\n";
    private static int Px(PcMachine m, int x, int y) => m.Vga.Frame[y * m.Vga.Width + x] & 0xffffff;

    private static List<VTest> Tests() => new()
    {
        // ---- the chip ID behind the S3 locks, VBE 4F00 and 4F01
        new("s3_id_vbe", PcVideo.S3Trio64, Head + @"
    mov dx, 0x3d4
    mov al, 0x30
    out dx, al
    inc dx
    in al, dx
    mov [0x9000], al        ; CR30 chip ID
    dec dx
    mov ax, 0x4838          ; unlock
    out dx, ax
    mov ax, 0xa539
    out dx, ax
    mov al, 0x2e
    out dx, al
    inc dx
    in al, dx
    mov [0x9001], al        ; CR2E device low
    mov dword [0x9100], 'VBE2'
    mov ax, 0x4f00
    mov di, 0x9100
    int 0x10
    mov [0x9002], ax
    mov ax, 0x4f01
    mov cx, 0x114
    mov di, 0x9400
    int 0x10
    mov [0x9004], ax
    ; walk the mode list (far pointer at +14)
    push ds
    lds si, [0x9100+14]
    xor cx, cx
.ml: lodsw
    cmp ax, 0xffff
    je .mle
    inc cx
    jmp .ml
.mle:
    pop ds
    mov [0x9006], cx
    mov ax, 0x4f01
    mov cx, 0x1ff           ; no such mode
    mov di, 0x9500
    int 0x10
    mov [0x9008], ax
" + Done + Tail, (m, c) =>
        {
            c.Eq("CR30 = E1 (Trio64)", 0xe1, m.Phys8(0x9000));
            c.Eq("CR2E = 11", 0x11, m.Phys8(0x9001));
            c.Eq("4F00 ok", 0x4f, m.Phys16(0x9002));
            c.Eq("VESA signature", 0x41534556, m.Phys32(0x9100));
            c.Eq("VBE 2.0", 0x200, m.Phys16(0x9104));
            c.Eq("4 MB", 64, m.Phys16(0x9112));
            c.Eq("4F01 ok", 0x4f, m.Phys16(0x9004));
            c.Eq("modes listed", PcMachine.VbeModes.Length, m.Phys16(0x9006));
            c.Eq("0x114 width", 800, m.Phys16(0x9400 + 0x12));
            c.Eq("0x114 height", 600, m.Phys16(0x9400 + 0x14));
            c.Eq("0x114 bpp", 16, m.Phys8(0x9400 + 0x19));
            c.Eq("0x114 bytes per line", 1600, m.Phys16(0x9400 + 0x10));
            c.Eq("0x114 red mask 5@11", 0x0b05, m.Phys16(0x9400 + 0x1f));
            c.Eq("0x114 LFB at E0000000", 0xE0000000, m.Phys32(0x9400 + 0x28));
            c.Eq("unknown mode fails", 0x014f, m.Phys16(0x9008));
        }),

        // ---- the plain VGA has no VBE: AH=4Fh does nothing (AL stays 00), as before
        new("vga_no_vbe", PcVideo.Vga, Head + @"
    mov ax, 0x4f00
    mov di, 0x9100
    int 0x10
    mov [0x9000], ax
" + Done + Tail, (m, c) => { c.Eq("AX unchanged", 0x4f00, m.Phys16(0x9000)); c.True("no S3", m.S3 == null); }),

        // ---- 640x480x8 through the 64 KB bank window: one colour band per bank (4F05 and the WinFuncPtr far call)
        new("banked_8bpp", PcVideo.S3Trio64, Head + @"
    mov ax, 0x4f02
    mov bx, 0x101
    int 0x10
    mov [0x9000], ax
    mov di, 0x9400
    mov ax, 0x4f01
    mov cx, 0x101
    int 0x10
    mov ax, 0xa000
    mov es, ax
    xor dx, dx              ; bank
.bank:
    cmp dx, 2
    jb .int
    xor bx, bx              ; banks 2+: the far routine
    push dx
    call far [0x9400+0x0c]
    pop dx
    jmp .fill
.int:
    mov ax, 0x4f05
    xor bx, bx
    push dx
    int 0x10
    pop dx
.fill:
    xor di, di
    mov al, dl
    add al, 32
    mov cx, 0x8000
    mov ah, al
    rep stosw
    inc dx
    cmp dx, 5
    jb .bank
    mov ax, 0x4f03
    int 0x10
    mov [0x9002], bx
" + Done + Tail, (m, c) =>
        {
            c.Eq("set ok", 0x4f, m.Phys16(0x9000));
            c.Eq("4F03: mode 101", 0x101, m.Phys16(0x9002));
            c.Eq("picture 640 wide", 640, m.Vga.Width); c.Eq("picture 480 high", 480, m.Vga.Height);
            for (int b = 0; b < 5; b++) c.Eq($"bank {b} in video memory", 32 + b, m.Vga.Ram[b * 65536 + 1234]);
            // bank b covers lines b*65536/640 ..: line 10 is bank 0, line 110 bank 1, line 420 bank 4
            c.Eq("line 10 = DAC 32", m.Vga.Dac(32) & 0xffffff, Px(m, 5, 10));
            c.Eq("line 420 = DAC 36", m.Vga.Dac(36) & 0xffffff, Px(m, 600, 420));
        }),

        // ---- 800x600x16 linear: unreal mode (FS = 4 GB), a gradient written at E0000000
        new("linear_16bpp", PcVideo.S3Trio64, Head + @"
    mov ax, 0x4f02
    mov bx, 0x4114
    int 0x10
    mov [0x9000], ax
    cli
    lgdt [gdtr]
    mov eax, cr0
    or al, 1
    mov cr0, eax
    jmp $+2
    mov bx, 8
    mov fs, bx
    and al, 0xfe
    mov cr0, eax
    jmp $+2
    xor ax, ax
    mov fs, ax
    sti
    mov edi, 0xe0000000
    xor edx, edx            ; y
.y: xor ecx, ecx            ; x
.x: mov eax, ecx
    shr eax, 5              ; 0..24 -> red
    shl eax, 11
    mov ebx, edx
    shr ebx, 4              ; 0..37 -> green
    shl ebx, 5
    or eax, ebx
    mov [fs:edi], ax
    add edi, 2
    inc ecx
    cmp ecx, 800
    jb .x
    inc edx
    cmp edx, 600
    jb .y
" + Done + @"
gdt: dq 0
    dq 0x008f92000000ffff
gdtr: dw 15
    dd gdt
" + Tail, Frames: 80, Check: (m, c) =>
        {
            c.Eq("set ok", 0x4f, m.Phys16(0x9000));
            c.True("linear on", m.S3!.LinearOn);
            c.Eq("800 wide", 800, m.Vga.Width); c.Eq("600 high", 600, m.Vga.Height);
            int p = m.Vga.Ram[(300 * 800 + 400) * 2] | m.Vga.Ram[(300 * 800 + 400) * 2 + 1] << 8;
            c.Eq("pixel (400,300) in video memory", (400 >> 5) << 11 | (300 >> 4) << 5, p);
            int r = (400 >> 5) << 3 | (400 >> 5) >> 2, g = (300 >> 4) << 2 | (300 >> 4) >> 4;
            c.Eq("picture (400,300)", r << 16 | g << 8, Px(m, 400, 300));
        }),

        // ---- the drawing engine at 640x480x8: a rectangle fill, a BitBLT of it, a line, a mono CPU-data expansion
        new("engine_8bpp", PcVideo.S3Trio64, Head + @"
    mov ax, 0x4f02
    mov bx, 0x101
    int 0x10
    mov dx, 0x3d4
    mov ax, 0x4838
    out dx, ax
    mov ax, 0xa539
    out dx, ax
    ; fill (10,20) 100x50 with colour 5 : fg mix = fg colour, replace
    mov dx, 0xbae8
    mov ax, 0x0027
    out dx, ax
    mov dx, 0xa6e8
    mov ax, 5
    out dx, ax
    mov dx, 0xbee8
    mov ax, 0xa000          ; pixel control: foreground mix
    out dx, ax
    mov ax, 0x1000          ; scissors: top 0
    out dx, ax
    mov ax, 0x2000
    out dx, ax
    mov ax, 0x3000 | 479
    out dx, ax
    mov ax, 0x4000 | 639
    out dx, ax
    mov ax, 49              ; min axis = height-1
    out dx, ax
    mov dx, 0x86e8
    mov ax, 10
    out dx, ax
    mov dx, 0x82e8
    mov ax, 20
    out dx, ax
    mov dx, 0x96e8
    mov ax, 99
    out dx, ax
    mov dx, 0x9ae8
    mov ax, 0x40b1          ; rect, draw, +x +y, 16-bit bus
    out dx, ax
    ; BitBLT it to (300,200): source = display memory
    mov dx, 0xbae8
    mov ax, 0x0067
    out dx, ax
    mov dx, 0x86e8
    mov ax, 10
    out dx, ax
    mov dx, 0x82e8
    mov ax, 20
    out dx, ax
    mov dx, 0x8ee8
    mov ax, 300
    out dx, ax
    mov dx, 0x8ae8
    mov ax, 200
    out dx, ax
    mov dx, 0xbee8
    mov ax, 49
    out dx, ax
    mov dx, 0x96e8
    mov ax, 99
    out dx, ax
    mov dx, 0x9ae8
    mov ax, 0xc0b1
    out dx, ax
    ; XOR mix over part of the copy: colour 3 ^ 5 = 6 in (300..309, 200..209)
    mov dx, 0xbae8
    mov ax, 0x0025
    out dx, ax
    mov dx, 0xa6e8
    mov ax, 3
    out dx, ax
    mov dx, 0x86e8
    mov ax, 300
    out dx, ax
    mov dx, 0x82e8
    mov ax, 200
    out dx, ax
    mov dx, 0xbee8
    mov ax, 9
    out dx, ax
    mov dx, 0x96e8
    mov ax, 9
    out dx, ax
    mov dx, 0x9ae8
    mov ax, 0x40b1
    out dx, ax
    ; mono expansion from CPU data at (0,400), 16x2: fg 15, bg 1
    mov dx, 0xbae8
    mov ax, 0x0027
    out dx, ax
    mov dx, 0xb6e8
    mov ax, 0x0007
    out dx, ax
    mov dx, 0xa2e8
    mov ax, 1
    out dx, ax
    mov dx, 0xa6e8
    mov ax, 15
    out dx, ax
    mov dx, 0xbee8
    mov ax, 0xa080          ; pixel control: CPU data selects the mix
    out dx, ax
    mov ax, 1
    out dx, ax
    mov dx, 0x86e8
    xor ax, ax
    out dx, ax
    mov dx, 0x82e8
    mov ax, 400
    out dx, ax
    mov dx, 0x96e8
    mov ax, 15
    out dx, ax
    mov dx, 0x9ae8
    mov ax, 0x43b1          ; rect, CPU data, 16-bit bus
    out dx, ax
    mov dx, 0xe2e8
    mov ax, 0xf00f
    out dx, ax
    mov ax, 0x0ff0
    out dx, ax
" + Done + Tail, (m, c) =>
        {
            byte P(int x, int y) => m.Vga.Ram[y * 640 + x];
            c.Eq("fill inside", 5, P(10, 20)); c.Eq("fill corner", 5, P(109, 69)); c.Eq("outside", 0, P(110, 69)); c.Eq("below", 0, P(10, 70));
            c.Eq("blit copy", 5, P(399, 249)); c.Eq("blit edge", 0, P(400, 249));
            c.Eq("xor", 6, P(305, 205)); c.Eq("after xor", 5, P(310, 205));
            c.Eq("mono row 0 bit 15", 15, P(0, 400)); c.Eq("mono row 0 bit 11", 1, P(4, 400)); c.Eq("mono row 0 bit 0", 15, P(15, 400));
            c.Eq("mono row 1 bit 15", 1, P(0, 401)); c.Eq("mono row 1 bit 11", 15, P(4, 401));
        }),

        // ---- back to text mode 3 after a VBE mode: the S3 leaves enhanced mode and the BIOS text shows
        new("back_to_text", PcVideo.S3Trio64, Head + @"
    mov ax, 0x4f02
    mov bx, 0x4117
    int 0x10
    mov ax, 3
    int 0x10
    mov ah, 0x0e
    mov al, 'O'
    int 0x10
    mov al, 'K'
    int 0x10
    mov ax, 0x4f03
    int 0x10
    mov [0x9000], bx
" + Done + Tail, (m, c) =>
        {
            c.True("not enhanced", !m.S3!.Enhanced && !m.S3.LinearOn);
            c.Eq("4F03 = mode 3", 3, m.Phys16(0x9000));
            c.Eq("text width", 720, m.Vga.Width);
            c.Eq("O in text memory", 'O', m.Vga.Ram[0]);
        }),

        // ---- mode 13h on the S3 is the VGA's (chain 4), drawn the same as on the plain VGA
        new("s3_mode13", PcVideo.S3Trio64, Head + @"
    mov ax, 0x13
    int 0x10
    mov ax, 0xa000
    mov es, ax
    xor di, di
    xor ax, ax
.l: stosb
    inc al
    cmp di, 64000
    jb .l
" + Done + Tail, (m, c) =>
        {
            c.Eq("320x200 doubled", 640, m.Vga.Width); c.Eq("400 high", 400, m.Vga.Height);
            c.Eq("pixel 77", m.Vga.Dac(77) & 0xffffff, Px(m, 154, 0));
        }),

        // ---- 1024x768x32 linear, hardware cursor drawn on top (64x64 AND/XOR at 3 MB)
        new("cursor_32bpp", PcVideo.S3Trio64, Head + @"
    mov ax, 0x4f02
    mov bx, 0x4118
    int 0x10
    mov [0x9000], ax
    ; the cursor pattern through the bank window is off (linear on): place it with the host check instead, only registers here
    mov dx, 0x3d4
    mov ax, 0x4838
    out dx, ax
    mov ax, 0xa539
    out dx, ax
    mov ax, 0x0c4c          ; cursor at 3 MB (0xC00 KB)
    out dx, ax
    mov ax, 0x004d
    out dx, ax
    mov ax, 0x0046
    out dx, ax
    mov ax, 0x6447          ; x = 100
    out dx, ax
    mov ax, 0x0048
    out dx, ax
    mov ax, 0x3249          ; y = 50
    out dx, ax
    mov ax, 0x014e
    xor ah, ah
    out dx, ax
    mov ax, 0x004f
    out dx, ax
    mov ax, 0x0145          ; enable
    out dx, ax
" + Done + Tail, (m, c) =>
        {
            c.Eq("set ok", 0x4f, m.Phys16(0x9000));
            c.Eq("1024 wide", 1024, m.Vga.Width); c.Eq("768 high", 768, m.Vga.Height);
            // the cursor image: first 16 pixels of every line AND=0 XOR=1 (foreground), the rest transparent
            for (int y = 0; y < 64; y++)
                for (int g = 0; g < 4; g++)
                {
                    int o = 0xC00 * 1024 + y * 16 + g * 4;
                    m.Vga.Ram[o] = m.Vga.Ram[o + 1] = (byte)(g == 0 ? 0 : 0xff);
                    m.Vga.Ram[o + 2] = m.Vga.Ram[o + 3] = (byte)(g == 0 ? 0xff : 0);
                }
            m.RunFrame();
            c.Eq("cursor pixel", 0xffffff, Px(m, 105, 55));
            c.Eq("transparent part", 0, Px(m, 130, 55));
            c.Eq("outside the cursor", 0, Px(m, 99, 55));
        }),
    };
}
