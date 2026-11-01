using System;
using System.Collections.Generic;
using System.Globalization;

namespace NesEmulator.Debugging;

/// <summary>What an expression can see while it runs: the access that triggered it, and the machine.</summary>
public sealed class ExprContext
{
    /// <summary>The byte read or written (0 for an execute breakpoint).</summary>
    public long Value;
    /// <summary>The CPU address accessed (the PC for an execute breakpoint).</summary>
    public long Address;
    /// <summary>Who accessed it: 0 the CPU, 1 DMA, 2 HDMA, 3 OAM DMA, 4 VRAM DMA (<see cref="AccessSource"/>).</summary>
    public long Source;
}

/// <summary>What the compiler resolves names against (one CPU of one machine).</summary>
public interface IExprEnvironment
{
    /// <summary>A register of the CPU (A, X, Y, S, P, PC, ...; case-insensitive).</summary>
    bool TryRegister(string name, out Func<long> read);
    /// <summary>frame, scanline, dot, cycle.</summary>
    bool TryMachineValue(string name, out Func<long> read);
    /// <summary>A symbol: its address and, when it is not a CPU address, the space it lives in.</summary>
    bool TrySymbol(string name, out long address, out int space, out string? error);
    /// <summary>A memory space by name (wram, vram, prg, ...).</summary>
    bool TrySpace(string name, out int space);
    byte ReadCpu(long address);
    byte ReadSpace(int space, long offset);
}

/// <summary>
/// Breakpoint conditions and watch expressions, compiled once into closures: <c>A == 3 &amp;&amp; [player_hp] &lt; 2</c>.
/// Numbers: <c>$1F</c> / <c>0x1F</c> hex, <c>%101</c> binary, plain decimal. <c>[x]</c> reads a byte, <c>{x}</c> a little-endian word;
/// <c>[wram:$10]</c> reads a named space; a symbol inside brackets reads the space the symbol lives in. Names: the CPU's registers,
/// <c>value</c> and <c>address</c> (the access), <c>frame</c>, <c>scanline</c>, <c>dot</c>, <c>cycle</c>, then symbols.
/// Operators (C precedence): unary - ! ~, * / %, + -, &lt;&lt; &gt;&gt;, &lt; &lt;= &gt; &gt;=, == !=, &amp;, ^, |, &amp;&amp;, ||.
/// </summary>
public sealed class DebugExpr
{
    private readonly Func<ExprContext, long> eval;
    public string Text { get; }
    private DebugExpr(string text, Func<ExprContext, long> eval) { Text = text; this.eval = eval; }

    public long Evaluate(ExprContext ctx) => eval(ctx);
    public bool IsTrue(ExprContext ctx) => eval(ctx) != 0;

    public static DebugExpr Compile(string text, IExprEnvironment env)
    {
        var p = new Parser(text, env);
        var node = p.ParseExpression();
        p.SkipSpace();
        if (!p.AtEnd) throw p.Error("unexpected text");
        return new DebugExpr(text, node.Eval);
    }

    /// <summary>A compiled sub-expression; <see cref="Space"/> is set when its value is an address in a named space (a symbol's).</summary>
    private sealed class Node
    {
        public Func<ExprContext, long> Eval = null!;
        public int Space = -1;
        public static Node Of(Func<ExprContext, long> f, int space = -1) => new() { Eval = f, Space = space };
    }

    private sealed class Parser
    {
        private readonly string s;
        private readonly IExprEnvironment env;
        private int i;
        public Parser(string s, IExprEnvironment env) { this.s = s; this.env = env; }
        public bool AtEnd => i >= s.Length;

        public FormatException Error(string what) => new($"{what} at column {i + 1} of '{s}'");

        public void SkipSpace() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private bool Eat(string tok)
        {
            SkipSpace();
            if (string.CompareOrdinal(s, i, tok, 0, tok.Length) != 0) return false;
            // don't take '<' out of '<<' or '<=', '&' out of '&&', '|' out of '||', '=' alone
            if (tok.Length == 1 && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if ((tok == "<" || tok == ">") && (n == tok[0] || n == '=')) return false;
                if ((tok == "&" || tok == "|") && n == tok[0]) return false;
                if (tok == "!" && n == '=') return false;
            }
            i += tok.Length;
            return true;
        }

        public Node ParseExpression() => Binary(0);

        private static readonly string[][] Levels =
        {
            new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" },
            new[] { "==", "!=" }, new[] { "<=", ">=", "<", ">" }, new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "%" },
        };

