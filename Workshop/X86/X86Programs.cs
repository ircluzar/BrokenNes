using System;
using System.Collections.Generic;

namespace BrokenNes.Workshop.X86;

/// <summary>The hand-written --x86test programs. Code at 0000:7C00 (real mode, SS:SP 0000:7000), results stored at 0x9000 up, expected values worked out by hand.</summary>
internal static class Programs
{
    private const uint Org = 0x7c00;

    public static IEnumerable<X86Case> All()
    {
        yield return Alu();
        yield return MulDiv();
        yield return Shifts();
        yield return Strings();
        yield return Stack();
        yield return Jumps();
        yield return SoftInt();
        yield return DivFault();
        yield return HwIrq();
        yield return Misc486();
        yield return ProtMode();
        yield return Paging();
        yield return Ring3();
    }

    // ---- scaffolding ----

    private static byte[] Ivt(uint handler = 0x500)
    {
        var b = new byte[1024];
        for (int i = 0; i < 256; i++) { b[i * 4] = (byte)handler; b[i * 4 + 1] = (byte)(handler >> 8); }
        return b;
    }

    private static X86Case Make(string name, Asm a, int steps, Action<X86TestCli.Checker> check)
    {
        var c = new X86Case { Name = name, Steps = steps, Ip = Org, Check = check };
        c.Regs[4] = 0x7000; c.Regs[8] = 0x2;
        c.Clear(0, 0x400000).Bytes(0, Ivt()).Bytes(0x500, new byte[] { 0xf4 }).Bytes(Org, a.Build());
        c.HashAddr = 0; c.HashLen = 0x400000;
        c.Code = a.Build();
        return c;
    }

    private static void SetVec(X86Case c, int vec, uint off) => c.Bytes((uint)vec * 4, new[] { (byte)off, (byte)(off >> 8), (byte)0, (byte)0 });

    private static Asm PushfTo(Asm a, uint addr) => a.B(0x9c, 0x8f, 0x06).W(addr);          // pushf; pop word [addr]
    private static Asm MovMemAx(Asm a, uint addr) => a.B(0xa3).W(addr);                     // mov [addr], ax
    private static Asm MovMemAl(Asm a, uint addr) => a.B(0xa2).W(addr);                     // mov [addr], al
    private static Asm MovMemReg(Asm a, int reg, uint addr) => a.B(0x89, 0x06 | (reg << 3)).W(addr);   // mov [addr], r16
    private static Asm MovMemEax(Asm a, uint addr) => a.B(0x66, 0xa3).W(addr);              // mov [addr], eax (16-bit code)

    // ---- programs ----

    private static X86Case Alu()
    {
        var a = new Asm(Org);
        a.B(0xb8).W(0x1234).B(0xbb).W(0x0f0f).B(0x01, 0xd8);      // mov ax,1234 ; mov bx,0f0f ; add ax,bx
        MovMemAx(a, 0x9000); PushfTo(a, 0x9002);
        a.B(0xb0, 0x7f, 0x04, 0x01); PushfTo(a, 0x9004);          // mov al,7f ; add al,1
        a.B(0xb0, 0x00, 0x2c, 0x01); PushfTo(a, 0x9006);          // mov al,0 ; sub al,1
        a.B(0xf9, 0xb0, 0xff, 0xfe, 0xc0); PushfTo(a, 0x9008);    // stc ; mov al,ff ; inc al
        a.B(0xf4);
        return Make("alu", a, 64, k =>
        {
            k.Eq("add result", k.M16(0x9000), 0x2143);
            k.Eq("add flags (AF)", k.M16(0x9002), 0x0012);
            k.Eq("7f+1 flags (OF SF AF)", k.M16(0x9004), 0x0892);
            k.Eq("0-1 flags (CF PF AF SF)", k.M16(0x9006), 0x0097);
            k.Eq("inc keeps CF (ZF PF AF CF)", k.M16(0x9008), 0x0057);
        });
    }

