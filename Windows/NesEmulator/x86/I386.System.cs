// Intel 80386 / 80486 core: protected-mode control transfers (far call / jump through gates, interrupts and exceptions, far return / IRET,
// task switches), I/O permission and the descriptor-query instructions. Ported from tiny386 i386.c (task_switch, pmcall, call_isr,
// pmret, check_ioperm, larsl_helper, verrw_helper), commit 2c1205c, BSD-3-Clause, Copyright (c) 2024-2025, Chunhui He.
// See x86/THIRD_PARTY_NOTICES.md.

namespace NesEmulator.X86;

public sealed partial class I386
{
    private const int TS_JMP = 0, TS_CALL = 1, TS_IRET = 2;

    // tiny386 TRY1: a failure here aborts
    private void Try1(bool ok, string where) { if (!ok) Abort(where); }

    private bool TaskSwitch(uint tss, int swType)
    {
        var m = new OptAddr();
        uint oldtss = SegSel[SEG_TR];
        uint trType = SegFlags[SEG_TR] & 0xf;
        if (trType != 9 && trType != 11) Abort("task switch from a non-TSS");

        Try1(Translate(ref m, 2, SEG_TR, 0x20, 4, 0), "ts"); Store32(ref m, NextIp);
        RefreshFlags();
        Try1(Translate(ref m, 2, SEG_TR, 0x24, 4, 0), "ts"); Store32(ref m, swType == TS_IRET ? Flags & ~NT : Flags);
        for (int i = 0; i < 8; i++) { Try1(Translate(ref m, 2, SEG_TR, (uint)(0x28 + 4 * i), 4, 0), "ts"); Store32(ref m, R[i]); }
        for (int i = 0; i < 6; i++) { Try1(Translate(ref m, 2, SEG_TR, (uint)(0x48 + 4 * i), 4, 0), "ts"); Store32(ref m, SegSel[i]); }

        if (swType == TS_JMP || swType == TS_IRET)   // clear the busy bit
        {
            uint a = GdtBase + (SegSel[SEG_TR] & ~7u);
            Try1(TranslateLaddr(ref m, 3, a + 4, 4, 0), "ts"); Store32(ref m, Load32(ref m) & ~(1u << 9));
        }

        Try1(SetSeg(SEG_TR, tss), "ts");
        uint newType = SegFlags[SEG_TR] & 0xf;
        if (newType != 9 && newType != 11) Abort("task switch to a non-TSS");

        if (swType == TS_JMP || swType == TS_CALL)   // set the busy bit
        {
            uint a = GdtBase + (tss & ~7u);
            Try1(TranslateLaddr(ref m, 3, a + 4, 4, 0), "ts"); Store32(ref m, Load32(ref m) | (1u << 9));
            SegFlags[SEG_TR] |= 2;
        }

        Cr0 |= 1 << 3; // TS

        Try1(Translate(ref m, 1, SEG_TR, 0x60, 4, 0), "ts"); Try1(SetSeg(SEG_LDT, Load32(ref m)), "ts");
        for (int i = 0; i < 8; i++) { Try1(Translate(ref m, 1, SEG_TR, (uint)(0x28 + 4 * i), 4, 0), "ts"); R[i] = Load32(ref m); }
        for (int i = 0; i < 6; i++) { Try1(Translate(ref m, 1, SEG_TR, (uint)(0x48 + 4 * i), 4, 0), "ts"); Try1(SetSeg(i, Load32(ref m)), "ts"); }
        Try1(Translate(ref m, 1, SEG_TR, 0x20, 4, 0), "ts"); NextIp = Load32(ref m);
        Try1(Translate(ref m, 1, SEG_TR, 0x24, 4, 0), "ts");
        Flags = Load32(ref m); Flags &= FlagsMask; Flags |= 2;
        _ccMask = 0;
        if (swType == TS_CALL)
        {
            Try1(Translate(ref m, 2, SEG_TR, 0, 4, 0), "ts"); Store32(ref m, oldtss);
            Flags |= NT;
        }
        Try1(Translate(ref m, 1, SEG_TR, 0x1c, 4, 0), "ts"); Cr3 = Load32(ref m);
        TlbClear();
        return true;
    }

