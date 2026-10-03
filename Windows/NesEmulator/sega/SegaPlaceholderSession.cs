using System;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>
/// What a Sega console runs while its core does not exist yet: colour bars and a square the pad moves, silence, the right picture size and frame rate. It exists
/// so the plumbing (menus, ROM detection, input, display size, saves, the three apps) can be exercised end to end during the foundation phase. It is reachable
/// only through the preview switch (<see cref="Consoles.SegaPreview"/>), says what it is in <see cref="Description"/>, and plays nothing: it is not an emulator
/// and is never presented as one. Each track replaces it in <see cref="ConsoleSessions.Create"/> when its core is certified.
/// </summary>
public sealed class SegaPlaceholderSession : IConsoleSession
{
    private const int Rate = 44100;
    private readonly uint[] frame;
    private readonly int width, height;
    private PadButtons pad;
    private int squareX, squareY, frames;
    private double sampleDebt;
    private int pendingShorts;
    private string cpuId, ppuId, apuId;

    private static readonly uint[] Bars =
        { 0xFFFFFFFF, 0xFFFFFF00, 0xFF00FFFF, 0xFF00FF00, 0xFFFF00FF, 0xFFFF0000, 0xFF0000FF, 0xFF000000 };

    public SegaPlaceholderSession(ConsoleKind console, byte[] rom, string cpu, string ppu, string apu)
    {
        Console = console;
        (width, height) = SegaTiming.FrameSize(console, SegaRegion.Ntsc);
        frame = new uint[width * height];
        squareX = width / 2 - 8; squareY = height / 2 - 8;
        GameId = ConsoleSessions.Sha1(rom);
        FramesPerSecond = console == ConsoleKind.Genesis ? SegaTiming.GenesisFramesPerSecond(SegaRegion.Ntsc) : SegaTiming.MasterSystemFramesPerSecond(SegaRegion.Ntsc);
        cpuId = cpu; ppuId = ppu; apuId = apu;
        Title = $"{Consoles.DisplayName(console)} placeholder";
        Draw();
    }

    public ConsoleKind Console { get; }
    public string Title { get; }
    public string Description => $"PLACEHOLDER: the {Consoles.DisplayName(Console)} core is not implemented yet (colour bars, no game, no sound) | CPU {cpuId} | PPU {ppuId} | APU {apuId}";
    public double FramesPerSecond { get; }
    public string GameId { get; }

    public void SetPad(int player, PadButtons buttons) { if (player == 0) pad = buttons; }

    public void RunFrame()
    {
        frames++;
        if ((pad & PadButtons.Left) != 0) squareX -= 2;
        if ((pad & PadButtons.Right) != 0) squareX += 2;
        if ((pad & PadButtons.Up) != 0) squareY -= 2;
        if ((pad & PadButtons.Down) != 0) squareY += 2;
        squareX = Math.Clamp(squareX, 0, width - 16);
        squareY = Math.Clamp(squareY, 0, height - 16);
        Draw();
        sampleDebt += Rate / FramesPerSecond;
        int whole = (int)sampleDebt;
        sampleDebt -= whole;
        pendingShorts += whole * 2;   // silence, but the right amount of it, so audio-paced apps keep their rhythm
    }

    private void Draw()
    {
        // eight vertical bars over the upper two thirds, a grey ramp below, then the square (white; red with A, green with B, blue with C, yellow with Start)
        int barsEnd = height * 2 / 3;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint c;
                if (y < barsEnd) c = Bars[x * Bars.Length / width];
                else { uint g = (uint)(x * 255 / Math.Max(1, width - 1)); c = 0xFF000000 | g << 16 | g << 8 | g; }
                frame[y * width + x] = c;
            }
        uint sq = (pad & PadButtons.A) != 0 ? 0xFFFF2020 : (pad & PadButtons.B) != 0 ? 0xFF20FF20 : (pad & PadButtons.C) != 0 ? 0xFF4040FF
                : (pad & PadButtons.Start) != 0 ? 0xFFFFFF20 : 0xFFFFFFFF;
        for (int y = squareY; y < squareY + 16; y++)
            for (int x = squareX; x < squareX + 16; x++)
                frame[y * width + x] = (x == squareX || x == squareX + 15 || y == squareY || y == squareY + 15) ? 0xFF000000 : sq;
    }

    public void Reset() { squareX = width / 2 - 8; squareY = height / 2 - 8; frames = 0; pendingShorts = 0; sampleDebt = 0; Draw(); }
    public uint[] Frame => frame;
    public int FrameWidth => width;
    public int FrameHeight => height;
    public int DisplayWidth => width;
    public int SampleRate => Rate;

    public int ReadSamples(short[] buffer)
    {
        int n = Math.Min(pendingShorts, buffer.Length) & ~1;
        Array.Clear(buffer, 0, n);
        pendingShorts -= n;
        return n;
    }

    public bool HasBattery => false;
    public byte[] ExportSave() => Array.Empty<byte>();
    public void ImportSave(byte[] data) { }

    public bool TrySwapCore(CoreSlot slot, string id)
    {
        switch (slot) { case CoreSlot.Cpu: cpuId = id; break; case CoreSlot.Ppu: ppuId = id; break; default: apuId = id; break; }
        return true;
    }

    public void Dispose() { }
}