    private static X86Case MulDiv()
    {
        var a = new Asm(Org);
        a.B(0xb8).W(300).B(0xbb).W(200).B(0xf7, 0xe3);            // mul bx
        MovMemAx(a, 0x9010); MovMemReg(a, 2, 0x9012);
        a.B(0xb8).W(0xfed4).B(0xbb).W(200).B(0xf7, 0xeb);         // mov ax,-300 ; imul bx
        MovMemAx(a, 0x9014); MovMemReg(a, 2, 0x9016);
        a.B(0x31, 0xd2).B(0xb8).W(1000).B(0xb9).W(7).B(0xf7, 0xf1); // xor dx,dx ; div cx
        MovMemAx(a, 0x9018); MovMemReg(a, 2, 0x901a);
        a.B(0xb8).W(0xfc18).B(0x99, 0xf7, 0xf9);                  // mov ax,-1000 ; cwd ; idiv cx
        MovMemAx(a, 0x901c); MovMemReg(a, 2, 0x901e);
        a.B(0x66, 0xb8).D(0x12345678).B(0x66, 0xb9).D(0x10000).B(0x66, 0xf7, 0xe1);   // mul ecx
        MovMemEax(a, 0x9020); a.B(0x66, 0x89, 0x16).W(0x9024);
        a.B(0x6b, 0xc3, 0xfd); MovMemAx(a, 0x9028);               // imul ax,bx,-3
        a.B(0xf4);
        return Make("muldiv", a, 64, k =>
        {
            k.Eq("mul ax", k.M16(0x9010), 0xea60); k.Eq("mul dx", k.M16(0x9012), 0);
            k.Eq("imul ax", k.M16(0x9014), 0x15a0); k.Eq("imul dx", k.M16(0x9016), 0xffff);
            k.Eq("div quotient", k.M16(0x9018), 142); k.Eq("div remainder", k.M16(0x901a), 6);
            k.Eq("idiv quotient", k.M16(0x901c), 0xff72); k.Eq("idiv remainder", k.M16(0x901e), 0xfffa);
            k.Eq("mul32 eax", k.M32(0x9020), 0x56780000); k.Eq("mul32 edx", k.M32(0x9024), 0x1234);
            k.Eq("imul3", k.M16(0x9028), 0xfda8);
        });
    }

    private static X86Case Shifts()
    {
        var a = new Asm(Org);
        a.B(0xb0, 0x81, 0xd0, 0xe0); PushfTo(a, 0x9030); MovMemAl(a, 0x9032);   // shl al,1
        a.B(0xb0, 0x81, 0xd0, 0xf8); MovMemAl(a, 0x9033);                       // sar al,1
        a.B(0xb0, 0x81, 0xd0, 0xc0); MovMemAl(a, 0x9034);                       // rol al,1
        a.B(0xb0, 0x81, 0xd0, 0xc8); MovMemAl(a, 0x9035);                       // ror al,1
        a.B(0xf8, 0xb0, 0x81, 0xd0, 0xd0); MovMemAl(a, 0x9036);                 // clc ; rcl al,1
        a.B(0xf9, 0xb0, 0x80, 0xd0, 0xd8); PushfTo(a, 0x9044); MovMemAl(a, 0x9037); // stc ; rcr al,1
        a.B(0xb8).W(0x1234).B(0xb1, 0x04, 0xd3, 0xe0); MovMemAx(a, 0x9038);      // shl ax,cl
        a.B(0x66, 0xb8).D(0x12345678).B(0x66, 0xba).D(0x9abcdef0).B(0x66, 0x0f, 0xa4, 0xd0, 0x08); MovMemEax(a, 0x903c); // shld eax,edx,8
        a.B(0x66, 0xb8).D(0x12345678).B(0x66, 0x0f, 0xac, 0xd0, 0x08); MovMemEax(a, 0x9040);                             // shrd eax,edx,8
        a.B(0xf4);
        return Make("shifts", a, 64, k =>
        {
            k.Eq("shl flags (CF OF)", k.M16(0x9030), 0x0803); k.Eq("shl", k.M8(0x9032), 0x02);
            k.Eq("sar", k.M8(0x9033), 0xc0); k.Eq("rol", k.M8(0x9034), 0x03); k.Eq("ror", k.M8(0x9035), 0xc0);
            k.Eq("rcl", k.M8(0x9036), 0x02); k.Eq("rcr", k.M8(0x9037), 0xc0); k.Eq("rcr CF", k.M16(0x9044) & 1, 0);
            k.Eq("shl cl", k.M16(0x9038), 0x2340);
            k.Eq("shld", k.M32(0x903c), 0x3456789a); k.Eq("shrd", k.M32(0x9040), 0xf0123456);
        });
    }