    private bool PmCall(bool opsz16, uint addr, uint sel, bool isjmp)
    {
        sel &= 0xffff;
        uint spMask = SsMask();
        if ((sel & ~3u) == 0) return Throw(EX_GP, 0);
        if (!ReadDesc(sel, out uint w1, out uint w2)) return false;

        bool s = ((w2 >> 12) & 1) != 0;
        int dpl = (int)((w2 >> 13) & 3);
        bool p = ((w2 >> 15) & 1) != 0;
        if (!p) return Throw(EX_NP, sel & ~3u);

        if (s)
        {
            bool code = ((w2 >> 8) & 8) != 0, conforming = ((w2 >> 8) & 4) != 0;
            if (!code) return Throw(EX_GP, sel & ~3u);
            if (conforming) { if (dpl > Cpl) return Throw(EX_GP, sel & ~3u); }
            else if ((sel & 3) > Cpl || dpl != Cpl) return Throw(EX_GP, sel & ~3u);
            sel = (sel & 0xfffc) | (uint)Cpl;

            if (!isjmp)
            {
                var m1 = new OptAddr(); var m2 = new OptAddr();
                uint sp = R[4];
                if (opsz16)
                {
                    if (!Translate(ref m1, 2, SEG_SS, (sp - 2) & spMask, 2, 0) || !Translate(ref m2, 2, SEG_SS, (sp - 4) & spMask, 2, 0)) return false;
                    SetSp(sp - 4, spMask); Store16(ref m1, SegSel[SEG_CS]); Store16(ref m2, NextIp);
                }
                else
                {
                    if (!Translate(ref m1, 2, SEG_SS, (sp - 4) & spMask, 4, 0) || !Translate(ref m2, 2, SEG_SS, (sp - 8) & spMask, 4, 0)) return false;
                    SetSp(sp - 8, spMask); Store32(ref m1, SegSel[SEG_CS]); Store32(ref m2, NextIp);
                }
            }
            Try1(SetSeg(SEG_CS, sel), "pmcall cs");
            NextIp = addr;
            return true;
        }

        uint newcs = w1 >> 16;
        uint newip = (w1 & 0xffff) | (w2 & 0xffff0000);
        int gt = (int)((w2 >> 8) & 0xf);
        int wc = (int)(w2 & 31);
        if (dpl < Cpl || dpl < (int)(sel & 3)) return Throw(EX_GP, sel & ~3u);

        uint trType = SegFlags[SEG_TR] & 0xf;   // only a 32-bit TSS is supported
        if (trType == 9 || trType == 11)
        {
            if (gt == 9) return TaskSwitch(sel, isjmp ? TS_JMP : TS_CALL);
            if (gt == 5) return TaskSwitch(newcs, isjmp ? TS_JMP : TS_CALL);
        }
        if (gt != 4 && gt != 12) Abort($"pmcall: gate type {gt}");

        // call gate
        if ((newcs & ~3u) == 0) return Throw(EX_GP, 0);
        if (!ReadDesc(newcs, out _, out uint neww2, false)) return false;
        if (((neww2 >> 11) & 3) != 3) return Throw(EX_GP, newcs & ~3u);
        int newdpl = (int)((neww2 >> 13) & 3);
        if (((neww2 >> 15) & 1) == 0) return Throw(EX_NP, newcs & ~3u);
        if (newdpl > Cpl) return Throw(EX_GP, newcs & ~3u);
        bool conf = ((neww2 >> 8) & 4) != 0;
        bool gate16 = gt == 4;
        if (gate16) newip &= 0xffff;

        if (!conf && newdpl < Cpl)
        {
            // more privilege
            var msp0 = new OptAddr(); var mss0 = new OptAddr();
            uint oldss = SegSel[SEG_SS], oldsp = R[4];
            var pars = new uint[31];
            uint sm = SsMask();
            for (int i = 0; i < wc; i++)
            {
                var m = new OptAddr();
                if (!gate16) { if (!Translate(ref m, 1, SEG_SS, (oldsp + 4 * (uint)i) & sm, 4, 0)) return false; pars[i] = Load32(ref m); }
                else { if (!Translate(ref m, 1, SEG_SS, (oldsp + 2 * (uint)i) & sm, 2, 0)) return false; pars[i] = Load16(ref m); }
            }
            if ((SegFlags[SEG_TR] & 8) == 0)
            {
                if (!Translate(ref msp0, 1, SEG_TR, 2 + 4 * (uint)newdpl, 2, 0) || !Translate(ref mss0, 1, SEG_TR, 4 + 4 * (uint)newdpl, 2, 0)) return false;
                R[4] = Load16(ref msp0);
                if (!SetSeg(SEG_SS, Load16(ref mss0))) return false;
            }
            else
            {
                if (!Translate(ref msp0, 1, SEG_TR, 4 + 8 * (uint)newdpl, 4, 0) || !Translate(ref mss0, 1, SEG_TR, 8 + 8 * (uint)newdpl, 4, 0)) return false;
                R[4] = Load32(ref msp0);
                if (!SetSeg(SEG_SS, Load32(ref mss0))) return false;
            }
            sm = SsMask();
            if (!isjmp)
            {
                uint n = gate16 ? 2u : 4u; int sz = (int)n;
                var m1 = new OptAddr(); var m2 = new OptAddr(); var m3 = new OptAddr(); var m4 = new OptAddr();
                uint sp = R[4];
                Try1(Translate(ref m1, 2, SEG_SS, (sp - n * 1) & sm, sz, 0), "pmcall");
                Try1(Translate(ref m2, 2, SEG_SS, (sp - n * 2) & sm, sz, 0), "pmcall");
                Try1(Translate(ref m3, 2, SEG_SS, (sp - n * (3 + (uint)wc)) & sm, sz, 0), "pmcall");
                Try1(Translate(ref m4, 2, SEG_SS, (sp - n * (4 + (uint)wc)) & sm, sz, 0), "pmcall");
                for (int i = 0; i < wc; i++)
                {
                    var m = new OptAddr();
                    Try1(Translate(ref m, 2, SEG_SS, (sp - n * (2 + (uint)wc - (uint)i)) & sm, sz, 0), "pmcall");
                    Store(sz * 8, ref m, pars[i]);
                }
                Store(sz * 8, ref m1, oldss); Store(sz * 8, ref m2, oldsp);
                Store(sz * 8, ref m3, SegSel[SEG_CS]); Store(sz * 8, ref m4, NextIp);
                SetSp(sp - n * (4 + (uint)wc), sm);
            }
            newcs = (newcs & 0xfffc) | (uint)newdpl;
        }
        else
        {
            // same privilege
            if (!isjmp)
            {
                var m1 = new OptAddr(); var m2 = new OptAddr();
                uint sp = R[4], sm = SsMask();
                uint n = gate16 ? 2u : 4u; int sz = (int)n;
                if (!Translate(ref m1, 2, SEG_SS, (sp - n) & sm, sz, 0) || !Translate(ref m2, 2, SEG_SS, (sp - 2 * n) & sm, sz, 0)) return false;
                Store(sz * 8, ref m1, SegSel[SEG_CS]); Store(sz * 8, ref m2, NextIp);
                SetSp(sp - 2 * n, sm);
            }
            newcs = (newcs & 0xfffc) | (uint)Cpl;
        }
        Try1(SetSeg(SEG_CS, newcs), "pmcall gate cs");
        NextIp = newip;
        return true;
    }

