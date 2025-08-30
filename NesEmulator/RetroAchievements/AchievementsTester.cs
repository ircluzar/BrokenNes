using System;
using System.Collections.Generic;
using System.Globalization;

namespace NesEmulator.RetroAchievements
{
    /// <summary>
    /// Instruction-driven tester that parses RA-like formulas and generates memory edits
    /// to force achievements to unlock by writing values directly into NES System RAM.
    /// It reuses the existing Parser to interpret conditions, then creates a step plan
    /// across frames to satisfy flags, hitcounts, and simple delta-based patterns.
    /// </summary>
    public sealed class AchievementsTester
    {
        // Public API ---------------------------------------------------------

        /// <summary>
        /// Build a plan of writes/frames to satisfy the given formula.
        /// </summary>
        public static TestPlan BuildPlan(string formula)
        {
            var conditions = Parser.ParseConditions(formula);
            var planner = new Planner(conditions);
            return planner.Build();
        }

        /// <summary>
        /// Execute a plan against a NES instance. Optionally, provide an AchievementsEngine
        /// to verify unlock and short-circuit early when unlocked (achId is used for check).
        /// </summary>
        public static bool ExecutePlan(NES nes, TestPlan plan, AchievementsEngine? engine = null, string? achId = null)
        {
            return new Executor(nes, engine, achId).Run(plan);
        }

        // Data structures ---------------------------------------------------

        /// <summary>
        /// A single byte write to System RAM with optional bitmask semantics.
        /// If Mask == 0xFF, Value is written directly; otherwise, newByte = (old & ~Mask) | (Value & Mask).
        /// Address is the raw RAM index (0..2047); it will be masked with 0x7FF when executed.
        /// </summary>
        public readonly struct ByteWrite
        {
            public readonly int Address; // RAM index (pre-masked)
            public readonly byte Mask;
            public readonly byte Value;
            public ByteWrite(int address, byte value, byte mask = 0xFF)
            { Address = address; Value = value; Mask = mask; }
            public override string ToString() => $"@{Address & 0x7FF:X3}: {(Mask == 0xFF ? Value.ToString("X2") : ($"mask={Mask:X2} val={Value:X2}"))}";
        }

        /// <summary>
        /// A step represents all writes to apply in one frame. After applying writes, the executor will advance one frame.
        /// </summary>
        public sealed class Step
        {
            public List<ByteWrite> Writes { get; } = new();
            public string Note { get; set; } = string.Empty; // optional for debugging/tracing
        }

        /// <summary>
        /// Full plan of steps to execute, in order. Each step applies its writes, then advances 1 frame.
        /// </summary>
        public sealed class TestPlan
        {
            public List<Step> Steps { get; } = new();
            public override string ToString() => $"TestPlan(Steps={Steps.Count})";
        }

        // Planner: converts parsed conditions into a TestPlan ----------------
        private sealed class Planner
        {
            private readonly List<Condition> _conds;
            public Planner(List<Condition> conditions) { _conds = conditions; }