    private static X86Case Strings()
    {
        var a = new Asm(Org);
        a.B(0xfc).B(0xbe).W(0x9100).B(0xbf).W(0x9200).B(0xb9).W(5).B(0xf3, 0xa4);          // rep movsb
        a.B(0xbf).W(0x9210).B(0xb8).W(0xabcd).B(0xb9).W(3).B(0xf3, 0xab);                  // rep stosw
        a.B(0xbf).W(0x9100).B(0xb0, 0x4c).B(0xb9).W(5).B(0xf2, 0xae);                      // repne scasb 'L'
        MovMemReg(a, 7, 0x9220); MovMemReg(a, 1, 0x9222);
        a.B(0xbe).W(0x9100).B(0xbf).W(0x9108).B(0xb9).W(5).B(0xf3, 0xa6);                  // repe cmpsb
        MovMemReg(a, 6, 0x9224); MovMemReg(a, 7, 0x9226); MovMemReg(a, 1, 0x9228); PushfTo(a, 0x922a);
        a.B(0xfd).B(0xbe).W(0x9104).B(0xac); MovMemAl(a, 0x922c); MovMemReg(a, 6, 0x922e); a.B(0xfc); // std ; lodsb
        a.B(0xf4);
        var c = Make("strings", a, 64, k =>
        {
            k.Eq("movsb", k.M32(0x9200) | 0, 0x4c4c4548); k.Eq("movsb 5th", k.M8(0x9204), 0x4f);
            k.Eq("stosw", k.M16(0x9210) & k.M16(0x9212) & k.M16(0x9214), 0xabcd); k.Eq("stosw stops", k.M16(0x9216), 0);
            k.Eq("scasb di", k.M16(0x9220), 0x9103); k.Eq("scasb cx", k.M16(0x9222), 2);
            k.Eq("cmpsb si", k.M16(0x9224), 0x9104); k.Eq("cmpsb di", k.M16(0x9226), 0x910c); k.Eq("cmpsb cx", k.M16(0x9228), 1);
            k.Eq("cmpsb flags", k.M16(0x922a), 0x0087);
            k.Eq("lodsb std al", k.M8(0x922c), 0x4f); k.Eq("lodsb std si", k.M16(0x922e), 0x9103);
        });
        c.Bytes(0x9100, "HELLO"u8.ToArray()).Bytes(0x9108, "HELP!"u8.ToArray());
        return c;
    }

    private static X86Case Stack()
    {
        var a = new Asm(Org);
        a.B(0xb8).W(0x1111).B(0xbb).W(0x2222).B(0x50, 0x53, 0x58, 0x5b);   // push ax ; push bx ; pop ax ; pop bx
        MovMemAx(a, 0x9300); MovMemReg(a, 3, 0x9302);
        a.B(0xe8).Rel16("sub"); MovMemReg(a, 2, 0x9304);
        a.B(0xb8).W(0xaaaa).B(0xb9).W(0xcccc).B(0x60, 0x31, 0xc0, 0x31, 0xc9, 0x61);   // pusha ; xor ax,ax ; xor cx,cx ; popa
        MovMemAx(a, 0x930e); MovMemReg(a, 1, 0x9316);
        a.B(0xbd).W(0x1234).B(0xc8, 0x04, 0x00, 0x00);                      // mov bp,1234 ; enter 4,0
        MovMemReg(a, 4, 0x9306); MovMemReg(a, 5, 0x9308);
        a.B(0xc9); MovMemReg(a, 4, 0x930a); MovMemReg(a, 5, 0x930c);        // leave
        a.B(0xf4);
        a.L("sub").B(0xba).W(0x5555).B(0xc3);
        return Make("stack", a, 64, k =>
        {
            k.Eq("push/pop swap ax", k.M16(0x9300), 0x2222); k.Eq("push/pop swap bx", k.M16(0x9302), 0x1111);
            k.Eq("call/ret", k.M16(0x9304), 0x5555);
            k.Eq("popa ax", k.M16(0x930e), 0xaaaa); k.Eq("popa cx", k.M16(0x9316), 0xcccc);
            k.Eq("enter sp", k.M16(0x9306), 0x6ffa); k.Eq("enter bp", k.M16(0x9308), 0x6ffe);
            k.Eq("leave sp", k.M16(0x930a), 0x7000); k.Eq("leave bp", k.M16(0x930c), 0x1234);
        });
    }

