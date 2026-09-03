using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Headless "does this ROM actually run?" smoke test - the CI-style gate that was previously only
/// possible by hand-writing a throwaway console project against the linked cores (which is exactly
/// how the mapper-30 UNROM-512 homebrew was validated; see project_mapper30 memory). Same shape as
/// the other CLIs here: dispatched from Program.cs on argv[0], no WinForms, no audio device.
///
/// Usage:
///   --romtest --rom &lt;path.nes&gt; [--cpu FIX] [--ppu FMC] [--apu FMC] [--frames N]
///             [--input "60:Right,90:Right+A,120:"] [--json] [--out result.json]
///
/// Exit codes (chosen to match BenchmarkCli so a script can treat them uniformly):
///   0 ran clean | 1 emulator crashed | 2 usage/IO error | 3 a requested core did not apply
///   4 unsupported mapper | 5 unexpected exception
/// </summary>
internal static class RomTestCli
{
    private const string Usage =
        "Usage: --romtest --rom <path.nes> [--cpu ID] [--ppu ID] [--apu ID] [--frames N]\n" +
        "                 [--input \"frame:Buttons,frame:Buttons,...\"] [--json] [--out result.json]\n" +
        "  --input script: comma-separated frame:buttons steps, buttons joined by '+'.\n" +
        "                  Empty button list releases everything. Held set applies to player 1\n" +
        "                  from that frame until the next step. Buttons: A,B,Select,Start,Up,Down,Left,Right.\n" +
        "                  Example: \"60:Right,90:Right+A,120:\"";

