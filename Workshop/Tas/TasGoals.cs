// TAS workline T2 (docs/projects/tas-format.md "Goals"): chapter goals.
//   Memory goals: expressions over named variables from a per-game RAM map (UAT/tas/ramdb/<console>/<game>.json), peek(), frame, lag.
//   Picture goals: picture("ref.png"[, x, y, w, h[, tol]]) = the current frame's region matches the reference shot (fallback when no RAM map).
//   Goal lines: "EXPR" / "end: EXPR" (at the chapter end), "within N: EXPR" (true on some frame <= N), "reach: EXPR" (true on some frame;
//   reports the first), "always: EXPR" (true on every frame), and T1's "peek ADDR[:N] OP HEX".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BrokenNes.Workshop.Tas;

/// <summary>A per-game RAM map: name -> address/size/type (or an expression over other names).</summary>
public sealed class TasRamMap
{
    public sealed class Var { public string Name = "", Addr = "", Type = "u8", Expr = "", Notes = ""; public int Size = 1; }
    public readonly Dictionary<string, Var> Vars = new(StringComparer.OrdinalIgnoreCase);
    public string Path = "";

    public static TasRamMap Load(string path)
    {
        var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var m = new TasRamMap { Path = System.IO.Path.GetFullPath(path) };
        foreach (var kv in j["vars"]!.AsObject())
        {
            var v = new Var { Name = kv.Key };
            if (kv.Value is JsonValue s) v.Addr = (string)s!;
            else
            {
                var o = kv.Value!.AsObject();
                v.Addr = (string?)o["addr"] ?? ""; v.Expr = (string?)o["expr"] ?? ""; v.Type = (string?)o["type"] ?? "u8"; v.Notes = (string?)o["notes"] ?? "";
                v.Size = (int?)o["size"] ?? TypeSize(v.Type);
            }
            if (v.Addr.Length == 0 && v.Expr.Length == 0) throw new FormatException($"{path}: var {kv.Key} has no addr or expr");
            m.Vars[kv.Key] = v;
        }
        return m;
    }

    static int TypeSize(string t) => t switch { "u16" or "s16" or "u16le" or "s16le" or "u16be" or "s16be" => 2, "u32" or "s32" or "u32le" or "s32le" or "u32be" or "s32be" => 4, _ => 1 };

    /// <summary>The map for a run: the run's "ramdb" entry, BROKENNES_TAS_RAMDB, or UAT/tas/ramdb/&lt;console&gt;/&lt;game&gt;.json above the run's folder.</summary>
    public static TasRamMap? For(TasRun run)
    {
        if (run.Raw["ramdb"] is JsonNode r) return Load(run.Resolve((string)r!));
        if (run.Game.Length == 0) return null;
        var env = Environment.GetEnvironmentVariable("BROKENNES_TAS_RAMDB");
        if (!string.IsNullOrEmpty(env) && File.Exists(System.IO.Path.Combine(env, run.Console, run.Game + ".json"))) return Load(System.IO.Path.Combine(env, run.Console, run.Game + ".json"));
        for (var d = new DirectoryInfo(run.Dir); d != null; d = d.Parent)
        {
            var f = System.IO.Path.Combine(d.FullName, "UAT", "tas", "ramdb", run.Console, run.Game + ".json");
            if (File.Exists(f)) return Load(f);
            f = System.IO.Path.Combine(d.FullName, "ramdb", run.Console, run.Game + ".json");
            if (File.Exists(f)) return Load(f);
        }
        return null;
    }

    /// <summary>A variable's value now (types u8 s8 u16[le|be] s16.. u32.. bcd = packed BCD bytes, digits = one decimal digit per byte; little-endian by default).</summary>
    public long Read(ITasTarget t, Var v, Func<string, long> name)
    {
        if (v.Expr.Length > 0) return TasExpr.Parse(v.Expr).Eval(new TasEvalContext(t, this, null, 0, 0) { Outer = name });
        var b = t.Peek(v.Addr, v.Size);
        string ty = v.Type;
        if (ty == "digits") { long d = 0; foreach (var x in b) d = d * 10 + (x % 10); return d; }
        if (ty == "bcd") { long d = 0; foreach (var x in b) d = d * 100 + (x >> 4) * 10 + (x & 15); return d; }
        bool be = ty.EndsWith("be"); bool signed = ty.StartsWith("s");
        ulong u = 0;
        if (be) foreach (var x in b) u = u << 8 | x; else for (int i = b.Length - 1; i >= 0; i--) u = u << 8 | b[i];
        if (!signed) return (long)u;
        int bits = b.Length * 8; return bits >= 64 ? (long)u : (long)(u << (64 - bits)) >> (64 - bits);
    }