    private static X86Case Jumps()
    {
        var a = new Asm(Org);
        a.B(0x31, 0xc0).B(0xb9).W(10).L("l1").B(0x01, 0xc8).B(0xe2).Rel8("l1"); MovMemAx(a, 0x9310);   // sum 10..1
        a.B(0xbb).W(5).B(0xbe).W(0).B(0x83, 0xfb, 0x05).B(0x74).Rel8("s1").B(0xbe).W(1).L("s1"); MovMemReg(a, 6, 0x9312); // je taken (try_jcc8 path)
        a.B(0xbb).W(3).B(0xbf).W(0).B(0x83, 0xfb, 0x05).B(0x7c).Rel8("s2").B(0xbf).W(9).L("s2"); MovMemReg(a, 7, 0x9314); // jl taken
        a.B(0x83, 0xfb, 0x05).B(0x7f).Rel8("s3").B(0xba).W(0x77).L("s3"); MovMemReg(a, 2, 0x9316);                    // jg not taken
        a.B(0x31, 0xc9).B(0xe3).Rel8("s4").B(0xbd).W(1).L("s4"); MovMemReg(a, 5, 0x9318);                             // jcxz taken
        a.B(0xb8).W(1).B(0x85, 0xc0).B(0x0f, 0x85).Rel16("s5").B(0xc7, 0x06).W(0x931a).W(0xdead).L("s5");             // test ; jnz near
        a.B(0xc7, 0x06).W(0x931c).W(0x600d);
        a.B(0x83, 0xfb, 0x03).B(0x75).Rel8("s6").B(0xc7, 0x06).W(0x931e).W(0x1111).L("s6");                          // cmp ; jnz not taken
        a.B(0xf4);
        return Make("jumps", a, 128, k =>
        {
            k.Eq("loop sum", k.M16(0x9310), 55);
            k.Eq("je taken", k.M16(0x9312), 0); k.Eq("jl taken", k.M16(0x9314), 0); k.Eq("jg not taken", k.M16(0x9316), 0x77);
            k.Eq("jcxz taken", k.M16(0x9318), 0); k.Eq("jnz near skipped", k.M16(0x931a), 0); k.Eq("after jnz near", k.M16(0x931c), 0x600d);
            k.Eq("jnz not taken", k.M16(0x931e), 0x1111);
        });
    }

    private static X86Case SoftInt()
    {
        var a = new Asm(Org);
        a.B(0xfb, 0xcd, 0x80); PushfTo(a, 0x9322); MovMemReg(a, 3, 0x9324); a.B(0xf4);
        var h = new Asm(0x7e00);
        h.B(0xbb).W(0xbeef); PushfTo(h, 0x9320); h.B(0xcf);
        var c = Make("softint", a, 32, k =>
        {
            k.Eq("flags inside the handler (IF off)", k.M16(0x9320), 0x0002);
            k.Eq("flags after iret", k.M16(0x9322), 0x0202);
            k.Eq("handler ran", k.M16(0x9324), 0xbeef);
        });
        c.Bytes(0x7e00, h.Build()); SetVec(c, 0x80, 0x7e00);
        return c;
    }

    private static X86Case DivFault()
    {
        var a = new Asm(Org);
        a.B(0xb1, 0x00).B(0xb8).W(5).L("div").B(0xf6, 0xf1).B(0xc7, 0x06).W(0x9332).W(0x1234).B(0xf4);
        uint divAt = a["div"];
        var h = new Asm(0x7e40);
        h.B(0x58, 0x5b); MovMemAx(h, 0x9334); h.B(0x05).W(2).B(0x53, 0x50).B(0xff, 0x06).W(0x9330).B(0xcf);
        var c = Make("divfault", a, 32, k =>
        {
            k.Eq("#DE taken once", k.M16(0x9330), 1);
            k.Eq("#DE return IP = the DIV", k.M16(0x9334), divAt);
            k.Eq("resumed after", k.M16(0x9332), 0x1234);
        });
        c.Bytes(0x7e40, h.Build()); SetVec(c, 0, 0x7e40);
        return c;
    }