    public static int Run(string[] args)
    {
        EnsureConsole(); // do this first so usage/IO errors are visible too, not just the report

        string? romPath = null, cpu = null, ppu = null, apu = null, outPath = null, inputScript = null;
        int frames = 600;
        bool json = false;

        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom": romPath = args[++i]; break;
                    case "--cpu": cpu = args[++i]; break;
                    case "--ppu": ppu = args[++i]; break;
                    case "--apu": apu = args[++i]; break;
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--input": inputScript = args[++i]; break;
                    case "--out": outPath = args[++i]; break;
                    case "--json": json = true; break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}\n{Usage}");
                        return 2;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException)
        {
            Console.Error.WriteLine($"Bad arguments: {ex.Message}\n{Usage}");
            return 2;
        }

        if (romPath == null) { Console.Error.WriteLine(Usage); return 2; }
        if (frames < 0) { Console.Error.WriteLine("--frames must be >= 0"); return 2; }

        List<InputStep> script;
        try { script = ParseInputScript(inputScript); }
        catch (FormatException fe) { Console.Error.WriteLine($"Bad --input script: {fe.Message}\n{Usage}"); return 2; }

        byte[] romBytes;
        try { romBytes = File.ReadAllBytes(romPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read ROM: {ex.Message}"); return 2; }

        try
        {
            var nes = new NES { RomName = Path.GetFileName(romPath) };
            nes.LoadROM(romBytes);

            // "Applied" is deliberately two questions, not one: SetXCore() returning true only says
            // the id resolved, so we also read the live core back. A silently-ignored core swap is
            // precisely the failure this tool exists to catch (the FIX-lock check).
            var cpuApply = ApplyCore("CPU", cpu, nes.SetCpuCore, nes.GetCpuCoreId);
            var ppuApply = ApplyCore("PPU", ppu, nes.SetPpuCore, nes.GetPpuCoreId);
            var apuApply = ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId);

            var report = new RomTestReport
            {
                Rom = Path.GetFileName(romPath),
                RomBytes = romBytes.Length,
                RequestedFrames = frames,
                Cpu = cpuApply, Ppu = ppuApply, Apu = apuApply,
                InputSteps = script.Select(s => new InputStepReport
                {
                    Frame = s.Frame,
                    Buttons = s.Names.Count == 0 ? "(release all)" : string.Join("+", s.Names),
                }).ToList(),
            };

            if (!cpuApply.Applied || !ppuApply.Applied || !apuApply.Applied)
            {
                report.Result = "core-not-applied";
                Emit(report, json, outPath);
                return 3;
            }

            if (nes.IsCrashed())
            {
                report.Result = "crashed";
                report.CrashFrame = -1; // crashed before a single frame was stepped
                report.CrashInfo = nes.GetCrashInfo();
                Emit(report, json, outPath);
                return 1;
            }

            var held = new bool[8];
            int nextStep = 0;
            int framesRun = 0;
            int crashFrame = -1;
            for (int f = 0; f < frames; f++)
            {
                // Steps are pre-sorted; a while (not if) so several steps on the same frame - or a
                // step landing on a frame we happen to be at - all resolve before this frame runs.
                while (nextStep < script.Count && script[nextStep].Frame <= f)
                {
                    held = (bool[])script[nextStep].Held.Clone();
                    nextStep++;
                }
                nes.SetInputs(held, null);

                nes.RunFrame();
                framesRun++;
                if (nes.IsCrashed()) { crashFrame = f; break; }
            }

            report.FramesRun = framesRun;

            var fb = nes.GetFrameBuffer();
            report.FrameBufferBytes = fb.Length;
            report.FrameBufferHash = Fnv1a64(fb);
            report.DistinctColors = CountDistinctColors(fb);

            if (crashFrame >= 0)
            {
                report.Result = "crashed";
                report.CrashFrame = crashFrame;
                report.CrashInfo = nes.GetCrashInfo();
                Emit(report, json, outPath);
                return 1;
            }

            report.Result = "ok";
            Emit(report, json, outPath);
            return 0;
        }
        catch (Cartridge.UnsupportedMapperException ume)
        {
            Console.Error.WriteLine($"Unsupported mapper {ume.MapperId} ({ume.MapperName}).");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }
    }

    // ---- core application ----------------------------------------------------

    // internal, not private: TraceCli reuses this verbatim so --trace and --romtest agree on what
    // "the requested core actually applied" means. A second copy would be free to drift.
    internal static CoreApplyReport ApplyCore(string kind, string? requested, Func<string, bool> set, Func<string> get)
    {
        if (requested == null)
            return new CoreApplyReport { Kind = kind, Requested = null, Accepted = true, Effective = get(), Applied = true };

        bool accepted = set(requested);
        string effective = get();
        // GetXCoreId() returns the live core's type name ("CPU_FIX"), not the bare suffix ("FIX").
        bool matches = effective.Equals(requested, StringComparison.OrdinalIgnoreCase)
                    || effective.EndsWith("_" + requested, StringComparison.OrdinalIgnoreCase);
        return new CoreApplyReport
        {
            Kind = kind, Requested = requested, Accepted = accepted,
            Effective = effective, Applied = accepted && matches,
        };
    }

    // ---- input script --------------------------------------------------------

    internal readonly struct InputStep
    {
        public InputStep(int frame, bool[] held, List<string> names) { Frame = frame; Held = held; Names = names; }
        public int Frame { get; }
        public bool[] Held { get; }
        public List<string> Names { get; }
    }

    // Index order fixed by NesEmulator.Input.SetInput (Windows/NesEmulator/board/Input.cs:13):
    // 0=A 1=B 2=Select 3=Start 4=Up 5=Down 6=Left 7=Right. NES.SetInputs() passes the array
    // through unremapped.
    private static readonly Dictionary<string, int> ButtonIndex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 0, ["B"] = 1, ["Select"] = 2, ["Start"] = 3,
        ["Up"] = 4, ["Down"] = 5, ["Left"] = 6, ["Right"] = 7,
    };

    // internal, not private: --trace must apply an input script with byte-identical semantics to
    // --romtest, so there is exactly one parser and exactly one definition of "held from this frame
    // inclusive". Writing a second one is the classic way a differential test silently lies.
    internal static List<InputStep> ParseInputScript(string? script)
    {
        var steps = new List<InputStep>();
        if (string.IsNullOrWhiteSpace(script)) return steps;

        foreach (var rawStep in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = rawStep.Trim();
            if (step.Length == 0) continue;

            int colon = step.IndexOf(':');
            if (colon < 0) throw new FormatException($"step '{step}' has no ':' (expected frame:buttons)");
            if (!int.TryParse(step[..colon].Trim(), out int frame) || frame < 0)
                throw new FormatException($"step '{step}' has a bad frame number");

            var held = new bool[8];
            var names = new List<string>();
            foreach (var rawBtn in step[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                var btn = rawBtn.Trim();
                if (btn.Length == 0) continue;
                if (!ButtonIndex.TryGetValue(btn, out int idx))
                    throw new FormatException($"unknown button '{btn}' (valid: {string.Join(", ", ButtonIndex.Keys)})");
                held[idx] = true;
                names.Add(btn);
            }
            steps.Add(new InputStep(frame, held, names));
        }

        // Stable sort by frame so the runner can walk the list with a single cursor.
        return steps.OrderBy(s => s.Frame).ToList();
    }

    // ---- framebuffer signals -------------------------------------------------

    // FNV-1a 64: stable across runs, processes and machines (unlike string/array GetHashCode,
    // which is randomized per-process), so two separate invocations' hashes are comparable.
    private static string Fnv1a64(byte[] data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in data) { h ^= b; h *= 1099511628211UL; }
        return h.ToString("X16");
    }

    // Framebuffer is RGBA bytes; alpha is ignored. Bounded by the 64-entry NES palette in practice,
    // so an uncapped HashSet is cheap. 1 == a flat single-color screen (nothing rendered).
    private static int CountDistinctColors(byte[] fb)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i + 3 < fb.Length; i += 4)
            seen.Add((fb[i] << 16) | (fb[i + 1] << 8) | fb[i + 2]);
        return seen.Count;
    }

    // ---- output --------------------------------------------------------------

    private static void Emit(RomTestReport report, bool json, string? outPath)
    {
        string text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (outPath != null)
        {
            try { File.WriteAllText(outPath, text); }
            catch (Exception ex) { Console.Error.WriteLine($"Failed to write --out file: {ex.Message}"); }
        }
        Console.WriteLine(json ? text : Humanize(report));
        Console.Out.Flush();
    }

    // Workshop is a WinExe (it has to be - it's a WinForms app), so a bare interactive run has no
    // stdout handle at all and Console.WriteLine goes nowhere. Piped/redirected runs are fine and
    // must NOT be touched, so only borrow the parent's console when there is genuinely no stdout.
    //
    // Note Console.IsOutputRedirected cannot make this call: with a null handle GetFileType()
    // reports FILE_TYPE_UNKNOWN, which .NET reads as "redirected" - the exact case we need to fix.
    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    internal static void EnsureConsole()
    {
        try
        {
            IntPtr h = GetStdHandle(StdOutputHandle);
            bool haveRealStdout = h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) != 0; // 0 = FILE_TYPE_UNKNOWN
            if (haveRealStdout) return;
            if (!AttachConsole(AttachParentProcess)) return;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { /* best-effort only - --out and the exit code stay authoritative */ }
    }

    private static string Humanize(RomTestReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ROM        : {r.Rom} ({r.RomBytes} bytes)");
        foreach (var c in new[] { r.Cpu, r.Ppu, r.Apu })
        {
            if (c == null) continue;
            string req = c.Requested ?? "(default)";
            sb.AppendLine($"{c.Kind,-11}: requested={req} accepted={c.Accepted} effective={c.Effective} applied={c.Applied}");
        }
        if (r.InputSteps.Count > 0)
        {
            sb.AppendLine($"Input      : {r.InputSteps.Count} step(s)");
            foreach (var s in r.InputSteps) sb.AppendLine($"             frame {s.Frame} -> {s.Buttons}");
        }
        else sb.AppendLine("Input      : (none)");
        sb.AppendLine($"Frames     : ran {r.FramesRun} of {r.RequestedFrames}");
        sb.AppendLine($"Framebuffer: {r.FrameBufferBytes} bytes, hash={r.FrameBufferHash}, distinct colors={r.DistinctColors}");
        if (r.Result == "crashed")
            sb.AppendLine($"RESULT     : CRASHED at frame {r.CrashFrame}: {r.CrashInfo}");
        else if (r.Result == "core-not-applied")
            sb.AppendLine("RESULT     : FAILED - a requested core did not apply (see 'applied=False' above)");
        else
            sb.AppendLine("RESULT     : OK - no crash");
        return sb.ToString().TrimEnd();
    }

    // ---- report shape --------------------------------------------------------

    private sealed class RomTestReport
    {
        public string Rom { get; set; } = "";
        public int RomBytes { get; set; }
        public CoreApplyReport? Cpu { get; set; }
        public CoreApplyReport? Ppu { get; set; }
        public CoreApplyReport? Apu { get; set; }
        public List<InputStepReport> InputSteps { get; set; } = new();
        public int RequestedFrames { get; set; }
        public int FramesRun { get; set; }
        public int FrameBufferBytes { get; set; }
        public string FrameBufferHash { get; set; } = "";
        public int DistinctColors { get; set; }
        public string Result { get; set; } = "";
        public int? CrashFrame { get; set; }
        public string? CrashInfo { get; set; }
    }

    internal sealed class CoreApplyReport
    {
        public string Kind { get; set; } = "";
        public string? Requested { get; set; }
        public bool Accepted { get; set; }
        public string Effective { get; set; } = "";
        public bool Applied { get; set; }
    }

    private sealed class InputStepReport
    {
        public int Frame { get; set; }
        public string Buttons { get; set; } = "";
    }
}