    public JsonObject Snapshot(ITasTarget t)
    {
        var o = new JsonObject();
        foreach (var v in Vars.Values) { try { o[v.Name] = Get(t, v.Name); } catch (Exception e) { o[v.Name] = "error: " + e.Message; } }
        return o;
    }

    public long Get(ITasTarget t, string name)
    {
        if (!Vars.TryGetValue(name, out var v)) throw new FormatException($"no variable '{name}' in {Path}");
        return Read(t, v, n => Get(t, n));
    }
}

public sealed class TasEvalContext
{
    public readonly ITasTarget T; public readonly TasRamMap? Map; public readonly string? Dir; public readonly int Frame, Lag;
    public Func<string, long>? Outer;
    public TasEvalContext(ITasTarget t, TasRamMap? map, string? dir, int frame, int lag) { T = t; Map = map; Dir = dir; Frame = frame; Lag = lag; }
    public readonly List<string> Notes = new();
}

/// <summary>An expression: or/and/not, == != &lt; &lt;= &gt; &gt;=, + - * / % &amp;, numbers ($hex, 0xhex, decimal), names, peek(ADDR[,N]) (big-endian),
/// peekle(ADDR,N), picture("ref.png"[,x,y,w,h[,tol]]), frame, lag. Booleans are 1/0.</summary>
public abstract class TasExpr
{
    public abstract long Eval(TasEvalContext c);

    public static TasExpr Parse(string text) { var p = new P(text); var e = p.Or(); p.Ws(); if (p.I < text.Length) throw new FormatException($"goal '{text}': unexpected '{text[p.I..]}'"); return e; }

    sealed class Num : TasExpr { public long V; public string Hex = ""; public override long Eval(TasEvalContext c) => V; }
    sealed class Str : TasExpr { public string V = ""; public override long Eval(TasEvalContext c) => throw new FormatException($"\"{V}\" is not a number"); }
    sealed class Name : TasExpr
    {
        public string N = "";
        public override long Eval(TasEvalContext c)
        {
            if (N == "frame") return c.Frame;
            if (N == "lag") return c.Lag;
            if (c.Outer != null && (c.Map == null || !c.Map.Vars.ContainsKey(N))) return c.Outer(N);
            if (c.Map == null) throw new FormatException($"'{N}': no RAM map for this game (UAT/tas/ramdb/<console>/<game>.json) - use peek(ADDR)");
            return c.Map.Get(c.T, N);
        }
    }
    sealed class Un : TasExpr { public string Op = ""; public TasExpr A = null!; public override long Eval(TasEvalContext c) => Op == "!" ? (A.Eval(c) == 0 ? 1 : 0) : -A.Eval(c); }
    sealed class Bin : TasExpr
    {
        public string Op = ""; public TasExpr A = null!, B = null!;
        public override long Eval(TasEvalContext c)
        {
            if (Op == "&&") return A.Eval(c) != 0 && B.Eval(c) != 0 ? 1 : 0;
            if (Op == "||") return A.Eval(c) != 0 || B.Eval(c) != 0 ? 1 : 0;
            long x = A.Eval(c), y = B.Eval(c);
            return Op switch
            {
                "+" => x + y, "-" => x - y, "*" => x * y, "/" => y == 0 ? 0 : x / y, "%" => y == 0 ? 0 : x % y, "&" => x & y,
                "==" => x == y ? 1 : 0, "!=" => x != y ? 1 : 0, "<" => x < y ? 1 : 0, "<=" => x <= y ? 1 : 0, ">" => x > y ? 1 : 0, ">=" => x >= y ? 1 : 0,
                _ => throw new FormatException("operator " + Op),
            };
        }
    }
    sealed class Call : TasExpr
    {
        public string F = ""; public List<TasExpr> A = new();
        string S(int i) => A[i] is Str s ? s.V : A[i] is Name n ? n.N : A[i] is Num u ? u.V.ToString("X") : throw new FormatException($"{F}: argument {i + 1}");
        public override long Eval(TasEvalContext c)
        {
            switch (F)
            {
                case "peek": case "peekle":
                {
                    // an address argument: a bare hex word (0770, 8033B21A) or $/0x hex
                    string ad = A[0] is Num nn ? nn.Hex : S(0);
                    int n = A.Count > 1 ? (int)A[1].Eval(c) : 1;
                    var b = c.T.Peek(ad, n); ulong u = 0;
                    if (F == "peek") foreach (var x in b) u = u << 8 | x; else for (int i = b.Length - 1; i >= 0; i--) u = u << 8 | b[i];
                    return (long)u;
                }
                case "picture":
                {
                    string f = S(0); var p = c.Dir != null && !System.IO.Path.IsPathRooted(f) ? System.IO.Path.Combine(c.Dir, f) : f;
                    int x = A.Count > 1 ? (int)A[1].Eval(c) : 0, y = A.Count > 2 ? (int)A[2].Eval(c) : 0;
                    int w = A.Count > 3 ? (int)A[3].Eval(c) : 0, h = A.Count > 4 ? (int)A[4].Eval(c) : 0;
                    double tol = A.Count > 5 ? A[5].Eval(c) / 1000.0 : 0.02;   // tol in per mille of the region's pixels
                    var (diff, n) = TasPicture.Compare(c.T, p, x, y, w, h);
                    c.Notes.Add($"picture({System.IO.Path.GetFileName(f)}) differs on {diff}/{n} px");
                    return n > 0 && diff <= tol * n ? 1 : 0;
                }
                case "abs": return Math.Abs(A[0].Eval(c));
                case "min": return Math.Min(A[0].Eval(c), A[1].Eval(c));
                case "max": return Math.Max(A[0].Eval(c), A[1].Eval(c));
            }
            throw new FormatException($"unknown function {F} (peek, peekle, picture, abs, min, max)");
        }
    }