    private static X86Case HwIrq()
    {
        var a = new Asm(Org);
        a.B(0xfb, 0xf4).B(0xa1).W(0x9340); MovMemAx(a, 0x9342); a.B(0xf4);
        var h = new Asm(0x7e80);
        h.B(0xff, 0x06).W(0x9340).B(0xcf);
        var c = Make("hwirq", a, 16, k =>
        {
            k.Eq("IRQ handler ran once", k.M16(0x9340), 1);
            k.Eq("woke from HLT, resumed after it", k.M16(0x9342), 1);
        });
        c.Bytes(0x7e80, h.Build()); SetVec(c, 8, 0x7e80);
        c.IrqAt = 1; c.IrqVec = 8;
        return c;
    }

    private static X86Case Misc486()
    {
        var a = new Asm(Org);
        a.B(0xb0, 0x15, 0x04, 0x27, 0x27); MovMemAl(a, 0x9350);                         // daa
        a.B(0xb0, 0x80, 0x98); MovMemAx(a, 0x9352);                                     // cbw
        a.B(0xb8).W(1).B(0xbb).W(2).B(0x93); MovMemAx(a, 0x9354); MovMemReg(a, 3, 0x9356); // xchg ax,bx
        a.B(0xbb).W(0x0080).B(0x0f, 0xb6, 0xc3); MovMemAx(a, 0x9358);                   // movzx ax,bl
        a.B(0x0f, 0xbe, 0xc3); MovMemAx(a, 0x935a);                                     // movsx ax,bl
        a.B(0xbb).W(0x0110).B(0x0f, 0xbc, 0xc3); MovMemAx(a, 0x935c);                   // bsf ax,bx
        a.B(0x0f, 0xbd, 0xc3); MovMemAx(a, 0x935e);                                     // bsr ax,bx
        a.B(0xb8).W(4).B(0xb9).W(2).B(0x0f, 0xa3, 0xc8, 0x0f, 0x92, 0xc2, 0x88, 0x16).W(0x9360); // bt ax,cx ; setc dl
        a.B(0xb8).W(5).B(0xbb).W(7).B(0x0f, 0xc1, 0xd8); MovMemAx(a, 0x9362); MovMemReg(a, 3, 0x9364); // xadd ax,bx
        a.B(0xb8).W(5).B(0xbb).W(5).B(0xb9).W(9).B(0x0f, 0xb1, 0xcb); MovMemReg(a, 3, 0x9366); // cmpxchg bx,cx (equal)
        a.B(0xb8).W(4).B(0x0f, 0xb1, 0xcb); MovMemAx(a, 0x9368);                         // cmpxchg bx,cx (not equal)
        a.B(0x66, 0xb8).D(0x11223344).B(0x66, 0x0f, 0xc8); MovMemEax(a, 0x936c);         // bswap eax
        a.B(0xb0, 0x01, 0x3c, 0x02, 0x9f, 0x88, 0x26).W(0x9370);                         // cmp al,2 ; lahf
        a.B(0xbb).W(0x9380).B(0xb0, 0x02, 0xd7); MovMemAl(a, 0x9371);                   // xlat
        a.B(0x66, 0xbb).D(0x9300).B(0x66, 0xbe).D(0x22).B(0x66, 0x67, 0x8b, 0x44, 0xb3, 0x08); MovMemEax(a, 0x9374); // mov eax,[ebx+esi*4+8]
        a.B(0xf4);
        var c = Make("misc486", a, 96, k =>
        {
            k.Eq("daa", k.M8(0x9350), 0x42); k.Eq("cbw", k.M16(0x9352), 0xff80);
            k.Eq("xchg ax", k.M16(0x9354), 2); k.Eq("xchg bx", k.M16(0x9356), 1);
            k.Eq("movzx", k.M16(0x9358), 0x0080); k.Eq("movsx", k.M16(0x935a), 0xff80);
            k.Eq("bsf", k.M16(0x935c), 4); k.Eq("bsr", k.M16(0x935e), 8); k.Eq("bt + setc", k.M8(0x9360), 1);
            k.Eq("xadd sum", k.M16(0x9362), 12); k.Eq("xadd old", k.M16(0x9364), 5);
            k.Eq("cmpxchg equal stores", k.M16(0x9366), 9); k.Eq("cmpxchg unequal loads", k.M16(0x9368), 9);
            k.Eq("bswap", k.M32(0x936c), 0x44332211); k.Eq("lahf", k.M8(0x9370), 0x97); k.Eq("xlat", k.M8(0x9371), 0x30);
            k.Eq("32-bit sib addressing", k.M32(0x9374), 0x12345678);
        });
        c.Bytes(0x9380, new byte[] { 0x10, 0x20, 0x30, 0x40 }).Bytes(0x9390, new byte[] { 0x78, 0x56, 0x34, 0x12 });
        return c;
    }

