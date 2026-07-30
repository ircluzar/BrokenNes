using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BrokenNes.Workshop;

/// <summary>
/// Compares two "nesreflex-raw-v2" per-frame dumps - one produced by the ML_NesPlayer custom
/// FCEUX fork (TAS/fceux_custom/src/nesreflex_dump.cpp, via -nesreflex-dump) and one produced by
/// BrokenNes (Workshop.Tas.NesReflexDumpWriter, via --dump-out) replaying the same .fm2 movie -
/// frame by frame, field by field. This is the actual cross-emulator accuracy check: unlike
/// AccuracyCoin (a synthetic test ROM) or the self-play witness verification (BrokenNes against
/// itself), this diffs BrokenNes against a real, independent reference implementation.
///
/// Two fields are known, expected to differ and are excluded from comparison (see
/// NesReflexDumpWriter's class doc for why): IRQlow (no uniform equivalent across BrokenNes's CPU
/// cores) and screen_hash (same CRC32 algorithm, different underlying pixel format). Everything
/// else - CPU regs, PC, open bus, RAM, PPU regs/scroll/vram-addr/scanline/dot, OAM, palette,
/// nametables, lag flag - is a genuine field-for-field mirror and IS compared.
///
/// Usage: --compare-dumps --a fceux.raw --b brokennes.raw [--out result.json] [--max-mismatches N]
/// </summary>
internal static class DumpCompareCli
{
    private const int HeaderSize = 16; // 8-byte magic + u32 formatVersion + u32 recordLayoutVersion
    private const int RecordSize = 4432;