    /// <summary>tiny386 __call_isr_check_cs: 0 = fault (Excno set), 1 = same level, 2 = to an inner level, 3 = from virtual-8086 mode.</summary>
    private int CallIsrCheckCs(uint sel, int ext, out int csdpl)
    {
        csdpl = 0;
        sel &= 0xffff;
        var m = new OptAddr();
        uint off = sel & ~7u, bas, limit;
        if ((sel & 4) != 0) { bas = SegBase[SEG_LDT]; limit = SegLimit[SEG_LDT]; }
        else { bas = GdtBase; limit = GdtLimit; }
        if ((sel & ~3u) == 0 || off + 7 > limit) { Throw(EX_GP, (uint)ext); return 0; }
        Try1(TranslateLaddr(ref m, 1, bas + off + 4, 4, 0), "isr cs");
        uint w2 = Load32(ref m);
        bool s = ((w2 >> 12) & 1) != 0, code = ((w2 >> 8) & 8) != 0, conforming = ((w2 >> 8) & 4) != 0;
        int dpl = (int)((w2 >> 13) & 3);
        bool p = ((w2 >> 15) & 1) != 0;
        csdpl = dpl;
        if (!s || !code || dpl > Cpl) { Throw(EX_GP, (sel & ~3u) | (uint)ext); return 0; }
        if (!p) { Throw(EX_NP, sel & ~3u); return 0; }
        if (!conforming && dpl < Cpl)
        {
            if ((Flags & VM) == 0) return 2;
            if (dpl != 0) { Throw(EX_GP, (sel & ~3u) | (uint)ext); return 0; }
            return 3;
        }
        if ((Flags & VM) != 0) { Throw(EX_GP, (sel & ~3u) | (uint)ext); return 0; }
        if (conforming || dpl == Cpl) return 1;
        Throw(EX_GP, (sel & ~3u) | (uint)ext);
        return 0;
    }

