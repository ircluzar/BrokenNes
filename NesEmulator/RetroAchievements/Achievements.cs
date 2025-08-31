using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace NesEmulator.RetroAchievements
{
    /// <summary>
    /// Lightweight RAM-domain abstraction for the achievements engine.
    /// Implementations must read from the emulator's RAM domain (CPU System RAM for NES).
    /// </summary>
    public interface IRamDomain
    {
        int Size { get; }
        /// <summary>Copy the full RAM into the destination span (length must be at least Size).</summary>
        void CopyRam(Span<byte> destination);
        /// <summary>Optional single-byte peek, used for some fast-paths. Default uses CopyRam.</summary>
        byte Peek(int index)
        {
            Span<byte> tmp = stackalloc byte[1];
            CopyRam(tmp);
            return tmp[0];
        }
    }

    /// <summary>
    /// Adapter for this project's NES class that exposes the System RAM (2KB) as an IRamDomain.
    /// Uses NES.PeekSystemRam(index). Stays within RAM domain by design.
    /// </summary>
    public sealed class NesRamDomain : IRamDomain
    {
        private readonly NES _nes;
        public NesRamDomain(NES nes) { _nes = nes; }
        public int Size => 2048; // NES internal RAM is 2KB (mirrored in CPU map)
        public void CopyRam(Span<byte> destination)
        {
            int len = Math.Min(Size, destination.Length);
            for (int i = 0; i < len; i++) destination[i] = _nes.PeekSystemRam(i);
        }
        public byte Peek(int index) => _nes.PeekSystemRam(index);
    }

    /// <summary>
    /// Swappable RAM domain that dereferences the active NES instance on each read.
    /// Use this when the host may replace the NES object (e.g., during LoadState).
    /// </summary>
    public sealed class NesRamDomainRef : IRamDomain
    {
        private readonly Func<NES?> _getNes;
        public NesRamDomainRef(Func<NES?> getNes) { _getNes = getNes; }
        public int Size => 2048;
        public void CopyRam(Span<byte> destination)
        {
            var nes = _getNes();
            int len = Math.Min(Size, destination.Length);
            if (nes == null)
            {
                // If NES missing, zero-fill to keep engine stable
                for (int i = 0; i < len; i++) destination[i] = 0;
                return;
            }
            for (int i = 0; i < len; i++) destination[i] = nes.PeekSystemRam(i);
        }
        public byte Peek(int index)
        {
            var nes = _getNes();
            return nes != null ? nes.PeekSystemRam(index) : (byte)0;
        }
    }

    // === Spec atoms ===

    public enum ComparisonOp { Eq, Ne, Lt, Le, Gt, Ge }

    /// <summary>Condition flag per finalspec (PauseIf, ResetIf, AndNext, etc.).</summary>
    public enum ConditionFlag
    {
        None,
        PauseIf,        // P:
        ResetIf,        // R:
        ResetNextIf,    // Z:
        AddSource,      // A: (parsed; not implemented in v1)
        SubSource,      // B: (parsed; not implemented in v1)
        AddHits,        // C:
        SubHits,        // D:
        AddAddress,     // I: (parsed; not implemented in v1)
        AndNext,        // N:
        OrNext,         // O:
        Measured,       // M:
        MeasuredPercent,// G:
        MeasuredIf,     // Q:
        Trigger,        // T:
        Remember        // K:
    }

    public enum MemoryPrefix
    {
        // Integer
        Bit0, Bit1, Bit2, Bit3, Bit4, Bit5, Bit6, Bit7, // 0xM..0xT
        LowerNibble, UpperNibble,                       // 0xL, 0xU
        U8, U16LE, U24LE, U32LE,                        // 0xH, 0x, 0xW, 0xX
        U16BE, U24BE, U32BE,                            // 0xI, 0xJ, 0xG
        BitCount,                                       // 0xK
        // Float (optional)
        F32LE, F32BE, Double32LE, Double32BE, MBF32Native, MBF32LE,
        // Special
        None
    }

    public enum ValueKind { Integer, Float }

    public enum OperandKind { Memory, Constant, Recall }

    public readonly struct Numeric
    {
        public readonly ValueKind Kind;
        public readonly long I64;
        public readonly double F64;
        public Numeric(long i)
        {
            Kind = ValueKind.Integer; I64 = i; F64 = default;
        }
        public Numeric(double f)
        {
            Kind = ValueKind.Float; F64 = f; I64 = default;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(Numeric a, Numeric b)
        {
            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
            {
                double da = a.Kind == ValueKind.Float ? a.F64 : a.I64;
                double db = b.Kind == ValueKind.Float ? b.F64 : b.I64;
                return da.CompareTo(db);
            }
            if (a.I64 == b.I64) return 0;
            return a.I64 < b.I64 ? -1 : 1;
        }
    }

    public sealed class MemoryRef
    {
        public MemoryPrefix Prefix;
        public int Address; // raw address in hex (RAM domain index for this engine)
        // Modifiers
        public bool UseDelta;
        public bool UsePrior;
        public bool UseBcd;
        public bool UseInvert;
        public ValueKind Kind => IsFloat(Prefix) ? ValueKind.Float : ValueKind.Integer;

        public static bool IsFloat(MemoryPrefix p)
        {
            return p == MemoryPrefix.F32LE || p == MemoryPrefix.F32BE
                || p == MemoryPrefix.Double32LE || p == MemoryPrefix.Double32BE
                || p == MemoryPrefix.MBF32Native || p == MemoryPrefix.MBF32LE;
        }
    }

    public sealed class Operand
    {
        public OperandKind Kind;
        public MemoryRef? Mem; // when Kind==Memory
        public Numeric Const;  // when Kind==Constant
        public static Operand FromConst(long v) => new Operand { Kind = OperandKind.Constant, Const = new Numeric(v) };
        public static Operand FromConst(double v) => new Operand { Kind = OperandKind.Constant, Const = new Numeric(v) };
        public static Operand Recall() => new Operand { Kind = OperandKind.Recall };
    }

    public sealed class Condition
    {
        public ConditionFlag Flag;
        public Operand Left = new Operand();
        public ComparisonOp Op;
        public Operand Right = new Operand();
        public int HitTarget; // 0 => no hitcount requirement
        // runtime state
        public int Hits;
        public bool IsMet; // updated each frame based on Hits/HitTarget
    }

    /// <summary>
    /// Achievement definition + runtime state. Single group only (alts not implemented in v1).
    /// </summary>
    public sealed class Achievement
    {
        public string Id = string.Empty;
        public string Formula = string.Empty;
        public List<Condition> Conditions = new();
        // runtime
        public bool Primed;      // all non-trigger met this frame
        public bool Unlocked;    // sticky once unlocked
        public Numeric? Remembered; // for K:/Recall
    // Measured tracking (for UI/leaderboards integration outside this engine)
    public double MeasuredCurrent;   // current progress value (0 if gated by Q:)
    public double MeasuredTarget;    // intended target (best-effort inference)
    public bool MeasuredIsPercent;   // true if any G: present in group
    public bool MeasuredActive;      // true if any M:/G: exists in group
    }

    /// <summary>
    /// Tiny, allocation-light per-frame evaluator. Maintains two prior RAM snapshots to support Delta/Prior.
    /// Call EvaluateFrame() once per emulated frame.
    /// </summary>
    public sealed class AchievementsEngine
    {
        private readonly IRamDomain _ram;
        private readonly byte[] _ramNow;
        private readonly byte[] _ramPrev; // previous frame
        private readonly byte[] _ramPrior; // two frames ago

        private readonly Dictionary<string, Achievement> _byId = new();

        public AchievementsEngine(IRamDomain ramDomain)
        {
            _ram = ramDomain;
            _ramNow = new byte[_ram.Size];
            _ramPrev = new byte[_ram.Size];
            _ramPrior = new byte[_ram.Size];
            // Initialize snapshots
            _ram.CopyRam(_ramNow);
            Array.Copy(_ramNow, _ramPrev, _ramNow.Length);
            Array.Copy(_ramNow, _ramPrior, _ramNow.Length);
        }

        public void Load(IEnumerable<(string id, string formula)> list)
        {
            foreach (var (id, formula) in list)
            {
                var ach = new Achievement { Id = id, Formula = formula };
                ach.Conditions = Parser.ParseConditions(formula);
                _byId[id] = ach;
            }
        }

        public Achievement? Get(string id) => _byId.TryGetValue(id, out var a) ? a : null;

        /// <summary>
        /// Evaluate all loaded achievements against current RAM. Returns IDs unlocked this frame.
        /// </summary>
        public List<string> EvaluateFrame()
        {
            // Shift snapshots: prior <= prev, prev <= now, now <= fresh
            Array.Copy(_ramPrev, _ramPrior, _ramPrev.Length);
            Array.Copy(_ramNow, _ramPrev, _ramNow.Length);
            _ram.CopyRam(_ramNow);

            var unlocked = new List<string>();
            foreach (var kv in _byId)
            {
                var ach = kv.Value;
                if (ach.Unlocked) continue;
                EvaluateAchievement(ach);
                if (ach.Unlocked) unlocked.Add(ach.Id);
            }
            return unlocked;
        }

        // ======== Snapshot/Restore API (resync-project MVP) ========
        public sealed class AchvNumber
        {
            public string Kind { get; set; } = "int"; // "int" or "float"
            public long I64 { get; set; }
            public double F64 { get; set; }
            public static AchvNumber FromNumeric(Numeric n)
                => n.Kind == ValueKind.Float ? new AchvNumber { Kind = "float", F64 = n.F64 } : new AchvNumber { Kind = "int", I64 = n.I64 };
            public Numeric ToNumeric()
                => string.Equals(Kind, "float", StringComparison.OrdinalIgnoreCase) ? new Numeric(F64) : new Numeric(I64);
        }
        public sealed class CondState { public int Hits { get; set; } public bool IsMet { get; set; } }
        public sealed class AchvAchState
        {
            public bool Primed { get; set; }
            public AchvNumber? Remembered { get; set; }
            public List<CondState> Conditions { get; set; } = new();
            public double MeasuredCurrent { get; set; }
            public double MeasuredTarget { get; set; }
            public bool MeasuredActive { get; set; }
            public bool MeasuredIsPercent { get; set; }
        }
        public sealed class AchvStateDTO
        {
            public string SchemaVersion { get; set; } = "achv-snap-v1";
            public List<string> CompletedIds { get; set; } = new();
            public Dictionary<string, AchvAchState> Progress { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public bool Hardcore { get; set; } // reserved for future
        }

        /// <summary>
        /// Serialize current achievement progress and completions. Intended to be paired with an emulator savestate.
        /// </summary>
        public AchvStateDTO SerializeState()
        {
            var dto = new AchvStateDTO();
            foreach (var kv in _byId)
            {
                var a = kv.Value;
                if (a.Unlocked)
                {
                    dto.CompletedIds.Add(a.Id);
                    continue;
                }
                var asd = new AchvAchState
                {
                    Primed = a.Primed,
                    Remembered = a.Remembered != null ? AchvNumber.FromNumeric(a.Remembered.Value) : null,
                    MeasuredCurrent = a.MeasuredCurrent,
                    MeasuredTarget = a.MeasuredTarget,
                    MeasuredActive = a.MeasuredActive,
                    MeasuredIsPercent = a.MeasuredIsPercent
                };
                foreach (var c in a.Conditions)
                    asd.Conditions.Add(new CondState { Hits = c.Hits, IsMet = c.IsMet });
                dto.Progress[a.Id] = asd;
            }
            return dto;
        }

        /// <summary>
        /// Restore a previously serialized achievement state. Unknown IDs are ignored.
        /// </summary>
        public void RestoreState(AchvStateDTO state)
        {
            // Reset to clean slate first
            foreach (var kv in _byId)
            {
                var a = kv.Value;
                a.Unlocked = false; a.Primed = false; a.Remembered = null;
                a.MeasuredActive = false; a.MeasuredCurrent = 0; a.MeasuredTarget = 0; a.MeasuredIsPercent = false;
                for (int i = 0; i < a.Conditions.Count; i++) { a.Conditions[i].Hits = 0; a.Conditions[i].IsMet = false; }
            }
            // Apply completions
            if (state.CompletedIds != null)
            {
                foreach (var id in state.CompletedIds)
                {
                    if (_byId.TryGetValue(id, out var a)) a.Unlocked = true;
                }
            }
            // Apply progress
            if (state.Progress != null)
            {
                foreach (var kv in state.Progress)
                {
                    if (!_byId.TryGetValue(kv.Key, out var a)) continue;
                    var asd = kv.Value;
                    a.Primed = asd.Primed;
                    a.Remembered = asd.Remembered != null ? asd.Remembered.ToNumeric() : (Numeric?)null;
                    a.MeasuredActive = asd.MeasuredActive; a.MeasuredCurrent = asd.MeasuredCurrent; a.MeasuredTarget = asd.MeasuredTarget; a.MeasuredIsPercent = asd.MeasuredIsPercent;
                    if (asd.Conditions != null)
                    {
                        for (int i = 0; i < a.Conditions.Count && i < asd.Conditions.Count; i++)
                        {
                            a.Conditions[i].Hits = asd.Conditions[i].Hits;
                            a.Conditions[i].IsMet = asd.Conditions[i].IsMet;
                        }
                    }
                }
            }
            // Re-initialize RAM snapshots to current to keep d/p semantics consistent
            try
            {
                _ram.CopyRam(_ramNow);
                Array.Copy(_ramNow, _ramPrev, _ramNow.Length);
                Array.Copy(_ramNow, _ramPrior, _ramNow.Length);
            }
            catch { }
        }

        /// <summary>
        /// Reset all achievements to power-on state (clear hits, met flags, primed, unlocked, remembered, measured).
        /// </summary>
        public void ResetToPowerOn()
        {
            foreach (var kv in _byId)
            {
                var a = kv.Value;
                a.Primed = false; a.Unlocked = false; a.Remembered = null;
                a.MeasuredActive = false; a.MeasuredCurrent = 0; a.MeasuredTarget = 0; a.MeasuredIsPercent = false;
                for (int i = 0; i < a.Conditions.Count; i++) { a.Conditions[i].Hits = 0; a.Conditions[i].IsMet = false; }
            }
            try
            {
                _ram.CopyRam(_ramNow);
                Array.Copy(_ramNow, _ramPrev, _ramNow.Length);
                Array.Copy(_ramNow, _ramPrior, _ramNow.Length);
            }
            catch { }
        }

        private void EvaluateAchievement(Achievement ach)
        {
            // 1) Pause check: if any PauseIf is true, suspend group (no hit updates, no resets)
            bool anyPause = false;
            for (int i = 0; i < ach.Conditions.Count; i++)
            {
                var c = ach.Conditions[i];
                if (c.Flag != ConditionFlag.PauseIf) continue;
                if (EvalRaw(c, ach, out _)) { anyPause = true; break; }
            }
            if (anyPause) { ach.Primed = false; return; }

            // 2) Stateful accumulators for AddSource/SubSource and AddAddress pointer bias (lightweight approximations)
            Numeric sourceSum = new Numeric(0);
            int addressBias = 0;

            // 3) Process ResetNextIf inline; evaluate others accumulating hits.
            // Track AndNext/OrNext chains: only last in chain gets hits applied.
            // Also compute if all non-trigger conditions are met this frame.
            bool allNonTriggerMet = true;
            bool anyMeasured = false;
            bool anyGPercent = false;
            double measuredValue = 0.0;
            double measuredTarget = 0.0;
            bool measuredTargetSet = false;
            bool measuredGateOk = true; // Q: gating (AND across group)
            // Pre-scan Q: gating
            for (int qi = 0; qi < ach.Conditions.Count; qi++)
            {
                var q = ach.Conditions[qi];
                if (q.Flag == ConditionFlag.MeasuredIf)
                {
                    bool qok = EvalRaw(q, ach, out _);
                    if (!qok) { measuredGateOk = false; break; }
                }
            }
            for (int i = 0; i < ach.Conditions.Count; i++)
            {
                var cond = ach.Conditions[i];

                if (cond.Flag == ConditionFlag.ResetNextIf)
                {
                    if (EvalRaw(cond, ach, out _))
                    {
                        if (i + 1 < ach.Conditions.Count) { ach.Conditions[i + 1].Hits = 0; ach.Conditions[i + 1].IsMet = false; }
                    }
                    continue; // ResetNextIf does not contribute to group met
                }
                if (cond.Flag == ConditionFlag.ResetIf)
                {
                    if (EvalRaw(cond, ach, out _))
                    {
                        // Reset entire achievement progress
                        ResetAchievement(ach);
                        ach.Primed = false;
                        return; // resets take effect immediately
                    }
                    continue; // ResetIf itself does not contribute to group met
                }

                // Arithmetic/address builder flags (do not directly contribute to met state)
                if (cond.Flag == ConditionFlag.AddSource || cond.Flag == ConditionFlag.SubSource || cond.Flag == ConditionFlag.AddAddress)
                {
                    // Evaluate left operand as numeric and adjust accumulators
                    var leftVal = EvalOperand(cond.Left, ach);
                    if (cond.Flag == ConditionFlag.AddSource)
                    {
                        // sourceSum += leftVal
                        if (leftVal.Kind == ValueKind.Float || sourceSum.Kind == ValueKind.Float)
                            sourceSum = new Numeric((sourceSum.Kind == ValueKind.Float ? sourceSum.F64 : sourceSum.I64) + (leftVal.Kind == ValueKind.Float ? leftVal.F64 : leftVal.I64));
                        else
                            sourceSum = new Numeric(sourceSum.I64 + leftVal.I64);
                    }
                    else if (cond.Flag == ConditionFlag.SubSource)
                    {
                        if (leftVal.Kind == ValueKind.Float || sourceSum.Kind == ValueKind.Float)
                            sourceSum = new Numeric((sourceSum.Kind == ValueKind.Float ? sourceSum.F64 : sourceSum.I64) - (leftVal.Kind == ValueKind.Float ? leftVal.F64 : leftVal.I64));
                        else
                            sourceSum = new Numeric(sourceSum.I64 - leftVal.I64);
                    }
                    else // AddAddress
                    {
                        // treat as integer bias
                        int delta = (int)(leftVal.Kind == ValueKind.Float ? leftVal.F64 : leftVal.I64);
                        addressBias += delta;
                    }
                    continue;
                }

                // Determine chain extent if this is start of a chain
                int chainStart = i;
                int chainEnd = i;
                bool chainOr = false;
                // Extend chain while current element has AndNext/OrNext flag
                int probe = i;
                while (probe < ach.Conditions.Count && (ach.Conditions[probe].Flag == ConditionFlag.AndNext || ach.Conditions[probe].Flag == ConditionFlag.OrNext))
                {
                    if (ach.Conditions[probe].Flag == ConditionFlag.OrNext) chainOr = true;
                    probe++;
                }
                chainEnd = probe; // last in chain has no N:/O: flag
                if (chainEnd >= ach.Conditions.Count) chainEnd = ach.Conditions.Count - 1;

                bool result;
                if (chainEnd > chainStart)
                {
                    // Evaluate each element of the chain individually so we can:
                    // - Attribute hitcounts to the condition that actually has a HitTarget (commonly the first)
                    // - Use the aggregate result only to gate final met state, not to accumulate hits over time
                    bool agg = chainOr ? false : true;
                    var raws = new bool[chainEnd - chainStart + 1];
                    for (int ci = chainStart; ci <= chainEnd; ci++)
                    {
                        bool r = EvalRaw(ach.Conditions[ci], ach, out _, sourceSum, addressBias);
                        raws[ci - chainStart] = r;
                        if (chainOr) agg |= r; else agg &= r;
                    }
                    result = agg;

                    // Pick target condition to receive hits: prefer the first with a HitTarget > 0, else the last
                    int targetIndex = chainEnd;
                    for (int ci = chainStart; ci <= chainEnd; ci++)
                    {
                        if (ach.Conditions[ci].HitTarget > 0) { targetIndex = ci; break; }
                    }
                    var target = ach.Conditions[targetIndex];
                    bool targetRaw = raws[targetIndex - chainStart];
                    ApplyHits(target, targetRaw);
                    // Met state for a hitcount-bearing target depends on hits; for 0-hit target, use sticky-isMet via hits as well
                    target.IsMet = IsConditionMet(target, targetRaw);

                    // For group met, consider only the target of the chain once
                    allNonTriggerMet &= (target.Flag == ConditionFlag.Trigger) ? true : target.IsMet;
                    i = chainEnd; // skip to end
                    // Reset accumulators after a completed compare chain
                    sourceSum = new Numeric(0);
                    addressBias = 0;
                }
                else
                {
                    // Single condition path
                    bool raw = EvalRaw(cond, ach, out var leftOut, sourceSum, addressBias);
                    // AddHits/SubHits explicitly tweak hit counters regardless of raw compare
                    if (cond.Flag == ConditionFlag.AddHits) { if (raw) cond.Hits++; }
                    else if (cond.Flag == ConditionFlag.SubHits) { if (raw && cond.Hits > 0) cond.Hits--; }
                    else { ApplyHits(cond, raw); }
                    cond.IsMet = IsConditionMet(cond, raw);

                    // K:Remember stores current left operand into achievement's Remembered slot
                    if (cond.Flag == ConditionFlag.Remember)
                    {
                        ach.Remembered = leftOut; // store last-evaluated value for Recall
                        continue; // Remember does not gate group met
                    }

                    if (cond.Flag != ConditionFlag.Trigger)
                        allNonTriggerMet &= cond.IsMet;
                    // Reset accumulators after a completed compare
                    if (cond.Flag != ConditionFlag.AddSource && cond.Flag != ConditionFlag.SubSource && cond.Flag != ConditionFlag.AddAddress)
                    {
                        sourceSum = new Numeric(0);
                        addressBias = 0;
                    }

                    // Measured handling (best-effort target inference)
                    if (cond.Flag == ConditionFlag.Measured || cond.Flag == ConditionFlag.MeasuredPercent)
                    {
                        anyMeasured = true; if (cond.Flag == ConditionFlag.MeasuredPercent) anyGPercent = true;
                        // Infer target: prefer HitTarget, else RHS const, else 1
                        double target = 1.0;
                        if (cond.HitTarget > 0) target = cond.HitTarget;
                        else if (cond.Right.Kind == OperandKind.Constant) target = (cond.Right.Const.Kind == ValueKind.Float) ? cond.Right.Const.F64 : cond.Right.Const.I64;
                        double current = (leftOut.Kind == ValueKind.Float) ? leftOut.F64 : leftOut.I64;
                        // Accumulate as max across multiple measured lines
                        if (!measuredTargetSet) { measuredTarget = target; measuredTargetSet = true; }
                        else measuredTarget = Math.Max(measuredTarget, target);
                        measuredValue = Math.Max(measuredValue, current);
                    }
                }
            }

            // 3) Prime logic
            ach.Primed = allNonTriggerMet;

            // 4) Unlock logic
            if (ach.Primed)
            {
                bool hasTriggers = false, allTriggersMet = true;
                for (int i = 0; i < ach.Conditions.Count; i++)
                {
                    var c = ach.Conditions[i];
                    if (c.Flag == ConditionFlag.Trigger)
                    {
                        hasTriggers = true;
                        bool raw = EvalRaw(c, ach, out _, sourceSum, addressBias);
                        ApplyHits(c, raw);
                        c.IsMet = IsConditionMet(c, raw);
                        allTriggersMet &= c.IsMet;
                        // Reset accumulators after trigger compare
                        sourceSum = new Numeric(0);
                        addressBias = 0;
                    }
                }
                if (!hasTriggers)
                {
                    ach.Unlocked = true; return;
                }
                if (allTriggersMet)
                {
                    ach.Unlocked = true; return;
                }
            }

            // 5) Measured publish
            ach.MeasuredActive = anyMeasured;
            ach.MeasuredIsPercent = anyGPercent;
            if (anyMeasured && measuredGateOk)
            {
                ach.MeasuredCurrent = measuredValue;
                ach.MeasuredTarget = measuredTarget > 0 ? measuredTarget : 1.0;
            }
            else
            {
                ach.MeasuredCurrent = 0;
                ach.MeasuredTarget = 0;
            }
        }

        private static void ResetAchievement(Achievement ach)
        {
            for (int j = 0; j < ach.Conditions.Count; j++) { ach.Conditions[j].Hits = 0; ach.Conditions[j].IsMet = false; }
            ach.Remembered = null;
        }

        private static int FindChainEnd(List<Condition> list, int start, out bool isOr)
        {
            isOr = list[start].Flag == ConditionFlag.OrNext;
            int i = start;
            while (i + 1 < list.Count)
            {
                var f = list[i].Flag;
                if (f != ConditionFlag.AndNext && f != ConditionFlag.OrNext) break;
                if (list[i + 1].Flag == ConditionFlag.AndNext || list[i + 1].Flag == ConditionFlag.OrNext)
                {
                    // chain continues
                }
                i++;
            }
            // Determine if chain is OR if any link is OrNext
            for (int k = start; k <= i; k++) if (list[k].Flag == ConditionFlag.OrNext) { isOr = true; break; }
            return i;
        }

    private bool EvalChain(Achievement ach, int start, int end, bool isOr, Numeric sourceSum, int addressBias)
        {
            bool agg = isOr ? false : true;
            for (int i = start; i <= end; i++)
            {
        bool raw = EvalRaw(ach.Conditions[i], ach, out _, sourceSum, addressBias);
                if (isOr) agg |= raw; else agg &= raw;
            }
            return agg;
        }

        private static void ApplyHits(Condition cond, bool rawTrue)
        {
            if (cond.HitTarget <= 0)
            {
                // Make 0-hit conditions sticky once satisfied. They can be reset by ResetIf/ResetNextIf elsewhere.
                if (rawTrue && cond.Hits == 0) cond.Hits = 1;
                return;
            }
            if (rawTrue)
            {
                if (cond.Hits < cond.HitTarget) cond.Hits++;
            }
        }

        private static bool IsConditionMet(Condition cond, bool rawTrue)
        {
            if (cond.HitTarget <= 0)
            {
                // Sticky: once hit at least once, it's considered met until reset
                return cond.Hits > 0;
            }
            return cond.Hits >= cond.HitTarget;
        }

        private bool EvalRaw(Condition cond, Achievement ach, out Numeric lastLeft)
            => EvalRaw(cond, ach, out lastLeft, new Numeric(0), 0);

        private bool EvalRaw(Condition cond, Achievement ach, out Numeric lastLeft, Numeric sourceSum, int addressBias)
        {
            // Evaluate operands
            var left = EvalOperand(cond.Left, ach, addressBias);
            var right = EvalOperand(cond.Right, ach, addressBias);
            // Apply source accumulator to left-hand value if applicable
            if (cond.Flag != ConditionFlag.AddSource && cond.Flag != ConditionFlag.SubSource && cond.Flag != ConditionFlag.AddAddress)
            {
                if (sourceSum.Kind == ValueKind.Float || left.Kind == ValueKind.Float)
                {
                    double lv = left.Kind == ValueKind.Float ? left.F64 : left.I64;
                    double sv = sourceSum.Kind == ValueKind.Float ? sourceSum.F64 : sourceSum.I64;
                    left = new Numeric(lv + sv);
                }
                else
                {
                    left = new Numeric(left.I64 + sourceSum.I64);
                }
            }
            lastLeft = left;

            // K:/Remember is handled by caller after EvalRaw when Flag==Remember
            // Q:/MeasuredIf gating has no effect on boolean compare; used for UI gating only in v1

            int cmp = Numeric.Compare(left, right);
            return cond.Op switch
            {
                ComparisonOp.Eq => cmp == 0,
                ComparisonOp.Ne => cmp != 0,
                ComparisonOp.Lt => cmp < 0,
                ComparisonOp.Le => cmp <= 0,
                ComparisonOp.Gt => cmp > 0,
                ComparisonOp.Ge => cmp >= 0,
                _ => false,
            };
        }

        private Numeric EvalOperand(Operand op, Achievement ach)
            => EvalOperand(op, ach, 0);

        private Numeric EvalOperand(Operand op, Achievement ach, int addressBias)
        {
            switch (op.Kind)
            {
                case OperandKind.Constant:
                    return op.Const;
                case OperandKind.Recall:
                    return ach.Remembered ?? new Numeric(0);
                case OperandKind.Memory:
                    var mr = op.Mem!;
                    if (addressBias != 0)
                    {
                        // Shallow copy with biased address to avoid mutating operand
                        var biased = new MemoryRef
                        {
                            Address = mr.Address + addressBias,
                            Prefix = mr.Prefix,
                            UseBcd = mr.UseBcd,
                            UseDelta = mr.UseDelta,
                            UseInvert = mr.UseInvert,
                            UsePrior = mr.UsePrior
                        };
                        return ReadMemoryNumeric(biased);
                    }
                    return ReadMemoryNumeric(mr);
                default:
                    return new Numeric(0);
            }
        }

        private Numeric ReadMemoryNumeric(MemoryRef mr)
        {
            // Delta semantics: dX returns Now - Prev for same address/prefix. Prior reads from two frames ago.
            if (MemoryRef.IsFloat(mr.Prefix))
            {
                if (mr.UseDelta)
                {
                    double now = ReadFloat(_ramNow, mr.Prefix, mr.Address);
                    double prev = ReadFloat(_ramPrev, mr.Prefix, mr.Address);
                    return new Numeric(now - prev);
                }
                double src = mr.UsePrior ? ReadFloat(_ramPrior, mr.Prefix, mr.Address) : ReadFloat(_ramNow, mr.Prefix, mr.Address);
                return new Numeric(src);
            }
            else
            {
                long val;
                if (mr.UseDelta)
                {
                    long now = ReadInt(_ramNow, mr.Prefix, mr.Address);
                    long prev = ReadInt(_ramPrev, mr.Prefix, mr.Address);
                    val = now - prev;
                }
                else
                {
                    byte[] src = mr.UsePrior ? _ramPrior : _ramNow;
                    val = ReadInt(src, mr.Prefix, mr.Address);
                }
                if (mr.UseBcd) val = BcdToInt(val);
                if (mr.UseInvert) val = ~val;
                return new Numeric(val);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ReadInt(byte[] src, MemoryPrefix p, int addr)
        {
            switch (p)
            {
                case MemoryPrefix.Bit0: return (src[addr & 0x7FF] & 0x01) != 0 ? 1 : 0;
                case MemoryPrefix.Bit1: return (src[addr & 0x7FF] & 0x02) != 0 ? 1 : 0;
                case MemoryPrefix.Bit2: return (src[addr & 0x7FF] & 0x04) != 0 ? 1 : 0;
                case MemoryPrefix.Bit3: return (src[addr & 0x7FF] & 0x08) != 0 ? 1 : 0;
                case MemoryPrefix.Bit4: return (src[addr & 0x7FF] & 0x10) != 0 ? 1 : 0;
                case MemoryPrefix.Bit5: return (src[addr & 0x7FF] & 0x20) != 0 ? 1 : 0;
                case MemoryPrefix.Bit6: return (src[addr & 0x7FF] & 0x40) != 0 ? 1 : 0;
                case MemoryPrefix.Bit7: return (src[addr & 0x7FF] & 0x80) != 0 ? 1 : 0;
                case MemoryPrefix.LowerNibble: return src[addr & 0x7FF] & 0x0F;
                case MemoryPrefix.UpperNibble: return (src[addr & 0x7FF] >> 4) & 0x0F;
                case MemoryPrefix.U8: return src[addr & 0x7FF];
                case MemoryPrefix.U16LE:
                    {
                        int i = addr & 0x7FF; int j = (i + 1) & 0x7FF;
                        return (uint)(src[i] | (src[j] << 8));
                    }
                case MemoryPrefix.U24LE:
                    {
                        int i = addr & 0x7FF; int j = (i + 1) & 0x7FF; int k = (i + 2) & 0x7FF;
                        return (uint)(src[i] | (src[j] << 8) | (src[k] << 16));
                    }
                case MemoryPrefix.U32LE:
                    {
                        int i = addr & 0x7FF; int j = (i + 1) & 0x7FF; int k = (i + 2) & 0x7FF; int m = (i + 3) & 0x7FF;
                        return (uint)(src[i] | (src[j] << 8) | (src[k] << 16) | (src[m] << 24));
                    }
                case MemoryPrefix.U16BE:
                    {
                        int i = addr & 0x7FF; int j = (i + 1) & 0x7FF;
                        return (uint)((src[i] << 8) | src[j]);
                    }
                case MemoryPrefix.U24BE:
                    {
                        int i = addr & 0x7FF; int j = (i + 1) & 0x7FF; int k = (i + 2) & 0x7FF;
                        return (uint)((src[i] << 16) | (src[j] << 8) | src[k]);
                    }
                case MemoryPrefix.U32BE:
                    {
                        int i = addr & 0x7FF; int j = (i + 1) & 0x7FF; int k = (i + 2) & 0x7FF; int m = (i + 3) & 0x7FF;
                        return (uint)((src[i] << 24) | (src[j] << 16) | (src[k] << 8) | src[m]);
                    }
                case MemoryPrefix.BitCount:
                    {
                        byte b = src[addr & 0x7FF];
                        b = (byte)(b - ((b >> 1) & 0x55));
                        b = (byte)((b & 0x33) + ((b >> 2) & 0x33));
                        return (byte)((b + (b >> 4)) & 0x0F);
                    }
                case MemoryPrefix.None:
                default:
                    // Treat as 16-bit LE by default per spec
                    goto case MemoryPrefix.U16LE;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ReadFloat(byte[] src, MemoryPrefix p, int addr)
        {
            uint u = (uint)ReadInt(src, p == MemoryPrefix.F32BE ? MemoryPrefix.U32BE : MemoryPrefix.U32LE, addr);
            unsafe
            {
                float f;
                u = p == MemoryPrefix.F32BE ? u : u; // no-op conversion; already in correct order above
                f = BitConverter.Int32BitsToSingle((int)u);
                return f;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long BcdToInt(long v)
        {
            // Interpret lower two nibbles as decimal (00-99). Simple & fast variant.
            long low = v & 0x0F;
            long high = (v >> 4) & 0x0F;
            return high * 10 + low;
        }
    }

    /// <summary>
    /// Very small, allocation-conscious parser for a compact RA-like formula string.
    /// Supported features: Flags P/R/Z/N/O/M/G/Q/T/K, hitcounts with (N), memory prefixes 0xM..0xT/L/U/H/W/X/I/J/G/K and fF/fB.
    /// Out-of-scope in v1: AddSource/SubSource/AddAddress arithmetic and alt groups.
    /// </summary>
    public static class Parser
    {
        // When true, the parser will log every step to Console (appears in browser console under WASM).
        public static bool DebugLogging = false;
    // Default FPS for seconds shorthand (e.g., "2S...") => hitcount in frames. NES NTSC is 60 FPS.
    public static int FramesPerSecond = 60;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Log(string msg)
        {
            if (DebugLogging)
                Console.WriteLine("[RA Parser] " + msg);
        }

        private static string DescribeOperand(Operand op)
        {
            return op.Kind switch
            {
                OperandKind.Constant => op.Const.Kind == ValueKind.Float ? $"Const(float)={op.Const.F64}" : $"Const(int)={op.Const.I64}",
                OperandKind.Recall => "Recall",
                OperandKind.Memory => DescribeMemory(op.Mem!),
                _ => "(unknown operand)"
            };
        }

        private static string DescribeMemory(MemoryRef mr)
        {
            string pf = mr.Prefix.ToString();
            string addr = $"0x{mr.Address:X}";
            string mods = string.Join("", new[] { mr.UseDelta ? "d" : "", mr.UsePrior ? "p" : "", mr.UseBcd ? "b" : "", mr.UseInvert ? "~" : "" });
            if (!string.IsNullOrEmpty(mods)) mods = $" mods={mods}";
            return $"Mem(prefix={pf}, addr={addr}{mods})";
        }

        public static List<Condition> ParseConditions(string formula)
        {
            var list = new List<Condition>(16);
            if (string.IsNullOrWhiteSpace(formula)) return list;

            Log($"Parse start: formula='{formula}'");

            var parts = new List<string>(formula.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries));
            Log($"Split into {parts.Count} token(s)");
            for (int i = 0; i < parts.Count; i++)
            {
                var token = parts[i].Trim();
                if (token.Length == 0) continue;
                Log($" Token[{i}] raw='{parts[i]}' trimmed='{token}'");
                var cond = new Condition();

                // Flag prefix (e.g., "R:")
                int idx = token.IndexOf(':');
                if (idx >= 1 && idx <= 2)
                {
                    cond.Flag = ParseFlag(token.AsSpan(0, idx));
                    token = token.Substring(idx + 1);
                    Log($"  Flag='{cond.Flag}' remaining='{token}'");
                }
                else cond.Flag = ConditionFlag.None;

                // Optional shorthand: leading "<seconds>S" converts to hitcount in frames
                // Example: "2Sd0xH0006=2" => hitcount = 2 * FramesPerSecond; token becomes "d0xH0006=2"
                int secondsFrames = 0;
                if (TryConsumeSecondsPrefix(ref token, out int frames))
                {
                    secondsFrames = frames;
                    Log($"  Seconds shorthand: {secondsFrames} frames remaining='{token}'");
                }

                // Embedded seconds on RHS: e.g., "0xH003f=2Sd0xH0006=2" => first condition gets hitcount from 2S,
                // extra tail becomes a new token appended after current index for parsing next.
                bool chainNext = false;
                if (TryExtractEmbeddedSecondsRhs(token, out string primary, out int rhsFrames, out string? extra, out bool nextIsChained))
                {
                    token = primary;
                    secondsFrames = Math.Max(secondsFrames, rhsFrames);
                    chainNext = nextIsChained;
                    if (!string.IsNullOrWhiteSpace(extra))
                    {
                        parts.Insert(i + 1, extra!);
                        Log($"  Extracted RHS seconds; queued extra token: '{extra}'");
                    }
                }

                // Hitcount suffix variant: trailing ".<digits>." (e.g., "...=1.3.")
                // Capture but do not yet apply (allow explicit forms like seconds or parentheses to override later)
                int dotSuffixHits = 0;
                TryConsumeTrailingDotHitcount(ref token, out dotSuffixHits);

                // Hitcount at end: "(...)"
                int hcStart = token.LastIndexOf('(');
                if (hcStart >= 0 && token.EndsWith(")", StringComparison.Ordinal))
                {
                    string inner = token.Substring(hcStart + 1, token.Length - hcStart - 2);
                    if (int.TryParse(inner.Trim(), out int n) && n > 0) cond.HitTarget = n;
                    token = token.Substring(0, hcStart);
                    Log($"  HitTarget={cond.HitTarget} remaining='{token}'");
                }
                // If no explicit hitcount, apply seconds-derived frames
                if (cond.HitTarget <= 0 && secondsFrames > 0) { cond.HitTarget = secondsFrames; Log($"  Applied seconds-derived HitTarget={cond.HitTarget}"); }
                // If still no hitcount, apply dot-suffix hits
                if (cond.HitTarget <= 0 && dotSuffixHits > 0) { cond.HitTarget = dotSuffixHits; Log($"  Applied dot-suffix HitTarget={cond.HitTarget}"); }
                // If embedded seconds produced a tail token, chain to next unless a flag already exists
                if (chainNext && cond.Flag == ConditionFlag.None)
                {
                    cond.Flag = ConditionFlag.AndNext;
                    Log("  Applied implicit AndNext due to embedded seconds with tail token");
                }

                // Split LHS op RHS
                SplitCompare(token, out string lhs, out ComparisonOp op, out string rhs);
                cond.Op = op;
                Log($"  Compare split: LHS='{lhs}' Op='{op}' RHS='{rhs}'");
                cond.Left = ParseOperand(lhs);
                Log($"   LHS parsed: {DescribeOperand(cond.Left)}");
                cond.Right = ParseOperand(rhs);
                Log($"   RHS parsed: {DescribeOperand(cond.Right)}");

                list.Add(cond);
            }
            Log($"Parse complete: {list.Count} condition(s)");
            return list;
        }

        // Detects pattern where RHS contains "<digits>S" immediately followed by the beginning of another condition
        // without an underscore, e.g., "LHS=2Sd0x...". Returns the primary token without the 'S' and seconds converted to frames,
        // and the tail as 'extraToken' to be parsed separately.
    private static bool TryExtractEmbeddedSecondsRhs(string token, out string primaryToken, out int frames, out string? extraToken, out bool chainNext)
        {
            primaryToken = token; frames = 0; extraToken = null; chainNext = false;
            if (string.IsNullOrEmpty(token)) return false;

            // Find comparison operator position
            int p;
            int opLen = 1;
            if ((p = token.IndexOf("!=", StringComparison.Ordinal)) > 0) opLen = 2;
            else if ((p = token.IndexOf(">=", StringComparison.Ordinal)) > 0) opLen = 2;
            else if ((p = token.IndexOf("<=", StringComparison.Ordinal)) > 0) opLen = 2;
            else if ((p = token.IndexOf('=', StringComparison.Ordinal)) > 0) opLen = 1;
            else if ((p = token.IndexOf('>', StringComparison.Ordinal)) > 0) opLen = 1;
            else if ((p = token.IndexOf('<', StringComparison.Ordinal)) > 0) opLen = 1;
            else return false;

            int i = p + opLen;
            // Skip whitespace
            while (i < token.Length && char.IsWhiteSpace(token[i])) i++;
            int startNum = i;
            // Only accept integer seconds for shorthand
            while (i < token.Length && char.IsDigit(token[i])) i++;
            if (i == startNum) return false; // no number
            if (i >= token.Length) return false;
            if (token[i] != 'S' && token[i] != 's') return false;

            // Parse seconds
            if (!int.TryParse(token.AsSpan(startNum, i - startNum), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                return false;
            if (seconds <= 0) return false;
            frames = seconds * Math.Max(1, FramesPerSecond);

            // Advance past 'S' and any whitespace to get tail
            i++;
            while (i < token.Length && char.IsWhiteSpace(token[i])) i++;
            if (i < token.Length)
            {
                string tail = token.Substring(i).Trim();
                // Apply seconds to the tail by prefixing it with leading seconds shorthand so it gets HitTarget
                extraToken = seconds.ToString(CultureInfo.InvariantCulture) + "S" + tail;
                // Primary token keeps the numeric value before 'S' but no hitcount applied here
                primaryToken = token.Substring(0, p + opLen) + token.Substring(startNum, (i - startNum) - 1);
                chainNext = true; frames = 0; // do not apply frames to primary when tail exists
            }
            else
            {
                // No tail; just strip the 'S'
                primaryToken = token.Substring(0, p + opLen) + token.Substring(startNum, (i - startNum) - 1);
                frames = seconds * Math.Max(1, FramesPerSecond); // apply to primary when no tail
            }
            primaryToken = primaryToken.Trim();
            return true;
        }

        // Consumes a leading seconds shorthand of the form "<digits>S" and returns frame count.
        // On success, updates 'token' to the remainder after the shorthand and returns true.
        private static bool TryConsumeSecondsPrefix(ref string token, out int frames)
        {
            frames = 0;
            if (string.IsNullOrEmpty(token)) return false;
            int i = 0;
            while (i < token.Length && char.IsWhiteSpace(token[i])) i++;
            int start = i;
            while (i < token.Length && char.IsDigit(token[i])) i++;
            if (i == start) return false; // no digits
            if (i < token.Length && (token[i] == 'S' || token[i] == 's'))
            {
                var numSpan = token.AsSpan(start, i - start);
                if (int.TryParse(numSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds > 0)
                {
                    frames = seconds * Math.Max(1, FramesPerSecond);
                    // consume trailing 'S' and optional whitespace
                    i++; while (i < token.Length && char.IsWhiteSpace(token[i])) i++;
                    token = token.Substring(i);
                    return true;
                }
            }
            return false;
        }

        private static void SplitCompare(string token, out string lhs, out ComparisonOp op, out string rhs)
        {
            // Try operators in order of two-char first
            int p;
            if ((p = token.IndexOf("!=", StringComparison.Ordinal)) > 0) { lhs = token[..p]; rhs = token[(p + 2)..]; op = ComparisonOp.Ne; Log($"   Operator '!=' at {p}"); return; }
            if ((p = token.IndexOf(">=", StringComparison.Ordinal)) > 0) { lhs = token[..p]; rhs = token[(p + 2)..]; op = ComparisonOp.Ge; Log($"   Operator '>=' at {p}"); return; }
            if ((p = token.IndexOf("<=", StringComparison.Ordinal)) > 0) { lhs = token[..p]; rhs = token[(p + 2)..]; op = ComparisonOp.Le; Log($"   Operator '<=' at {p}"); return; }
            if ((p = token.IndexOf('=', StringComparison.Ordinal)) > 0) { lhs = token[..p]; rhs = token[(p + 1)..]; op = ComparisonOp.Eq; Log($"   Operator '=' at {p}"); return; }
            if ((p = token.IndexOf('>', StringComparison.Ordinal)) > 0) { lhs = token[..p]; rhs = token[(p + 1)..]; op = ComparisonOp.Gt; Log($"   Operator '>' at {p}"); return; }
            if ((p = token.IndexOf('<', StringComparison.Ordinal)) > 0) { lhs = token[..p]; rhs = token[(p + 1)..]; op = ComparisonOp.Lt; Log($"   Operator '<' at {p}"); return; }
            // Fallback: whole token is a boolean (non-zero == true), compare to 1 implicitly
            lhs = token; rhs = "1"; op = ComparisonOp.Eq;
            Log($"   No operator found; treating '{token}' as boolean equality to 1");
        }

        // Consumes a trailing hitcount written as ".<digits>." at the end of the token.
        // On success, removes the suffix from 'token' and returns true with 'hits' set (>0).
        private static bool TryConsumeTrailingDotHitcount(ref string token, out int hits)
        {
            hits = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;
            string s = token.TrimEnd();
            if (s.Length < 3 || s[^1] != '.') return false;
            int firstDot = s.LastIndexOf('.', s.Length - 2);
            if (firstDot < 0) return false;
            string mid = s.Substring(firstDot + 1, s.Length - firstDot - 2);
            if (mid.Length == 0) return false;
            for (int i = 0; i < mid.Length; i++) { if (!char.IsDigit(mid[i])) return false; }
            if (!int.TryParse(mid, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0) return false;
            token = s.Substring(0, firstDot);
            hits = n;
            Log($"   Consumed trailing dot-hitcount '.{n}.' => new token='{token}'");
            return true;
        }

        private static Operand ParseOperand(string raw)
        {
            string s = raw.Trim();
            Log($"    ParseOperand raw='{raw}' trimmed='{s}'");
            if (s.Length == 0) return Operand.FromConst(0);

            // Recall token
            if (string.Equals(s, "recall", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "{recall}", StringComparison.OrdinalIgnoreCase))
            {
                Log("    -> Operand is Recall");
                return Operand.Recall();
            }

            // Constant: decimal or hex 0x...
            if (IsNumberLiteral(s))
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
                    {
                        Log($"    -> Const hex {hex} (0x{hex:X})");
                        return Operand.FromConst(hex);
                    }
                }
                else if (s.Contains('.') && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                {
                    Log($"    -> Const float {f}");
                    return Operand.FromConst(f);
                }
                else if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                {
                    Log($"    -> Const int {dec}");
                    return Operand.FromConst(dec);
                }
            }

            // Memory reference with optional modifiers d/p/b/~ and prefixes 0x... or fF/fB...
            var mr = new MemoryRef();

            // Modifiers
            while (s.Length > 0)
            {
                char c = s[0];
                if (c == 'd' || c == 'D') { mr.UseDelta = true; s = s.Substring(1); Log("    -> Modifier d (Delta)"); continue; }
                if (c == 'p' || c == 'P') { mr.UsePrior = true; s = s.Substring(1); Log("    -> Modifier p (Prior)"); continue; }
                if (c == 'b' || c == 'B') { mr.UseBcd = true; s = s.Substring(1); Log("    -> Modifier b (BCD)"); continue; }
                if (c == '~') { mr.UseInvert = true; s = s.Substring(1); Log("    -> Modifier ~ (Invert)"); continue; }
                break;
            }

            // Float prefixes: fF, fB, fH, fI, fM, fL
            if (s.Length >= 2 && (s[0] == 'f' || s[0] == 'F'))
            {
                string pf = s.Substring(0, 2);
                mr.Prefix = pf switch
                {
                    "fF" or "FF" => MemoryPrefix.F32LE,
                    "fB" or "FB" => MemoryPrefix.F32BE,
                    "fH" or "FH" => MemoryPrefix.Double32LE,
                    "fI" or "FI" => MemoryPrefix.Double32BE,
                    "fM" or "FM" => MemoryPrefix.MBF32Native,
                    "fL" or "FL" => MemoryPrefix.MBF32LE,
                    _ => MemoryPrefix.None
                };
                s = s.Substring(2);
                if (!TryParseHex(s, out int faddr)) faddr = 0;
                mr.Address = faddr;
                Log($"    -> Float {mr.Prefix} at 0x{mr.Address:X}");
                return new Operand { Kind = OperandKind.Memory, Mem = mr };
            }

            // 0x-prefixed memory tokens
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                // 0xH, 0xL, 0xU, 0xW, 0xX, 0xI, 0xJ, 0xG, 0xK, 0xM..0xT, or plain 0x (16-bit LE)
                if (s.Length >= 3)
                {
                    char k = s[2];
                    int addrStart = 2;
                    switch (char.ToUpperInvariant(k))
                    {
                        case 'H': mr.Prefix = MemoryPrefix.U8; addrStart = 3; break;
                        case 'L': mr.Prefix = MemoryPrefix.LowerNibble; addrStart = 3; break;
                        case 'U': mr.Prefix = MemoryPrefix.UpperNibble; addrStart = 3; break;
                        case 'W': mr.Prefix = MemoryPrefix.U24LE; addrStart = 3; break;
                        case 'X': mr.Prefix = MemoryPrefix.U32LE; addrStart = 3; break;
                        case 'I': mr.Prefix = MemoryPrefix.U16BE; addrStart = 3; break;
                        case 'J': mr.Prefix = MemoryPrefix.U24BE; addrStart = 3; break;
                        case 'G': mr.Prefix = MemoryPrefix.U32BE; addrStart = 3; break;
                        case 'K': mr.Prefix = MemoryPrefix.BitCount; addrStart = 3; break;
                        case 'M': mr.Prefix = MemoryPrefix.Bit0; addrStart = 3; break;
                        case 'N': mr.Prefix = MemoryPrefix.Bit1; addrStart = 3; break;
                        case 'O': mr.Prefix = MemoryPrefix.Bit2; addrStart = 3; break;
                        case 'P': mr.Prefix = MemoryPrefix.Bit3; addrStart = 3; break;
                        case 'Q': mr.Prefix = MemoryPrefix.Bit4; addrStart = 3; break;
                        case 'R': mr.Prefix = MemoryPrefix.Bit5; addrStart = 3; break;
                        case 'S': mr.Prefix = MemoryPrefix.Bit6; addrStart = 3; break;
                        case 'T': mr.Prefix = MemoryPrefix.Bit7; addrStart = 3; break;
                        default:
                            mr.Prefix = MemoryPrefix.U16LE; addrStart = 2; break; // plain 0x#### => 16-bit LE
                    }
                    string hex = s.Substring(addrStart);
                    if (!TryParseHex(hex, out int addr)) addr = 0;
                    mr.Address = addr;
                    Log($"    -> Mem {mr.Prefix} at 0x{mr.Address:X} (mods: d={(mr.UseDelta?1:0)}, p={(mr.UsePrior?1:0)}, b={(mr.UseBcd?1:0)}, ~= {(mr.UseInvert?1:0)})");
                    return new Operand { Kind = OperandKind.Memory, Mem = mr };
                }
            }

            // Fallback: treat as decimal constant if nothing matched
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ival)) return Operand.FromConst(ival);
            return Operand.FromConst(0);
        }

        private static bool IsNumberLiteral(string s)
        {
            if (s.Length == 0) return false;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true;
            bool dot = false; int i = 0; if (s[0] == '-' || s[0] == '+') i = 1;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '.') { if (dot) return false; dot = true; continue; }
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static bool TryParseHex(string s, out int value)
        {
            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static ConditionFlag ParseFlag(ReadOnlySpan<char> span)
        {
            if (span.Length == 0) return ConditionFlag.None;
            char c = span[0];
            var flag = char.ToUpperInvariant(c) switch
            {
                'P' => ConditionFlag.PauseIf,
                'R' => ConditionFlag.ResetIf,
                'Z' => ConditionFlag.ResetNextIf,
                'A' => ConditionFlag.AddSource,
                'B' => ConditionFlag.SubSource,
                'C' => ConditionFlag.AddHits,
                'D' => ConditionFlag.SubHits,
                'I' => ConditionFlag.AddAddress,
                'N' => ConditionFlag.AndNext,
                'O' => ConditionFlag.OrNext,
                'M' => ConditionFlag.Measured,
                'G' => ConditionFlag.MeasuredPercent,
                'Q' => ConditionFlag.MeasuredIf,
                'T' => ConditionFlag.Trigger,
                'K' => ConditionFlag.Remember,
                _ => ConditionFlag.None
            };
            Log($"   Parsed flag '{span.ToString()}' => {flag}");
            return flag;
        }
    }
}