    sealed class P
    {
        readonly string s; public int I;
        public P(string t) { s = t; }
        public void Ws() { while (I < s.Length && char.IsWhiteSpace(s[I])) I++; }
        bool Eat(string t) { Ws(); if (string.CompareOrdinal(s, I, t, 0, t.Length) == 0) { if (char.IsLetter(t[0]) && I + t.Length < s.Length && char.IsLetterOrDigit(s[I + t.Length])) return false; I += t.Length; return true; } return false; }
        public TasExpr Or() { var a = And(); while (Eat("||") || Eat("or")) a = new Bin { Op = "||", A = a, B = And() }; return a; }
        TasExpr And() { var a = Not(); while (Eat("&&") || Eat("and")) a = new Bin { Op = "&&", A = a, B = Not() }; return a; }
        TasExpr Not() { if (Eat("not") || (Peek('!') && !PeekAt(1, '=') && Eat("!"))) return new Un { Op = "!", A = Not() }; return Cmp(); }
        bool Peek(char ch) { Ws(); return I < s.Length && s[I] == ch; }
        bool PeekAt(int k, char ch) => I + k < s.Length && s[I + k] == ch;
        TasExpr Cmp()
        {
            var a = Sum();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" }) if (Eat(op)) return new Bin { Op = op, A = a, B = Sum() };
            return a;
        }
        TasExpr Sum() { var a = Prod(); while (true) { if (Eat("+")) a = new Bin { Op = "+", A = a, B = Prod() }; else if (Eat("-")) a = new Bin { Op = "-", A = a, B = Prod() }; else return a; } }
        TasExpr Prod()
        {
            var a = Unary();
            while (true)
            {
                Ws();
                if (I < s.Length && s[I] == '&' && !PeekAt(1, '&')) { I++; a = new Bin { Op = "&", A = a, B = Unary() }; }
                else if (Eat("*")) a = new Bin { Op = "*", A = a, B = Unary() };
                else if (Eat("/")) a = new Bin { Op = "/", A = a, B = Unary() };
                else if (Eat("%")) a = new Bin { Op = "%", A = a, B = Unary() };
                else return a;
            }
        }
        TasExpr Unary() { if (Eat("-")) return new Un { Op = "-", A = Unary() }; return Prim(); }
        TasExpr Prim()
        {
            Ws();
            if (I >= s.Length) throw new FormatException($"goal '{s}': ends early");
            if (Eat("(")) { var e = Or(); if (!Eat(")")) throw new FormatException($"goal '{s}': missing )"); return e; }
            char c = s[I];
            if (c == '"') { int j = s.IndexOf('"', I + 1); if (j < 0) throw new FormatException($"goal '{s}': open string"); var v = s[(I + 1)..j]; I = j + 1; return new Str { V = v }; }
            if (c == '$' || (c == '0' && I + 1 < s.Length && (s[I + 1] == 'x' || s[I + 1] == 'X')))
            {
                I += c == '$' ? 1 : 2; int st = I; while (I < s.Length && Uri.IsHexDigit(s[I])) I++;
                var h = s[st..I]; return new Num { V = long.Parse(h, NumberStyles.HexNumber), Hex = h };
            }
            if (char.IsDigit(c))
            {
                int st = I; while (I < s.Length && char.IsLetterOrDigit(s[I])) I++;
                var t = s[st..I];
                // a decimal number; a word with hex letters (8033B21A) only makes sense as a peek address
                return t.All(char.IsDigit) ? new Num { V = long.Parse(t), Hex = t } : new Num { V = long.Parse(t, NumberStyles.HexNumber), Hex = t };
            }
            if (char.IsLetter(c) || c == '_')
            {
                int st = I; while (I < s.Length && (char.IsLetterOrDigit(s[I]) || s[I] == '_' || s[I] == '.')) I++;
                var n = s[st..I];
                if (Eat("("))
                {
                    var call = new Call { F = n.ToLowerInvariant() };
                    if (!Eat(")")) { do call.A.Add(Or()); while (Eat(",")); if (!Eat(")")) throw new FormatException($"goal '{s}': missing ) after {n}("); }
                    return call;
                }
                return new Name { N = n };
            }
            throw new FormatException($"goal '{s}': unexpected '{c}'");
        }
    }
}