    private bool CallIsr(int no, bool pusherr, int ext)
    {
        if ((Cr0 & 1) == 0)
        {
            // real-address mode
            uint spMask = SsMask();
            var m = new OptAddr();
            Try1(TranslateLaddr(ref m, 1, IdtBase + (uint)no * 4, 4, 0), "ivt");
            uint w1 = Load32(ref m);
            uint newcs = w1 >> 16, newip = w1 & 0xffff;
            var m1 = new OptAddr(); var m2 = new OptAddr(); var m3 = new OptAddr();
            uint sp = R[4];
            Try1(Translate(ref m1, 2, SEG_SS, (sp - 2) & spMask, 2, 0), "rm isr");
            Try1(Translate(ref m2, 2, SEG_SS, (sp - 4) & spMask, 2, 0), "rm isr");
            Try1(Translate(ref m3, 2, SEG_SS, (sp - 6) & spMask, 2, 0), "rm isr");
            RefreshFlags(); _ccMask = 0;
            Store16(ref m1, Flags); Store16(ref m2, SegSel[SEG_CS]); Store16(ref m3, Ip);
            SetSp(sp - 6, spMask);
            Try1(SetSeg(SEG_CS, newcs), "rm isr cs");
            NextIp = newip; Ip = newip;
            Flags &= ~(IF | TF);
            return true;
        }

        // protected mode
        var md = new OptAddr();
        uint offs = (uint)no << 3;
        if (offs + 7 > IdtLimit) return Throw(EX_GP, offs | 2 | (uint)ext);
        Try1(TranslateLaddr(ref md, 1, IdtBase + offs, 4, 0), "idt");
        uint gw1 = Load32(ref md);
        Try1(TranslateLaddr(ref md, 1, IdtBase + offs + 4, 4, 0), "idt");
        uint gw2 = Load32(ref md);

        int gt = (int)((gw2 >> 8) & 0xf);
        if (gt != 6 && gt != 7 && gt != 0xe && gt != 0xf && gt != 5) return Throw(EX_GP, offs | 2 | (uint)ext);
        int gdpl = (int)((gw2 >> 13) & 3);
        if (ext == 0 && gdpl < Cpl) return Throw(EX_GP, offs | 2);
        if (((gw2 >> 15) & 1) == 0) return Throw(EX_NP, offs | 2 | (uint)ext);

        if (gt == 5)
        {
            // task gate; an exception's error code goes onto the new task's stack
            if (!TaskSwitch(gw1 >> 16, TS_CALL)) return false;
            if (pusherr)
            {
                var m1 = new OptAddr();
                uint sp = R[4], sm = SsMask();
                if (!Translate(ref m1, 2, SEG_SS, (sp - 4) & sm, 4, Cpl)) return false;
                Store32(ref m1, Excerr);
                SetSp(sp - 4, sm);
            }
            return true;
        }

        // trap or interrupt gate
        uint gcs = gw1 >> 16;
        uint gip = (gw1 & 0xffff) | (gw2 & 0xffff0000);
        bool gate16 = gt == 6 || gt == 7;
        if (gate16) gip &= 0xffff;
        uint n = gate16 ? 2u : 4u; int sz = (int)n, bits = sz * 8;

        switch (CallIsrCheckCs(gcs, ext, out int csdpl))
        {
            case 0: return false;
            case 1: // same level
            {
                var m1 = new OptAddr(); var m2 = new OptAddr(); var m3 = new OptAddr(); var m4 = new OptAddr();
                uint sp = R[4], sm = SsMask();
                if (!Translate(ref m1, 2, SEG_SS, (sp - n) & sm, sz, 0)) return false;
                if (!Translate(ref m2, 2, SEG_SS, (sp - n * 2) & sm, sz, 0)) return false;
                if (!Translate(ref m3, 2, SEG_SS, (sp - n * 3) & sm, sz, 0)) return false;
                if (pusherr && !Translate(ref m4, 2, SEG_SS, (sp - n * 4) & sm, sz, 0)) return false;
                RefreshFlags(); _ccMask = 0;
                Store(bits, ref m1, Flags); Store(bits, ref m2, SegSel[SEG_CS]); Store(bits, ref m3, Ip);
                if (pusherr) { Store(bits, ref m4, Excerr); SetSp(sp - n * 4, sm); }
                else SetSp(sp - n * 3, sm);
                gcs = (gcs & ~3u) | (uint)Cpl;
                break;
            }
            case 2: // to an inner level
            {
                var msp0 = new OptAddr(); var mss0 = new OptAddr();
                int newpl = csdpl;
                uint oldss = SegSel[SEG_SS], oldsp = R[4], newss, newsp;
                if ((SegFlags[SEG_TR] & 8) != 0)
                {
                    if (!Translate(ref msp0, 1, SEG_TR, 4 + 8 * (uint)newpl, 4, 0) || !Translate(ref mss0, 1, SEG_TR, 8 + 8 * (uint)newpl, 4, 0)) return false;
                    newsp = Load32(ref msp0); newss = Load32(ref mss0) & 0xffff;
                }
                else
                {
                    if (!Translate(ref msp0, 1, SEG_TR, 2 + 4 * (uint)newpl, 2, 0) || !Translate(ref mss0, 1, SEG_TR, 4 + 4 * (uint)newpl, 2, 0)) return false;
                    newsp = Load16(ref msp0); newss = Load16(ref mss0);
                }
                R[4] = newsp;
                if (!SetSeg(SEG_SS, newss)) return false;
                uint sm = SsMask(), sp = R[4];
                var mm = new OptAddr[6];
                for (int i = 0; i < 5; i++) if (!Translate(ref mm[i], 2, SEG_SS, (sp - n * (uint)(i + 1)) & sm, sz, 0)) return false;
                if (pusherr && !Translate(ref mm[5], 2, SEG_SS, (sp - n * 6) & sm, sz, 0)) return false;
                Store(bits, ref mm[0], oldss); Store(bits, ref mm[1], oldsp);
                RefreshFlags(); _ccMask = 0;
                Store(bits, ref mm[2], Flags); Store(bits, ref mm[3], SegSel[SEG_CS]); Store(bits, ref mm[4], Ip);
                if (pusherr) Store(bits, ref mm[5], Excerr);
                uint used = n * (pusherr ? 6u : 5u);
                if (gate16) SetSp(sp - used, sm); else R[4] = sp - used;
                gcs = (gcs & ~3u) | (uint)newpl;
                break;
            }
            case 3: // from virtual-8086 mode
            {
                if (csdpl != 0) Abort("v86 isr: dpl");
                if (gate16) Abort("v86 isr: 16-bit gate");
                if ((SegFlags[SEG_TR] & 8) == 0) Abort("v86 isr: 16-bit TSS");
                var msp0 = new OptAddr(); var mss0 = new OptAddr();
                uint oldss = SegSel[SEG_SS], oldsp = R[4];
                if (!Translate(ref msp0, 1, SEG_TR, 4, 4, 0) || !Translate(ref mss0, 1, SEG_TR, 8, 4, 0)) return false;
                uint newsp = Load32(ref msp0), newss = Load32(ref mss0) & 0xffff;
                uint oldflags = Flags;
                Flags &= ~VM;
                R[4] = newsp;
                if (!SetSeg(SEG_SS, newss)) { Flags = oldflags; R[4] = oldsp; return false; }
                uint sm = SsMask(), sp = R[4];
                var mm = new OptAddr[10];
                for (int i = 0; i < 9; i++) Try1(Translate(ref mm[i], 2, SEG_SS, (sp - 4 * (uint)(i + 1)) & sm, 4, 0), "v86 isr");
                if (pusherr && !Translate(ref mm[9], 2, SEG_SS, (sp - 40) & sm, 4, 0)) return false;
                Store32(ref mm[0], SegSel[SEG_GS]); Store32(ref mm[1], SegSel[SEG_FS]);
                Store32(ref mm[2], SegSel[SEG_DS]); Store32(ref mm[3], SegSel[SEG_ES]);
                Store32(ref mm[4], oldss); Store32(ref mm[5], oldsp);
                RefreshFlags(); _ccMask = 0;
                Store32(ref mm[6], Flags | VM);
                Store32(ref mm[7], SegSel[SEG_CS]); Store32(ref mm[8], Ip);
                if (pusherr) { Store32(ref mm[9], Excerr); SetSp(sp - 40, sm); }
                else SetSp(sp - 36, sm);
                gcs = gcs & ~3u;
                Try1(SetSeg(SEG_DS, 0), "v86"); Try1(SetSeg(SEG_ES, 0), "v86"); Try1(SetSeg(SEG_FS, 0), "v86"); Try1(SetSeg(SEG_GS, 0), "v86");
                Flags &= ~(TF | RF | NT);
                Try1(SetSeg(SEG_CS, gcs), "v86 cs");
                NextIp = gip; Ip = gip;
                if (gt == 6 || gt == 0xe) Flags &= ~IF;
                return true;
            }
        }
        Try1(SetSeg(SEG_CS, gcs), "isr cs");
        NextIp = gip; Ip = gip;
        Flags &= ~(TF | RF | NT);
        if (gt == 6 || gt == 0xe) Flags &= ~IF;
        return true;
    }