            public TestPlan Build()
            {
                // Strategy:
                // 1) Neutralize disruptive flags by making them false (PauseIf/ResetIf/ResetNextIf).
                // 2) Choose an option for OrNext chains (prefer simplest), require all for AndNext chains.
                // 3) For non-delta conditions with hitcounts, hold the satisfying value for N frames.
                // 4) For simple delta patterns like X > dX (N), create N increasing writes across frames.
                // 5) Aggregate writes per frame so multiple conditions can be satisfied concurrently.

                var plan = new TestPlan();

                // Normalize: expand chains into sets we must satisfy.
                var required = ExpandChains(_conds);

                // Prepend a step to neutralize PAUSE/RESET flags by forcing them false.
                var pre = new Step { Note = "Neutralize P:/R:/Z: flags" };
                foreach (var c in _conds)
                {
                    if (c.Flag == ConditionFlag.PauseIf || c.Flag == ConditionFlag.ResetIf || c.Flag == ConditionFlag.ResetNextIf)
                    {
                        // Make the compare evaluate false.
                        foreach (var bw in WritesToMakeCompare(c, desiredTrue: false)) pre.Writes.Add(bw);
                    }
                }
                if (pre.Writes.Count > 0) plan.Steps.Add(pre);

                // Build satisfying writes for required set.
                // Detect simple delta pattern groups to spread across frames; others can be in a single step followed by repeats.
                var deltaGroups = new List<(Condition Cond, int Frames)>();
                int holdFrames = 1; // minimum 1 frame
                foreach (var c in required)
                {
                    if (IsSimpleDeltaIncrease(c, out _))
                    {
                        int frames = Math.Max(1, c.HitTarget);
                        deltaGroups.Add((c, frames));
                    }
                    else
                    {
                        holdFrames = Math.Max(holdFrames, Math.Max(1, c.HitTarget));
                    }
                }

                // Initial step: set all non-delta conditions to their satisfying values.
                var step0 = new Step { Note = "Satisfy non-delta conditions" };
                foreach (var c in required)
                {
                    if (IsSimpleDeltaIncrease(c, out _)) continue;
                    foreach (var bw in WritesToMakeCompare(c, desiredTrue: true)) step0.Writes.Add(bw);
                }
                plan.Steps.Add(step0);

                // For delta groups: create frame-by-frame increments.
                if (deltaGroups.Count > 0)
                {
                    // For each frame k, write increments for each delta group.
                    int maxFrames = 0; foreach (var g in deltaGroups) maxFrames = Math.Max(maxFrames, g.Frames);
                    for (int frame = 0; frame < maxFrames; frame++)
                    {
                        var s = new Step { Note = frame == 0 ? "Delta kickstart" : $"Delta step {frame + 1}" };
                        foreach (var g in deltaGroups)
                        {
                            if (frame >= g.Frames) continue; // done for this condition
                            foreach (var bw in WritesForSimpleDeltaIncrease(g.Cond, stepIndex: frame)) s.Writes.Add(bw);
                        }
                        plan.Steps.Add(s);
                    }
                }

                // Additional hold frames to satisfy hitcounts for static conditions.
                for (int i = 1; i < holdFrames; i++)
                {
                    plan.Steps.Add(new Step { Note = $"Hold static values ({i + 1}/{holdFrames})" });
                }

                // Final step: attempt to satisfy triggers as well (usually already covered by required set).
                var triggers = new Step { Note = "Ensure triggers true" };
                foreach (var c in _conds)
                {
                    if (c.Flag == ConditionFlag.Trigger)
                    {
                        foreach (var bw in WritesToMakeCompare(c, desiredTrue: true)) triggers.Writes.Add(bw);
                    }
                }
                if (triggers.Writes.Count > 0) plan.Steps.Add(triggers);

                return plan;
            }

            // Expand AndNext/OrNext chains into the set of conditions we intend to satisfy.
            private static List<Condition> ExpandChains(List<Condition> src)
            {
                var result = new List<Condition>();
                int i = 0;
                while (i < src.Count)
                {
                    var c = src[i];
                    if (c.Flag == ConditionFlag.OrNext)
                    {
                        // Collect the entire OR chain (one or more items until a non-chain item).
                        var orList = new List<Condition>();
                        int j = i;
                        while (j < src.Count)
                        {
                            orList.Add(src[j]);
                            // last item in chain has flag != OrNext/AndNext
                            if (j + 1 >= src.Count) break;
                            var f = src[j + 1].Flag;
                            if (f != ConditionFlag.OrNext) { j++; break; }
                            j++;
                        }
                        // Choose simplest option in the OR chain to satisfy (prefer constant RHS equals)
                        var pick = ChooseSimplest(orList);
                        result.Add(pick);
                        i = j + 1; continue;
                    }
                    else if (c.Flag == ConditionFlag.AndNext)
                    {
                        // Collect the entire AND chain; all must be satisfied.
                        int j = i;
                        while (j < src.Count)
                        {
                            result.Add(src[j]);
                            if (j + 1 >= src.Count) break;
                            if (src[j + 1].Flag != ConditionFlag.AndNext) { j++; break; }
                            j++;
                        }
                        i = j + 1; continue;
                    }
                    else
                    {
                        result.Add(c); i++;
                    }
                }
                return result;
            }

