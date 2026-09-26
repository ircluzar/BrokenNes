using System;
using System.IO;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>`--apu SFC|HLE` for every SNES mode: the real audio unit, or the silent IPL stand-in.</summary>
internal static class SnesApuChoice
{
    public const string Default = "SFC";

    public static ISnesApu Create(string? choice) =>
        (choice ?? Default).ToUpperInvariant() switch
        {
            "SFC" => new APU_SFC(),
            "HLE" => new APU_HLE(),
            _ => throw new FormatException($"unknown --apu '{choice}' (expected SFC or HLE)"),
        };
}

/// <summary>Streams 16-bit stereo PCM to a .wav file, patching the header sizes on dispose.</summary>
internal sealed class WavWriter : IDisposable
{
    private readonly FileStream fs;
    private readonly BinaryWriter w;
    private long dataBytes;
    public long Frames => dataBytes / 4;

    public WavWriter(string path, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        fs = File.Create(path);
        w = new BinaryWriter(fs);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(sampleRate); w.Write(sampleRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(0);
    }

    public void Write(ReadOnlySpan<short> interleaved)
    {
        foreach (short s in interleaved) w.Write(s);
        dataBytes += interleaved.Length * 2;
    }

    public void Dispose()
    {
        w.Flush();
        fs.Position = 4; w.Write((int)(36 + dataBytes));
        fs.Position = 40; w.Write((int)dataBytes);
        w.Dispose();
    }
}