    // ---- protected mode ----

    private const uint Gdt = 0x8000, Gdtr = 0x8100, Idtr = 0x8110, Idt = 0x8200, Tss = 0x8c00;

    private static byte[] Desc(ulong d) => BitConverter.GetBytes(d);
    private static byte[] Gate(uint off, int type) => Desc(((ulong)((off & 0xffff0000) | ((uint)type << 8)) << 32) | ((0x08u << 16) | (off & 0xffff)));

    /// <summary>cli, lgdt, lidt, CR0.PE, far jump to 32-bit code; flat DS/ES/SS = 0x10, ESP 0x90000.</summary>
    private static void PmEntry(Asm a)
    {
        a.B(0xfa, 0x0f, 0x01, 0x16).W(Gdtr).B(0x0f, 0x01, 0x1e).W(Idtr);
        a.B(0x0f, 0x20, 0xc0, 0x0c, 0x01, 0x0f, 0x22, 0xc0);
        a.B(0x66, 0xea).Abs32("pm").W(0x08);
        a.L("pm").B(0x66, 0xb8).W(0x10).B(0x8e, 0xd8, 0x8e, 0xc0, 0x8e, 0xd0).B(0xbc).D(0x90000);
    }

    private static void PmTables(X86Case c, uint defaultHandler, params (int vec, uint off, int type)[] gates)
    {
        var gdt = new List<byte>();
        foreach (var d in new ulong[] { 0, 0x00CF9A000000FFFF, 0x00CF92000000FFFF, 0x00CFFA000000FFFF, 0x00CFF2000000FFFF,
                                        ((ulong)(0x00008900u | ((Tss >> 16) & 0xff) | (Tss & 0xff000000)) << 32) | ((Tss & 0xffff) << 16) | 0x67 })
            gdt.AddRange(Desc(d));
        c.Bytes(Gdt, gdt.ToArray());
        c.Bytes(Gdtr, new byte[] { 0x2f, 0, unchecked((byte)Gdt), unchecked((byte)(Gdt >> 8)), 0, 0 });
        c.Bytes(Idtr, new byte[] { 0xff, 0x07, unchecked((byte)Idt), unchecked((byte)(Idt >> 8)), 0, 0 });
        var idt = new byte[256 * 8];
        for (int v = 0; v < 256; v++) Gate(defaultHandler, 0x8e).CopyTo(idt, v * 8);
        foreach (var (vec, off, type) in gates) Gate(off, type).CopyTo(idt, vec * 8);
        c.Bytes(Idt, idt);
    }

    private static X86Case ProtMode()
    {
        var a = new Asm(Org);
        PmEntry(a);
        a.B(0xc7, 0x05).D(0x100000).D(0xcafebabe).B(0xa1).D(0x100000).B(0xa3).D(0x9400);   // above 1 MB
        a.B(0xcd, 0x30).B(0x89, 0x1d).D(0x9404);                                         // int 30h
        a.B(0x66, 0xb8).W(0x40).B(0x8e, 0xd8);                                           // mov ds,40h: #GP(40h)
        a.B(0xc7, 0x05).D(0x9410).D(0x600d).B(0xf4);
        a.L("hint").B(0xbb).D(0x12345678).B(0xcf);
        a.L("hgp").B(0x59, 0x89, 0x0d).D(0x940c).B(0x83, 0x04, 0x24, 0x02, 0xcf);
        a.L("hdef").B(0xf4);
        var c = Make("protmode", a, 96, k =>
        {
            k.Eq("store/load above 1 MB", k.M32(0x9400), 0xcafebabe); k.Eq("phys 0x100000", k.M32(0x100000), 0xcafebabe);
            k.Eq("int 30h through the IDT", k.M32(0x9404), 0x12345678);
            k.Eq("#GP error code", k.M32(0x940c), 0x40); k.Eq("resumed after #GP", k.M32(0x9410), 0x600d);
            k.Eq("CS", k.Cpu.SegSel[NesEmulator.X86.I386.SEG_CS], 0x08); k.Eq("32-bit code", k.Cpu.Code16 ? 1u : 0u, 0);
        });
        PmTables(c, a["hdef"], (0x30, a["hint"], 0x8e), (13, a["hgp"], 0x8e));
        return c;
    }