    private bool PmiretCheckCs(uint sel, bool outer)
    {
        sel &= 0xffff;
        if ((sel & ~3u) == 0) return Throw(EX_GP, sel & ~3u);
        if (!ReadDesc(sel, out _, out uint w2, false)) return false;
        bool s = ((w2 >> 12) & 1) != 0, code = ((w2 >> 8) & 8) != 0, conforming = ((w2 >> 8) & 4) != 0;
        int dpl = (int)((w2 >> 13) & 3);
        bool p = ((w2 >> 15) & 1) != 0;
        if (!s || !code) return Throw(EX_GP, sel & ~3u);
        if (!outer)
        {
            if (!conforming) { if (dpl != Cpl) return Throw(EX_GP, sel & ~3u); }
            else if (dpl > Cpl) return Throw(EX_GP, sel & ~3u);
        }
        else
        {
            if (!conforming) { if (dpl != (int)(sel & 3)) return Throw(EX_GP, sel & ~3u); }
            else if (dpl <= Cpl) return Throw(EX_GP, sel & ~3u);
        }
        if (!p) return Throw(EX_NP, sel & ~3u);
        return true;
    }

    private bool PmRet(bool opsz16, int off, bool isiret)
    {
        if (isiret)
        {
            if ((Flags & VM) != 0) return Throw(EX_GP, 0);
            if ((Flags & NT) != 0)
            {
                var m = new OptAddr();
                if (!Translate(ref m, 1, SEG_TR, 0, 2, 0)) return false;
                uint back = Load16(ref m);
                if (back == 0) return Throw(EX_TS, 0);
                return TaskSwitch(back, TS_IRET);
            }
            off += opsz16 ? 2 : 4;
        }

        var m1 = new OptAddr(); var m2 = new OptAddr(); var m3 = new OptAddr(); var m4 = new OptAddr(); var m5 = new OptAddr();
        uint spMask = SsMask(), sp = R[4], oldflags = Flags, newip, newcs, newflags = 0;
        if (opsz16)
        {
            if (!Translate(ref m1, 1, SEG_SS, sp & spMask, 2, 0) || !Translate(ref m2, 1, SEG_SS, (sp + 2) & spMask, 2, 0)) return false;
            if (isiret) { if (!Translate(ref m3, 1, SEG_SS, (sp + 4) & spMask, 2, 0)) return false; newflags = (oldflags & 0xffff0000) | Load16(ref m3); }
            newip = Load16(ref m1); newcs = Load16(ref m2);
        }
        else
        {
            if (!Translate(ref m1, 1, SEG_SS, sp & spMask, 4, 0) || !Translate(ref m2, 1, SEG_SS, (sp + 4) & spMask, 4, 0)) return false;
            if (isiret) { if (!Translate(ref m3, 1, SEG_SS, (sp + 8) & spMask, 4, 0)) return false; newflags = Load32(ref m3); }
            newip = Load32(ref m1); newcs = Load32(ref m2);
        }

        if (isiret)
        {
            uint mask = 0;
            if (Cpl > 0) mask |= IOPL;
            if (GetIOPL() < Cpl) mask |= IF;
            newflags = (oldflags & mask) | (newflags & ~mask);
            newflags &= FlagsMask; newflags |= 2;
        }

        if (isiret && (newflags & VM) != 0)
        {
            // return to virtual-8086 mode
            if (Cpl != 0) Abort("iret to v86 from cpl != 0");
            if (opsz16) Abort("16-bit iret to v86");
            var mes = new OptAddr(); var mds = new OptAddr(); var mfs = new OptAddr(); var mgs = new OptAddr();
            if (!Translate(ref m4, 1, SEG_SS, (sp + 12) & spMask, 4, 0) || !Translate(ref m5, 1, SEG_SS, (sp + 16) & spMask, 4, 0)
                || !Translate(ref mes, 1, SEG_SS, (sp + 20) & spMask, 4, 0) || !Translate(ref mds, 1, SEG_SS, (sp + 24) & spMask, 4, 0)
                || !Translate(ref mfs, 1, SEG_SS, (sp + 28) & spMask, 4, 0) || !Translate(ref mgs, 1, SEG_SS, (sp + 32) & spMask, 4, 0)) return false;
            Flags = newflags;
            Try1(SetSeg(SEG_CS, newcs), "iret v86");
            SetSp(sp + 12, spMask);
            NextIp = newip & 0xffff;
            Try1(SetSeg(SEG_SS, Load32(ref m5)), "iret v86");
            Try1(SetSeg(SEG_ES, Load32(ref mes)), "iret v86");
            Try1(SetSeg(SEG_DS, Load32(ref mds)), "iret v86");
            Try1(SetSeg(SEG_FS, Load32(ref mfs)), "iret v86");
            if (!SetSeg(SEG_GS, Load32(ref mgs))) return false;
            SetSp(Load32(ref m4), 0xffffffff);
        }
        else
        {
            int rpl = (int)(newcs & 3);
            if (rpl < Cpl) return Throw(EX_GP, newcs & ~3u);
            if (rpl == Cpl)
            {
                if (!PmiretCheckCs(newcs, false)) return false;
                if (isiret) Flags = newflags;
                Try1(SetSeg(SEG_CS, newcs), "ret cs");
                SetSp(sp + (opsz16 ? 4u : 8u) + (uint)off, spMask);
                NextIp = newip;
            }
            else
            {
                if (!PmiretCheckCs(newcs, true)) return false;
                uint newsp, newss;
                if (opsz16)
                {
                    if (!Translate(ref m4, 1, SEG_SS, (sp + 4 + (uint)off) & spMask, 2, 0) || !Translate(ref m5, 1, SEG_SS, (sp + 6 + (uint)off) & spMask, 2, 0)) return false;
                    newsp = Load16(ref m4); newss = Load16(ref m5);
                }
                else
                {
                    if (!Translate(ref m4, 1, SEG_SS, (sp + 8 + (uint)off) & spMask, 4, 0) || !Translate(ref m5, 1, SEG_SS, (sp + 12 + (uint)off) & spMask, 4, 0)) return false;
                    newsp = Load32(ref m4); newss = Load32(ref m5);
                }
                if (!isiret) newsp += (uint)off;
                if (isiret) Flags = newflags;
                Try1(SetSeg(SEG_CS, newcs), "ret outer cs");
                Try1(SetSeg(SEG_SS, newss), "ret outer ss");
                SetSp(newsp, SsMask());
                NextIp = newip;
                ClearSegs();
            }
        }
        if (isiret) _ccMask = 0;
        return true;
    }

