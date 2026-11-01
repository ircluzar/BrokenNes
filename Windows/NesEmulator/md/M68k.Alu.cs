// Arithmetic and logic helpers. Derived from ares' m68000 algorithms.cpp (ISC; see M68k.cs).

using System.Numerics;

namespace NesEmulator.Sega;

public sealed partial class M68k<TBus> where TBus : struct, IM68kBus
{
    private uint AddOp(int size, uint source, uint target, bool extend = false)
    {
        target = Clip(size, target);
        source = Clip(size, source);
        uint result = target + source + (extend && X ? 1u : 0u);
        uint carries = target ^ source ^ result;
        uint overflow = (target ^ result) & (source ^ result);
        uint msb = Msb(size);

        C = ((carries ^ overflow) & msb) != 0;
        V = (overflow & msb) != 0;
        Z = Clip(size, result) != 0 ? false : (extend ? Z : true);
        N = SignOf(size, result) < 0;
        X = C;
        return Clip(size, result);
    }

    private uint SubOp(int size, uint source, uint target, bool extend = false)
    {
        target = Clip(size, target);
        source = Clip(size, source);
        uint result = target - source - (extend && X ? 1u : 0u);
        uint carries = target ^ source ^ result;
        uint overflow = (target ^ result) & (source ^ target);
        uint msb = Msb(size);

        C = ((carries ^ overflow) & msb) != 0;
        V = (overflow & msb) != 0;
        Z = Clip(size, result) != 0 ? false : (extend ? Z : true);
        N = SignOf(size, result) < 0;
        X = C;
        return Clip(size, result);
    }

    private uint CmpOp(int size, uint source, uint target)
    {
        target = Clip(size, target);
        source = Clip(size, source);
        uint result = target - source;
        uint carries = target ^ source ^ result;
        uint overflow = (target ^ result) & (source ^ target);
        uint msb = Msb(size);

        C = ((carries ^ overflow) & msb) != 0;
        V = (overflow & msb) != 0;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        return Clip(size, result);
    }

    private uint AndOp(int size, uint source, uint target)
    {
        uint result = target & source;
        C = false; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        return Clip(size, result);
    }

    private uint OrOp(int size, uint source, uint target)
    {
        uint result = target | source;
        C = false; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        return Clip(size, result);
    }

    private uint EorOp(int size, uint source, uint target)
    {
        uint result = target ^ source;
        C = false; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        return Clip(size, result);
    }

    private uint AslOp(int size, uint result, int shift)
    {
        bool carry = false;
        uint overflow = 0;
        uint msb = Msb(size);
        for (int i = 0; i < shift; i++)
        {
            carry = (result & msb) != 0;
            uint before = result;
            result <<= 1;
            overflow |= before ^ result;
        }

        C = carry;
        V = SignOf(size, overflow) < 0;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        if (shift != 0) X = C;
        return Clip(size, result);
    }

    private uint AsrOp(int size, uint result, int shift)
    {
        bool carry = false;
        uint overflow = 0;
        for (int i = 0; i < shift; i++)
        {
            carry = (result & 1) != 0;
            uint before = result;
            result = (uint)(SignOf(size, result) >> 1);
            overflow |= before ^ result;
        }

        C = carry;
        V = SignOf(size, overflow) < 0;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        if (shift != 0) X = C;
        return Clip(size, result);
    }

    private uint LslOp(int size, uint result, int shift)
    {
        bool carry = false;
        uint msb = Msb(size);
        for (int i = 0; i < shift; i++)
        {
            carry = (result & msb) != 0;
            result <<= 1;
        }

        C = carry; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        if (shift != 0) X = C;
        return Clip(size, result);
    }

    private uint LsrOp(int size, uint result, int shift)
    {
        bool carry = false;
        for (int i = 0; i < shift; i++)
        {
            carry = (result & 1) != 0;
            result >>= 1;
        }

        C = carry; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        if (shift != 0) X = C;
        return Clip(size, result);
    }

    private uint RolOp(int size, uint result, int shift)
    {
        bool carry = false;
        uint msb = Msb(size);
        for (int i = 0; i < shift; i++)
        {
            carry = (result & msb) != 0;
            result = result << 1 | (carry ? 1u : 0u);
        }

        C = carry; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        return Clip(size, result);
    }

    private uint RorOp(int size, uint result, int shift)
    {
        bool carry = false;
        uint msb = Msb(size);
        for (int i = 0; i < shift; i++)
        {
            carry = (result & 1) != 0;
            result >>= 1;
            if (carry) result |= msb;
        }

        C = carry; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        return Clip(size, result);
    }

    private uint RoxlOp(int size, uint result, int shift)
    {
        bool carry = X;
        uint msb = Msb(size);
        for (int i = 0; i < shift; i++)
        {
            bool extend = carry;
            carry = (result & msb) != 0;
            result = result << 1 | (extend ? 1u : 0u);
        }

        C = carry; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        X = C;
        return Clip(size, result);
    }

    private uint RoxrOp(int size, uint result, int shift)
    {
        bool carry = X;
        uint msb = Msb(size);
        for (int i = 0; i < shift; i++)
        {
            bool extend = carry;
            carry = (result & 1) != 0;
            result >>= 1;
            if (extend) result |= msb;
        }

        C = carry; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
        X = C;
        return Clip(size, result);
    }

    /// <summary>The eight shift and rotate operations: <paramref name="kind"/> is 0 arithmetic, 1 logical, 2 rotate through extend, 3 rotate.</summary>
    private uint ShiftOp(int kind, bool left, int size, uint value, int count) => kind switch
    {
        0 => left ? AslOp(size, value, count) : AsrOp(size, value, count),
        1 => left ? LslOp(size, value, count) : LsrOp(size, value, count),
        2 => left ? RoxlOp(size, value, count) : RoxrOp(size, value, count),
        _ => left ? RolOp(size, value, count) : RorOp(size, value, count),
    };

    private bool Condition(int cc)
    {
        switch (cc)
        {
            case 0: return true;
            case 1: return false;
            case 2: return !C && !Z;
            case 3: return C || Z;
            case 4: return !C;
            case 5: return C;
            case 6: return !Z;
            case 7: return Z;
            case 8: return !V;
            case 9: return V;
            case 10: return !N;
            case 11: return N;
            case 12: return N == V;
            case 13: return N != V;
            case 14: return N == V && !Z;
            default: return N != V || Z;
        }
    }
}