/// <summary>One goal line, compiled; tracks per-frame state during a chapter.</summary>
public sealed class TasGoal
{
    public string Text = "", Kind = "end"; public int Within = -1; public TasExpr? Expr;
    string? legacy;
    public bool? Met; public int FirstFrame = -1; public int FailedFrame = -1; public string Detail = "";

    public static TasGoal Parse(string line)
    {
        var g = new TasGoal { Text = line };
        var t = line.Trim();
        var x = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (x.Length == 4 && x[0] == "peek" && x[2] is "==" or "!=" or "<" or "<=" or ">" or ">=") { g.legacy = t; return g; }
        int colon = t.IndexOf(':');
        if (colon > 0 && !t[..colon].Contains('(') && !t[..colon].Contains('"'))
        {
            var head = t[..colon].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (head[0])
            {
                case "end": g.Kind = "end"; break;
                case "reach": g.Kind = "reach"; break;
                case "always": g.Kind = "always"; break;
                case "within": g.Kind = "within"; g.Within = int.Parse(head[1]); break;
                default: throw new FormatException($"goal '{line}': '{head[0]}:' (end: within N: reach: always:)");
            }
            t = t[(colon + 1)..];
        }
        g.Expr = TasExpr.Parse(t);
        return g;
    }

    public bool PerFrame => Kind != "end" && legacy == null;

    public void Reset() { Met = null; FirstFrame = -1; FailedFrame = -1; Detail = ""; }

    long Eval(TasEvalContext c)
    {
        if (legacy == null) return Expr!.Eval(c);
        var x = legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ad = x[1].Split(':'); int n = ad.Length > 1 ? int.Parse(ad[1]) : 1;
        ulong v = 0; foreach (var b in c.T.Peek(ad[0], n)) v = v << 8 | b;
        ulong w = Convert.ToUInt64(x[3].TrimStart('$').Replace("0x", ""), 16);
        c.Notes.Add($"{v:X}");
        return x[2] switch { "==" => v == w, "!=" => v != w, "<" => v < w, "<=" => v <= w, ">" => v > w, _ => v >= w } ? 1 : 0;
    }

    /// <summary>After each frame (per-frame kinds only).</summary>
    public void OnFrame(TasEvalContext c)
    {
        if (!PerFrame) return;
        if (Kind == "always") { if (FailedFrame < 0 && Eval(c) == 0) FailedFrame = c.Frame; return; }
        if (FirstFrame >= 0) return;
        if (Kind == "within" && c.Frame > Within) return;
        if (Eval(c) != 0) FirstFrame = c.Frame;
    }

    public bool Finish(TasEvalContext c)
    {
        switch (Kind)
        {
            case "always": Met = FailedFrame < 0; Detail = Met.Value ? "held on every frame" : $"false at frame {FailedFrame}"; break;
            case "reach": case "within":
                Met = FirstFrame >= 0; Detail = Met.Value ? $"first true at frame {FirstFrame}" : Kind == "within" ? $"not true by frame {Within}" : "never true"; break;
            default:
            {
                bool ok = Eval(c) != 0; Met = ok; if (ok) FirstFrame = c.Frame;
                Detail = (ok ? "met at the end" : "NOT MET at the end") + (c.Notes.Count > 0 ? " (" + string.Join("; ", c.Notes) + ")" : ""); break;
            }
        }
        return Met.Value;
    }