    private bool CheckIoperm(int port, int bits)
    {
        bool allow = true;
        if ((Cr0 & 1) != 0 && (Cpl > GetIOPL() || (Flags & VM) != 0))
        {
            allow = false;
            if (SegLimit[SEG_TR] >= 103)
            {
                var m = new OptAddr();
                if (!Translate(ref m, 1, SEG_TR, 102, 2, 0)) return false;
                uint iobase = Load16(ref m);
                if (iobase + (uint)(port / 8) < SegLimit[SEG_TR])
                {
                    if (!Translate(ref m, 1, SEG_TR, iobase + (uint)(port / 8), 2, 0)) return false;
                    uint perm = Load16(ref m);
                    int len = bits / 8;
                    int bitIndex = port & 7;
                    uint mask = (1u << len) - 1;
                    if (((perm >> bitIndex) & mask) == 0) allow = true;
                }
            }
        }
        if (!allow) return Throw(EX_GP, 0);
        return true;
    }

    /// <summary>LAR (<paramref name="lar"/>) / LSL; zf = the selector was valid and readable at this level.</summary>
    private bool LarLsl(uint sel, bool lar, out uint res, out bool zf)
    {
        res = 0; zf = false;
        sel &= 0xffff;
        if ((Cr0 & 1) == 0 || (Flags & VM) != 0) return Throw0(EX_UD);
        if ((sel & ~3u) == 0) return true;
        if (!ReadDesc(sel, out uint w1, out uint w2)) return true;   // a bad selector clears ZF, no fault
        if (((w2 >> 12) & 1) != 0)
        {
            int dpl = (int)((w2 >> 13) & 3);
            if (((w2 >> 10) & 3) != 3 && (Cpl > dpl || (int)(sel & 3) > dpl)) return true;
        }
        else
        {
            int type = (int)((w2 >> 8) & 0xf);
            if (lar && type is 0 or 6 or 7 or 8 or 10 or 13 or 14 or 15) return true;
            if (!lar && type is 0 or 4 or 5 or 6 or 7 or 8 or 10 or 12 or 13 or 14 or 15) return true;
        }
        if (lar) res = w2 & 0x00ffff00;
        else
        {
            res = (w2 & 0xf0000) | (w1 & 0xffff);
            if ((w2 & 0x00800000) != 0) res = (res << 12) | 0xfff;
        }
        zf = true;
        return true;
    }

    private bool VerRW(uint sel, bool wr, out bool zf)
    {
        zf = false;
        sel &= 0xffff;
        if ((Cr0 & 1) == 0 || (Flags & VM) != 0) return Throw0(EX_UD);
        if ((sel & ~3u) == 0) return true;
        if (!ReadDesc(sel, out _, out uint w2)) return true;
        if (((w2 >> 12) & 1) == 0) return true;
        int dpl = (int)((w2 >> 13) & 3);
        if (((w2 >> 10) & 3) != 3 && (Cpl > dpl || (int)(sel & 3) > dpl)) return true;
        if (((w2 >> 11) & 1) == 0) { if (wr && ((w2 >> 9) & 1) == 0) return true; }   // data
        else if (!wr && ((w2 >> 9) & 1) == 0) return true;                              // code
        zf = true;
        return true;
    }
}