        private Node Binary(int level)
        {
            if (level == Levels.Length) return Unary();
            var left = Binary(level + 1);
            while (true)
            {
                string? op = null;
                foreach (var t in Levels[level]) if (Eat(t)) { op = t; break; }
                if (op == null) return left;
                var right = Binary(level + 1);
                var l = left.Eval; var r = right.Eval;
                int space = (op is "+" or "-") ? (left.Space >= 0 ? left.Space : right.Space) : -1;
                left = Node.Of(op switch
                {
                    "||" => c => l(c) != 0 || r(c) != 0 ? 1 : 0,
                    "&&" => c => l(c) != 0 && r(c) != 0 ? 1 : 0,
                    "|" => c => l(c) | r(c),
                    "^" => c => l(c) ^ r(c),
                    "&" => c => l(c) & r(c),
                    "==" => c => l(c) == r(c) ? 1 : 0,
                    "!=" => c => l(c) != r(c) ? 1 : 0,
                    "<" => c => l(c) < r(c) ? 1 : 0,
                    "<=" => c => l(c) <= r(c) ? 1 : 0,
                    ">" => c => l(c) > r(c) ? 1 : 0,
                    ">=" => c => l(c) >= r(c) ? 1 : 0,
                    "<<" => c => l(c) << (int)r(c),
                    ">>" => c => l(c) >> (int)r(c),
                    "+" => c => l(c) + r(c),
                    "-" => c => l(c) - r(c),
                    "*" => c => l(c) * r(c),
                    "/" => c => { long d = r(c); return d == 0 ? 0 : l(c) / d; },
                    _ => c => { long d = r(c); return d == 0 ? 0 : l(c) % d; },
                }, space);
            }
        }

        private Node Unary()
        {
            if (Eat("-")) { var n = Unary().Eval; return Node.Of(c => -n(c)); }
            if (Eat("!")) { var n = Unary().Eval; return Node.Of(c => n(c) == 0 ? 1 : 0); }
            if (Eat("~")) { var n = Unary().Eval; return Node.Of(c => ~n(c)); }
            return Primary();
        }

        private Node Primary()
        {
            SkipSpace();
            if (AtEnd) throw Error("expected a value");
            char ch = s[i];
            if (ch == '(') { i++; var n = ParseExpression(); if (!Eat(")")) throw Error("expected ')'"); return n; }
            if (ch == '[' || ch == '{')
            {
                bool word = ch == '{';
                i++;
                int space = -1;
                // an optional "space:" prefix
                int save = i;
                SkipSpace();
                string? id = PeekIdentifier(out int idEnd);
                if (id != null && idEnd < s.Length && s[idEnd] == ':' && env.TrySpace(id, out int sp)) { space = sp; i = idEnd + 1; }
                else i = save;
                var addr = ParseExpression();
                if (!Eat(word ? "}" : "]")) throw Error(word ? "expected '}'" : "expected ']'");
                if (space < 0) space = addr.Space;
                var a = addr.Eval;
                var e = env;
                if (space >= 0)
                {
                    int sp2 = space;
                    return word ? Node.Of(c => { long x = a(c); return e.ReadSpace(sp2, x) | e.ReadSpace(sp2, x + 1) << 8; })
                                : Node.Of(c => e.ReadSpace(sp2, a(c)));
                }
                return word ? Node.Of(c => { long x = a(c); return e.ReadCpu(x) | e.ReadCpu(x + 1) << 8; })
                            : Node.Of(c => e.ReadCpu(a(c)));
            }
            if (ch == '$' || ch == '%' || char.IsDigit(ch)) { long v = Number(); return Node.Of(_ => v); }
            if (ch == '\'' && i + 2 < s.Length && s[i + 2] == '\'') { long v = s[i + 1]; i += 3; return Node.Of(_ => v); }
            string? name = PeekIdentifier(out int end);
            if (name == null) throw Error($"unexpected '{ch}'");
            i = end;
            string lower = name.ToLowerInvariant();
            if (lower == "value") return Node.Of(c => c.Value);
            if (lower == "address") return Node.Of(c => c.Address);
            if (lower == "source") return Node.Of(c => c.Source);
            if (env.TryRegister(name, out var reg)) return Node.Of(_ => reg());
            if (env.TryMachineValue(lower, out var mv)) return Node.Of(_ => mv());
            if (env.TrySymbol(name, out long symAddr, out int symSpace, out string? err)) return Node.Of(_ => symAddr, symSpace);
            throw new FormatException(err ?? $"unknown name '{name}' in '{s}' (not a register, value, address, frame, scanline, dot, cycle or a loaded symbol)");
        }

        private string? PeekIdentifier(out int end)
        {
            int j = i;
            if (j >= s.Length || !(char.IsLetter(s[j]) || s[j] == '_' || s[j] == '.' || s[j] == '@')) { end = j; return null; }
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '.' or '@')) j++;
            end = j;
            return s.Substring(i, j - i);
        }

        private long Number()
        {
            int start = i;
            try
            {
                if (s[i] == '$') { i++; return ParseRun(16); }
                if (s[i] == '%') { i++; return ParseRun(2); }
                if (s[i] == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X')) { i += 2; return ParseRun(16); }
                return ParseRun(10);
            }
            catch (FormatException) { i = start; throw Error("bad number"); }
        }

        private long ParseRun(int radix)
        {
            int start = i;
            while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
            string digits = s.Substring(start, i - start);
            if (digits.Length == 0) throw new FormatException();
            return radix == 16 ? long.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : Convert.ToInt64(digits, radix);
        }
    }

    /// <summary>Parses an address or range as written in a breakpoint spec: <c>$8000</c>, <c>$7E0010-$7E001F</c>, <c>0x10</c>, <c>16</c>.</summary>
    public static bool TryParseNumber(string text, out long value)
    {
        text = text.Trim();
        value = 0;
        try
        {
            if (text.StartsWith("$")) { value = long.Parse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture); return true; }
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { value = long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture); return true; }
            if (text.StartsWith("%")) { value = Convert.ToInt64(text[1..], 2); return true; }
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        catch (Exception) { return false; }
    }
}