    public JsonObject ToJson() => new() { ["goal"] = Text, ["kind"] = Kind, ["met"] = Met, ["firstFrame"] = FirstFrame >= 0 ? FirstFrame : null, ["detail"] = Detail };
}

/// <summary>Picture goals: compare a region of the current frame with the same region of a reference PNG.</summary>
public static class TasPicture
{
    static readonly Dictionary<string, (uint[] px, int w, int h)> cache = new();

    /// <summary>(pixels differing by more than 24 on a channel, pixels compared). w/h 0 = to the edge.</summary>
    public static (int diff, int n) Compare(ITasTarget t, string refPng, int x, int y, int w, int h)
    {
        if (!cache.TryGetValue(refPng, out var r)) cache[refPng] = r = Png.Read(refPng);
        var px = t.Argb; int fw = t.Width, fh = t.Height;
        if (w <= 0) w = Math.Min(fw, r.w) - x; if (h <= 0) h = Math.Min(fh, r.h) - y;
        if (x < 0 || y < 0 || x + w > fw || y + h > fh || x + w > r.w || y + h > r.h) return (int.MaxValue, 0);
        int diff = 0;
        for (int j = y; j < y + h; j++)
            for (int i = x; i < x + w; i++)
            {
                uint a = px[j * fw + i], b = r.px[j * r.w + i];
                if (Math.Abs((int)(a >> 16 & 255) - (int)(b >> 16 & 255)) > 24 || Math.Abs((int)(a >> 8 & 255) - (int)(b >> 8 & 255)) > 24 || Math.Abs((int)(a & 255) - (int)(b & 255)) > 24) diff++;
            }
        return (diff, w * h);
    }
}

/// <summary>A small PNG reader (8-bit RGB / RGBA / grey, non-interlaced: what TasPng and most tools write).</summary>
public static class Png
{
    public static (uint[] px, int w, int h) Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 8 || b[0] != 0x89 || b[1] != 'P') throw new InvalidDataException($"{path}: not a PNG");
        int pos = 8, w = 0, h = 0, ct = 0, depth = 0; var idat = new MemoryStream(); byte[]? plte = null;
        while (pos + 8 <= b.Length)
        {
            int len = b[pos] << 24 | b[pos + 1] << 16 | b[pos + 2] << 8 | b[pos + 3]; string type = System.Text.Encoding.ASCII.GetString(b, pos + 4, 4);
            int d = pos + 8;
            if (type == "IHDR") { w = BE(b, d); h = BE(b, d + 4); depth = b[d + 8]; ct = b[d + 9]; if (b[d + 12] != 0) throw new InvalidDataException($"{path}: interlaced PNG"); }
            else if (type == "PLTE") plte = b[d..(d + len)];
            else if (type == "IDAT") idat.Write(b, d, len);
            else if (type == "IEND") break;
            pos = d + len + 4;
        }
        if (depth != 8) throw new InvalidDataException($"{path}: {depth}-bit PNG (8-bit only)");
        int bpp = ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"{path}: colour type {ct}") };
        idat.Position = 0; using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw); var data = raw.ToArray();
        int stride = w * bpp; var prev = new byte[stride]; var cur = new byte[stride]; var px = new uint[w * h]; int o = 0;
        for (int y = 0; y < h; y++)
        {
            int f = data[o++]; Buffer.BlockCopy(data, o, cur, 0, stride); o += stride;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, up = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int v = f switch { 1 => a, 2 => up, 3 => (a + up) >> 1, 4 => Paeth(a, up, c), _ => 0 };
                cur[i] = (byte)(cur[i] + v);
            }
            for (int x = 0; x < w; x++)
            {
                int k = x * bpp;
                px[y * w + x] = ct switch
                {
                    0 or 4 => 0xFF000000u | (uint)cur[k] * 0x010101u,
                    3 => 0xFF000000u | (uint)plte![cur[k] * 3] << 16 | (uint)plte[cur[k] * 3 + 1] << 8 | plte[cur[k] * 3 + 2],
                    _ => 0xFF000000u | (uint)cur[k] << 16 | (uint)cur[k + 1] << 8 | cur[k + 2],
                };
            }
            (prev, cur) = (cur, prev);
        }
        return (px, w, h);
    }
    static int BE(byte[] b, int o) => b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];
    static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
}