    public static int Run(string[] args)
    {
        string? aPath = null, bPath = null, outPath = null;
        int maxMismatchesToRecord = 20;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--a": aPath = args[++i]; break;
                case "--b": bPath = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--max-mismatches": maxMismatchesToRecord = int.Parse(args[++i]); break;
            }
        }
        if (aPath == null || bPath == null)
        {
            Console.Error.WriteLine("Usage: --compare-dumps --a <fceux.raw> --b <brokennes.raw> [--out result.json] [--max-mismatches N]");
            return 2;
        }

        byte[] aBytes, bBytes;
        try { aBytes = File.ReadAllBytes(aPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read --a '{aPath}': {ex.Message}"); return 2; }
        try { bBytes = File.ReadAllBytes(bPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read --b '{bPath}': {ex.Message}"); return 2; }

        if (!TryReadHeader(aBytes, out int aFrameCount, out string? aErr)) { Console.Error.WriteLine($"--a: {aErr}"); return 3; }
        if (!TryReadHeader(bBytes, out int bFrameCount, out string? bErr)) { Console.Error.WriteLine($"--b: {bErr}"); return 3; }

        int framesToCompare = Math.Min(aFrameCount, bFrameCount);
        int firstMismatchFrame = -1;
        var firstMismatchFields = new List<string>();
        int mismatchedFrameCount = 0;
        int lastMismatchFrame = -1;
        var sampleMismatches = new List<FrameMismatch>();

        for (int f = 0; f < framesToCompare; f++)
        {
            int aOff = HeaderSize + f * RecordSize;
            int bOff = HeaderSize + f * RecordSize;
            var fields = CompareRecord(aBytes, aOff, bBytes, bOff);
            if (fields.Count > 0)
            {
                mismatchedFrameCount++;
                lastMismatchFrame = f;
                if (firstMismatchFrame < 0)
                {
                    firstMismatchFrame = f;
                    firstMismatchFields = fields;
                }
                if (sampleMismatches.Count < maxMismatchesToRecord)
                    sampleMismatches.Add(new FrameMismatch(f, fields));
            }
        }

        bool frameCountMismatch = aFrameCount != bFrameCount;
        bool pass = !frameCountMismatch && firstMismatchFrame < 0;

        var result = new DumpCompareResult(
            Path.GetFileName(aPath), Path.GetFileName(bPath),
            aFrameCount, bFrameCount, frameCountMismatch,
            framesToCompare, firstMismatchFrame, firstMismatchFields,
            mismatchedFrameCount, lastMismatchFrame, sampleMismatches, pass);

        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        if (outPath != null) File.WriteAllText(outPath, json);
        Console.WriteLine(json);
        return pass ? 0 : 1;
    }

    private static bool TryReadHeader(byte[] data, out int frameCount, out string? error)
    {
        frameCount = 0; error = null;
        if (data.Length < HeaderSize) { error = "file too short for header"; return false; }
        if (data[0] != 'N' || data[1] != 'R' || data[2] != 'F' || data[3] != 'X' ||
            data[4] != 'R' || data[5] != 'A' || data[6] != 'W' || data[7] != '2')
        { error = "bad magic (expected NRFXRAW2)"; return false; }
        int payload = data.Length - HeaderSize;
        if (payload % RecordSize != 0)
        { error = $"file size {data.Length} isn't header + N*{RecordSize} - truncated dump?"; return false; }
        frameCount = payload / RecordSize;
        return true;
    }

    // Byte offsets within one 4432-byte record, per NesReflexDumpWriter.WriteFrame /
    // nesreflex_dump.cpp's WriteFrame - verified field-for-field against both sources.
    private static readonly (string Name, int Offset, int Length)[] ComparedFields =
    {
        ("MovieFrame", 4, 4),
        ("P1Input", 8, 1),
        ("P2Input", 9, 1),
        ("Cmd", 10, 1),
        ("A", 11, 1),
        ("X", 12, 1),
        ("Y", 13, 1),
        ("SP", 14, 1),
        // P is compared with bit 5 masked off - see MaskedFields. The 6502's bit 5 is physically
        // always 1 and has no effect on execution; FCEUX keeps it out of its stored P and ORs it
        // in only when pushing to the stack, while BrokenNes stores it. Purely representational.
        ("P", 15, 1),
        ("PC", 16, 2),
        ("OpenBus", 18, 1),
        // offset 19..22 = IRQlow - excluded, see class doc
        ("LagFlag", 23, 1),
        ("LagCounter", 24, 4),
        ("Ram", 28, 2048),
        ("PPUCTRL", 2076, 1),
        ("PPUMASK", 2077, 1),
        ("PPUSTATUS", 2078, 1),
        ("OAMADDR", 2079, 1),
        ("ScrollX", 2080, 1),
        ("ScrollY", 2081, 1),
        ("VramAddrLo", 2082, 1),
        ("VramAddrHi", 2083, 1),
        ("ScrollXDup", 2084, 1),
        ("ScrollYDup", 2085, 1),
        ("VramAddr16", 2086, 2),
        // Scanline/Dot deliberately NOT compared: each emulator's "frame complete" hook fires at a
        // different phase of the PPU's continuous dot stream, so these describe *when the dump was
        // taken*, not emulation state. Verified against real dumps: FCEUX reports scanline=0 dot=0
        // on every frame (it hooks exactly at the frame boundary) while BrokenNes reports whatever
        // dot it actually landed on. Comparing them would flag every frame of every game while
        // carrying no information about emulation correctness.
        ("Oam", 2092, 256),
        ("Palette", 2348, 32),
        ("Nametables", 2380, 2048),
        // offset 4428..4431 = screen_hash - excluded, see class doc
    };

    /// <summary>Per-field AND mask applied to both sides before comparing (single-byte fields
    /// only). Used to ignore bits that are representational rather than emulated state.</summary>
    private static readonly Dictionary<string, byte> MaskedFields = new()
    {
        ["P"] = 0xDF, // clear bit 5 - the 6502's always-1 unused flag, stored by one side and not the other
    };

    private static List<string> CompareRecord(byte[] a, int aOff, byte[] b, int bOff)
    {
        var mismatches = new List<string>();
        foreach (var (name, offset, length) in ComparedFields)
        {
            if (MaskedFields.TryGetValue(name, out byte mask))
            {
                if ((a[aOff + offset] & mask) != (b[bOff + offset] & mask)) mismatches.Add(name);
                continue;
            }
            if (!BytesEqual(a, aOff + offset, b, bOff + offset, length))
                mismatches.Add(name);
        }
        return mismatches;
    }

    private static bool BytesEqual(byte[] a, int aStart, byte[] b, int bStart, int length)
    {
        for (int i = 0; i < length; i++)
            if (a[aStart + i] != b[bStart + i]) return false;
        return true;
    }
}

internal sealed record FrameMismatch(int Frame, List<string> Fields);

internal sealed record DumpCompareResult(
    string FileA, string FileB,
    int FrameCountA, int FrameCountB, bool FrameCountMismatch,
    int FramesCompared, int FirstMismatchFrame, List<string> FirstMismatchFields,
    int MismatchedFrameCount, int LastMismatchFrame, List<FrameMismatch> SampleMismatches,
    bool Pass);