            private static Condition ChooseSimplest(List<Condition> candidates)
            {
                // Priority: memory vs constant, Eq preferred over others, non-delta preferred.
                Condition? best = null; int bestScore = int.MinValue;
                foreach (var c in candidates)
                {
                    int score = 0;
                    if (c.Left.Kind == OperandKind.Memory && c.Right.Kind == OperandKind.Constant) score += 5;
                    if (c.Op == ComparisonOp.Eq) score += 2;
                    if (c.Left.Kind == OperandKind.Memory && c.Left.Mem != null && !c.Left.Mem.UseDelta && !c.Left.Mem.UsePrior) score += 1;
                    if (c.Right.Kind == OperandKind.Memory && c.Right.Mem != null && (c.Right.Mem.UseDelta || c.Right.Mem.UsePrior)) score -= 1;
                    if (best == null || score > bestScore) { bestScore = score; best = c; }
                }
                return best ?? candidates[0];
            }

            private static bool IsSimpleDeltaIncrease(Condition c, out MemoryRef? mem)
            {
                mem = null;
                if (c.Left.Kind == OperandKind.Memory && c.Right.Kind == OperandKind.Memory)
                {
                    var L = c.Left.Mem!; var R = c.Right.Mem!;
                    if (!L.UseDelta && !L.UsePrior && (R.UseDelta || R.UsePrior)
                        && L.Prefix == R.Prefix && L.Address == R.Address
                        && (c.Op == ComparisonOp.Gt || c.Op == ComparisonOp.Ne || c.Op == ComparisonOp.Ge))
                    {
                        mem = L; return true;
                    }
                }
                return false;
            }

            private static IEnumerable<ByteWrite> WritesForSimpleDeltaIncrease(Condition c, int stepIndex)
            {
                // Increase the value by +1 per frame starting from an arbitrary baseline (we'll use 1+stepIndex).
                // For multi-byte, write least significant byte only; for 16-bit, we bump LSB and keep others minimal.
                var mr = c.Left.Mem!;
                int baseAddr = mr.Address & 0x7FF;
                switch (mr.Prefix)
                {
                    case MemoryPrefix.U8:
                    case MemoryPrefix.LowerNibble:
                    case MemoryPrefix.UpperNibble:
                    case MemoryPrefix.Bit0:
                    case MemoryPrefix.Bit1:
                    case MemoryPrefix.Bit2:
                    case MemoryPrefix.Bit3:
                    case MemoryPrefix.Bit4:
                    case MemoryPrefix.Bit5:
                    case MemoryPrefix.Bit6:
                    case MemoryPrefix.Bit7:
                    case MemoryPrefix.BitCount:
                        yield return new ByteWrite(baseAddr, (byte)(1 + stepIndex));
                        break;
                    default:
                        // Treat as 16-bit LE default for other integer prefixes
                        yield return new ByteWrite(baseAddr + 0, (byte)(1 + stepIndex));
                        yield return new ByteWrite((baseAddr + 1) & 0x7FF, 0x00);
                        break;
                }
            }

            private static IEnumerable<ByteWrite> WritesToMakeCompare(Condition c, bool desiredTrue)
            {
                // Only supports writing to the left memory operand when available; if left is not memory, but right is, try symmetric cases.
                if (c.Left.Kind == OperandKind.Memory)
                {
                    foreach (var w in WritesForLeftMemoryCompare(c, desiredTrue)) yield return w;
                    yield break;
                }
                if (c.Right.Kind == OperandKind.Memory)
                {
                    // Flip operator: A op Mem  ==> Mem flip(op) A
                    var flipped = FlipForRightMemory(c);
                    foreach (var w in WritesForLeftMemoryCompare(flipped, desiredTrue)) yield return w;
                    yield break;
                }
                // No writable operand: nothing to do.
                yield break;
            }