    private static X86Case Paging()
    {
        var a = new Asm(Org);
        PmEntry(a);
        a.B(0xbf).D(0x21000).B(0xb8).D(3).B(0xb9).D(1024).L("pt").B(0xab, 0x05).D(0x1000).B(0xe2).Rel8("pt");   // identity page table
        a.B(0xc7, 0x05).D(0x20000).D(0x21003).B(0xc7, 0x05).D(0x20004).D(0x22003).B(0xc7, 0x05).D(0x22000).D(0x200003);
        a.B(0xb8).D(0x20000).B(0x0f, 0x22, 0xd8).B(0x0f, 0x20, 0xc0, 0x0d).D(0x80000000).B(0x0f, 0x22, 0xc0, 0xeb, 0x00); // CR3, CR0.PG
        a.B(0xc7, 0x05).D(0x400010).D(0x11223344).B(0xa1).D(0x200010).B(0xa3).D(0x9500);   // write through the remapped page
        a.B(0xc7, 0x05).D(0x401000).D(0x55667788);                                          // not present: #PF, mapped, retried
        a.B(0xa1).D(0x201000).B(0xa3).D(0x950c).B(0xa1).D(0x22000).B(0xa3).D(0x9510).B(0xa1).D(0x20004).B(0xa3).D(0x9514).B(0xf4);
        a.L("hpf").B(0x0f, 0x20, 0xd0, 0xa3).D(0x9504).B(0x58, 0xa3).D(0x9508).B(0xc7, 0x05).D(0x22004).D(0x201003).B(0xcf);
        a.L("hdef").B(0xf4);
        var c = Make("paging", a, 1200 * 3, k =>
        {
            k.Eq("write via linear 0x400010 lands at phys 0x200010", k.M32(0x9500), 0x11223344);
            k.Eq("#PF CR2", k.M32(0x9504), 0x401000); k.Eq("#PF error code (write, not present)", k.M32(0x9508), 2);
            k.Eq("retried write landed at phys 0x201000", k.M32(0x950c), 0x55667788);
            k.Eq("PTE accessed + dirty", k.M32(0x9510), 0x200063); k.Eq("PDE accessed", k.M32(0x9514), 0x22023);
            k.Eq("paging on", k.Cpu.Cr0 >> 31, 1);
        });
        PmTables(c, a["hdef"], (14, a["hpf"], 0x8e));
        return c;
    }

    private static X86Case Ring3()
    {
        var a = new Asm(Org);
        PmEntry(a);
        a.B(0x66, 0xb8).W(0x28).B(0x0f, 0x00, 0xd8);                                    // ltr 28h
        a.B(0x6a, 0x23).B(0x68).D(0x70000).B(0x9c).B(0x6a, 0x1b).B(0x68).Abs32("r3").B(0xcf);   // iretd to ring 3
        a.L("r3").B(0x66, 0xb8).W(0x23).B(0x8e, 0xd8).B(0xc7, 0x05).D(0x9600).D(0x33).B(0xcd, 0x31, 0xeb, 0xfe);
        a.L("h31").B(0x8b, 0x44, 0x24, 0x04, 0xa3).D(0x9604).B(0x8b, 0x44, 0x24, 0x10, 0xa3).D(0x9608).B(0x89, 0x25).D(0x960c).B(0xf4);
        a.L("hdef").B(0xf4);
        var c = Make("ring3", a, 96, k =>
        {
            k.Eq("ran at ring 3", k.M32(0x9600), 0x33);
            k.Eq("pushed CS (ring 3)", k.M32(0x9604), 0x1b); k.Eq("pushed SS (ring 3)", k.M32(0x9608), 0x23);
            k.Eq("ESP from TSS.ESP0 minus 5 dwords", k.M32(0x960c), 0x7ffec);
            k.Eq("CPL back to 0", (uint)k.Cpu.Cpl, 0); k.Eq("CS", k.Cpu.SegSel[NesEmulator.X86.I386.SEG_CS], 0x08);
        });
        PmTables(c, a["hdef"], (0x31, a["h31"], 0xee));
        var tss = new byte[0x68];
        BitConverter.GetBytes(0x80000u).CopyTo(tss, 4); BitConverter.GetBytes(0x10u).CopyTo(tss, 8);
        c.Bytes(Tss, tss);
        return c;
    }
}
