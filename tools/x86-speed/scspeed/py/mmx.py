def ed(p,old,new,cnt=1):
    s=open(p,newline='').read(); o=old.replace('\n','\r\n'); n=new.replace('\n','\r\n')
    assert s.count(o)==cnt,(p,old[:60],s.count(o)); s=s.replace(o,n); open(p,'w',newline='').write(s)
ed('I386.Mmx.cs',"""        if (!DecodeModrm()) return FAIL;
        int d = _reg;""","""        if (!DecodeModrm()) return FAIL;
        return MmxBody(b1);
    }
    /// <summary>A compiled MMX instruction (DK_MMX, after JPrep: the ModRM already decoded): the interpreter's own checks and body.</summary>
    private bool JMmx(int b1)
    {
        if ((Cr0 & 4) != 0) { K(Throw0(EX_UD)); return false; }
        if ((Cr0 & 8) != 0) { K(Throw0(EX_NM)); return false; }
        if ((_fsw & FSW_ES) != 0 && (Cr0 & 0x20) != 0) { K(Throw0(EX_MF)); return false; }
        if (_mod != 3) Cycle += _memCost;   // (as DecodeModrm)
        return MmxBody((uint)b1) != FAIL;
    }
    private int MmxBody(uint b1)
    {
        int d = _reg;""")
ed('I386.Cache.cs',"""                if (b == 0xaf) { kind = DK_IMUL; modrm = true; op = 0; break; }""","""                if (b == 0xaf) { kind = DK_IMUL; modrm = true; op = 0; break; }
                if (_hasMmx && b != 0x77 && MmxOpcode(b) && !NoMmxJit) { kind = DK_MMX; op = (int)b; modrm = true; break; }   // MMX: MmxBody through a helper (StarCraft's menu / video code; X86_NOMMXJIT: off)""")
ed('I386.Cache.cs',"DK_FLAGOP = 36, DK_STKOP = 37;","DK_FLAGOP = 36, DK_STKOP = 37, DK_MMX = 38;")
ed('I386.Jit.cs','"JFpu", ','"JFpu", "JMmx", ')
ed('I386.Jit.cs',"if (e.Kind is not (DK_LEA or DK_FPU))","if (e.Kind is not (DK_LEA or DK_FPU or DK_MMX))")
ed('I386.Jit.cs',"DK_MOVSR or DK_SRMOV or DK_FPU;","DK_MOVSR or DK_SRMOV or DK_FPU or DK_MMX;")
ed('I386.Jit.cs',"    private static readonly bool NoHelperOps =","    private static readonly bool NoMmxJit = Environment.GetEnvironmentVariable(\"X86_NOMMXJIT\") != null;\n    private static readonly bool NoHelperOps =")
ed('I386.Jit.cs',"""                case DK_FPU:
                {""","""                case DK_MMX:
                {
                    Prep();
                    Ld0(); I4(op); Call("JMmx"); FailIfFalse();
                    if (mem && op is 0x7e or 0x7f)
                    {   // a store: it may have written to this code, leave right after it
                        var same = il.DefineLabel();
                        Ld0(); Call("JCodeSame"); il.Emit(OpCodes.Brtrue, same);
                        StDec(); LeaveAfter(k, () => IpAt(pe));
                        il.MarkLabel(same);
                    }
                    break;
                }
                case DK_FPU:
                {""")