            private static Condition FlipForRightMemory(Condition c)
            {
                var op = c.Op switch
                {
                    ComparisonOp.Eq => ComparisonOp.Eq,
                    ComparisonOp.Ne => ComparisonOp.Ne,
                    ComparisonOp.Lt => ComparisonOp.Gt,
                    ComparisonOp.Le => ComparisonOp.Ge,
                    ComparisonOp.Gt => ComparisonOp.Lt,
                    ComparisonOp.Ge => ComparisonOp.Le,
                    _ => c.Op
                };
                return new Condition { Flag = c.Flag, Left = c.Right, Right = c.Left, Op = op, HitTarget = c.HitTarget };
            }

            private static IEnumerable<ByteWrite> WritesForLeftMemoryCompare(Condition c, bool desiredTrue)
            {
                var mr = c.Left.Mem!;
                int addr = mr.Address & 0x7FF;
                // Determine target value range for the left operand to make the compare evaluate to desiredTrue.
                // Prefer a single minimal integer value when possible.
                // Resolve RHS constant where possible.
                bool rhsIsConst = (c.Right.Kind == OperandKind.Constant);
                long rhsV = rhsIsConst ? (c.Right.Const.Kind == ValueKind.Float ? (long)c.Right.Const.F64 : c.Right.Const.I64) : 0;

                // If RHS is memory or non-constant, pick a simple baseline: 1 (or 0 for some ops).
                if (!rhsIsConst)
                {
                    rhsV = 1;
                }

                // For bit-style prefixes, we manipulate specific bits/nibbles.
                switch (mr.Prefix)
                {
                    case MemoryPrefix.Bit0:
                    case MemoryPrefix.Bit1:
                    case MemoryPrefix.Bit2:
                    case MemoryPrefix.Bit3:
                    case MemoryPrefix.Bit4:
                    case MemoryPrefix.Bit5:
                    case MemoryPrefix.Bit6:
                    case MemoryPrefix.Bit7:
                        {
                            int bit = (int)mr.Prefix - (int)MemoryPrefix.Bit0; // 0..7
                            byte bitMask = (byte)(1 << bit);
                            bool wantSet = SolveCompareForBit(desiredTrue, c.Op, rhsV);
                            byte val = wantSet ? bitMask : (byte)0x00;
                            yield return new ByteWrite(addr, val, mask: bitMask);
                            yield break;
                        }
                    case MemoryPrefix.LowerNibble:
                    case MemoryPrefix.UpperNibble:
                        {
                            bool upper = mr.Prefix == MemoryPrefix.UpperNibble;
                            byte nibbleMask = upper ? (byte)0xF0 : (byte)0x0F;
                            byte shift = upper ? (byte)4 : (byte)0;
                            byte tgt = SolveByteTarget(c.Op, rhsV, desiredTrue, width: 4);
                            yield return new ByteWrite(addr, (byte)(tgt << shift), mask: nibbleMask);
                            yield break;
                        }
                    case MemoryPrefix.U8:
                        {
                            byte tgt = SolveByteTarget(c.Op, rhsV, desiredTrue, width: 8);
                            yield return new ByteWrite(addr, tgt);
                            yield break;
                        }
                    default:
                        {
                            // Treat as 16-bit LE integer for all other integer cases.
                            ushort tgt = SolveWordTarget(c.Op, rhsV, desiredTrue);
                            yield return new ByteWrite(addr, (byte)(tgt & 0xFF));
                            yield return new ByteWrite((addr + 1) & 0x7FF, (byte)(tgt >> 8));
                            yield break;
                        }
                }
            }

            private static bool SolveCompareForBit(bool desiredTrue, ComparisonOp op, long rhs)
            {
                // Bit reads as 0 or 1. Derive whether we need the bit set.
                // For simplicity, compare to RHS (clamped to 0/1) and pick set/clear to achieve desired outcome.
                int r = (rhs != 0) ? 1 : 0;
                // Evaluate truth table for L op R as a function of L in {0,1}. Pick L to match desiredTrue.
                bool When(int L)
                {
                    return op switch
                    {
                        ComparisonOp.Eq => L == r,
                        ComparisonOp.Ne => L != r,
                        ComparisonOp.Gt => L > r,
                        ComparisonOp.Ge => L >= r,
                        ComparisonOp.Lt => L < r,
                        ComparisonOp.Le => L <= r,
                        _ => false,
                    };
                }
                bool setTrue = When(1);
                bool clrTrue = When(0);
                if (desiredTrue)
                {
                    // Prefer setting the bit when both satisfy, else whichever yields true.
                    if (setTrue) return true; if (clrTrue) return false; return true;
                }
                else
                {
                    // Prefer clearing the bit when both yield false; else whichever yields false.
                    if (!clrTrue) return false; if (!setTrue) return true; return false;
                }
            }

