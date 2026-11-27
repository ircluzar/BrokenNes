exec(open('C:/BrokenNes-work/scspeed/py/mmx.py').read().split("ed('I386.Mmx.cs'")[0])
ed('I386.Jit.cs',"    private void JitCountInterp(uint pa) { InterpAt[pa]","""    /// <summary>JITSTAT: interpreted instructions by mode (VM / 16-bit code / ring 0) and by the address's JIT entry state.</summary>
    private readonly long[,] _interpMode = new long[8, 6];
    public IEnumerable<string> InterpModes()
    {
        string[] st = { "no entry", "other start", "warming", "failed", "pending", "has code" };
        for (int md = 0; md < 8; md++)
        {
            long t = 0; for (int s = 0; s < 6; s++) t += _interpMode[md, s];
            if (t == 0) continue;
            yield return $"{((md & 4) != 0 ? "V86" : (md & 2) != 0 ? "16-bit" : "32-bit")} {((md & 1) != 0 ? "ring0" : "ring>0")}: {t}  (" + string.Join(", ", Enumerable.Range(0, 6).Where(s => _interpMode[md, s] > 0).Select(s => $"{st[s]} {_interpMode[md, s]}")) + ")";
        }
    }
    private void JitCountInterp(uint pa)
    {
        int md = ((Flags & VM) != 0 ? 4 : 0) | (Code16 ? 2 : 0) | (Cpl == 0 ? 1 : 0);
        var en = _je[JitIndex(pa)];
        int s = en == null ? 0 : en.Start != pa ? 1 : en.Failed ? 3 : en.Pending ? 4 : en.Code != null ? 5 : 2;
        _interpMode[md, s]++;
        JitCountInterp0(pa);
    }
    private void JitCountInterp0(uint pa) { InterpAt[pa]""")