            private static byte SolveByteTarget(ComparisonOp op, long rhs, bool desiredTrue, int width)
            {
                int max = (1 << width) - 1;
                long r = Clamp(rhs, 0, max);
                long v;
                if (desiredTrue)
                {
                    v = op switch
                    {
                        ComparisonOp.Eq => r,
                        ComparisonOp.Ne => (r == 0 ? 1 : 0),
                        ComparisonOp.Gt => Math.Min(r + 1, max),
                        ComparisonOp.Ge => r,
                        ComparisonOp.Lt => Math.Max(r - 1, 0),
                        ComparisonOp.Le => r,
                        _ => r
                    };
                }
                else
                {
                    v = op switch
                    {
                        ComparisonOp.Eq => (r == 0 ? 1 : 0),
                        ComparisonOp.Ne => r,
                        ComparisonOp.Gt => r,
                        ComparisonOp.Ge => Math.Max(r - 1, 0),
                        ComparisonOp.Lt => r,
                        ComparisonOp.Le => Math.Min(r + 1, max),
                        _ => r
                    };
                }
                return (byte)Clamp(v, 0, max);
            }

            private static ushort SolveWordTarget(ComparisonOp op, long rhs, bool desiredTrue)
            {
                long r = Clamp(rhs, 0, 0xFFFF);
                long v;
                if (desiredTrue)
                {
                    v = op switch
                    {
                        ComparisonOp.Eq => r,
                        ComparisonOp.Ne => (r == 0 ? 1 : 0),
                        ComparisonOp.Gt => Math.Min(r + 1, 0xFFFF),
                        ComparisonOp.Ge => r,
                        ComparisonOp.Lt => Math.Max(r - 1, 0),
                        ComparisonOp.Le => r,
                        _ => r
                    };
                }
                else
                {
                    v = op switch
                    {
                        ComparisonOp.Eq => (r == 0 ? 1 : 0),
                        ComparisonOp.Ne => r,
                        ComparisonOp.Gt => r,
                        ComparisonOp.Ge => Math.Max(r - 1, 0),
                        ComparisonOp.Lt => r,
                        ComparisonOp.Le => Math.Min(r + 1, 0xFFFF),
                        _ => r
                    };
                }
                return (ushort)Clamp(v, 0, 0xFFFF);
            }

            private static long Clamp(long v, long min, long max) => v < min ? min : (v > max ? max : v);
        }

        // Executor: applies steps to NES and optionally verifies unlock via engine -----
        private sealed class Executor
        {
            private readonly NES _nes;
            private readonly AchievementsEngine? _engine;
            private readonly string? _achId;
            public Executor(NES nes, AchievementsEngine? engine, string? achId)
            { _nes = nes; _engine = engine; _achId = achId; }

            public bool Run(TestPlan plan)
            {
                bool unlocked = false;
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    var step = plan.Steps[i];
                    Apply(step);
                    // Advance a frame so hitcounts/deltas progress
                    _nes.RunFrame();
                    if (_engine != null && !string.IsNullOrEmpty(_achId))
                    {
                        var ids = _engine.EvaluateFrame();
                        foreach (var id in ids) if (id == _achId) { unlocked = true; break; }
                        if (unlocked) return true;
                    }
                }
                // If engine not provided, return true to indicate the plan executed fully.
                return unlocked || _engine == null;
            }

            private void Apply(Step s)
            {
                foreach (var w in s.Writes)
                {
                    int idx = w.Address & 0x7FF;
                    byte oldVal = _nes.PeekSystemRam(idx);
                    byte newVal = (w.Mask == 0xFF) ? w.Value : (byte)((oldVal & ~w.Mask) | (w.Value & w.Mask));
                    _nes.PokeSystemRam(idx, newVal);
                }
            }
        }
    }
}
