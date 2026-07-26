using System;
using System.IO;
using System.IO.Pipes;
using NesEmulator;

namespace BrokenNes.Workshop.Tas.SelfPlay;

/// <summary>
/// Named-pipe client for the source project's live inference server
/// (scripts/nesreflex_inference_server.py), protocol v2 - the only version the actual C++
/// emulator ever sends (input.cpp has no v1 frame-construction code; v1 exists solely as legacy
/// support in the Python server for an older, now-unused emulator build). Runs unmodified against
/// the exact same server and checkpoint file the source project uses.
///
/// Wire format (input.cpp:152-161, 1941-2024, 1863-1902):
///   Request:  24 frames x 2504 bytes + 4 bytes game_id (u32 LE) = 60,100 bytes.
///     Per-frame: RAM[2048] + regs[8] + ppu_summary[16] + OAM[256] + palette[32] + nt_summary[128]
///                + button_pressed[8] + hold_elapsed[8]  (offsets match NESREFLEX_CTX_*_OFF).
///   Response: 8x float32 button_logits + 8x float32 hold_remaining (normalized /32) = 64 bytes.
/// </summary>
public sealed class InferencePipeClient : IDisposable
{
    public const int Frames = 24;
    public const int FrameSize = 2504;
    public const int RequestSize = Frames * FrameSize + 4;
    public const int ResponseSize = 64;

    private const int RamOff = 0;
    private const int RegsOff = 2048;
    private const int PpuOff = 2056;
    private const int OamOff = 2072;
    private const int PalOff = 2328;
    private const int NtOff = 2360;
    private const int BtnOff = 2488;
    private const int HoldOff = 2496;

    private readonly byte[] _ring = new byte[Frames * FrameSize];
    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe is { IsConnected: true };

    public bool Connect(string pipeName, int timeoutMs = 2000)
    {
        Disconnect();
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(timeoutMs);
            _pipe = pipe;
            return true;
        }
        catch { return false; }
    }

    public void Disconnect()
    {
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _pipe = null;
    }

    /// <summary>Shifts the 24-frame ring buffer and appends the current frame's observation.
    /// `appliedButtons` and `holdElapsed` are causal (this frame's actually-applied state, per
    /// NESREFLEX_CTX_BTN_OFF/HOLD_OFF's own doc comment in the source), in BrokenNes's own button
    /// order (0:A 1:B 2:Select 3:Start 4:Up 5:Down 6:Left 7:Right) - no RLDUTSBA translation
    /// needed here, unlike the FM2 text format.</summary>
    public void PushFrame(NES nes, bool[] appliedButtons, byte[] holdElapsed)
    {
        Array.Copy(_ring, FrameSize, _ring, 0, (Frames - 1) * FrameSize);
        int slot = (Frames - 1) * FrameSize;

        byte[] ram = nes.PeekMemoryRange("System RAM", 0, 2048);
        Array.Copy(ram, 0, _ring, slot + RamOff, 2048);

        var regs = nes.GetCpuRegs();
        _ring[slot + RegsOff + 0] = regs.A;
        _ring[slot + RegsOff + 1] = regs.X;
        _ring[slot + RegsOff + 2] = regs.Y;
        _ring[slot + RegsOff + 3] = (byte)(regs.SP & 0xFF);
        _ring[slot + RegsOff + 4] = regs.P;
        _ring[slot + RegsOff + 5] = (byte)(regs.PC & 0xFF);
        _ring[slot + RegsOff + 6] = (byte)((regs.PC >> 8) & 0xFF);
        _ring[slot + RegsOff + 7] = nes.GetOpenBusValue();

        var ppu = nes.GetPpuState() as PpuSharedState;
        int scrollX = ppu?.PPUSCROLLX ?? 0, scrollY = ppu?.PPUSCROLLY ?? 0;
        int vramAddr = ppu?.PPUADDR ?? 0;
        int scanline = Math.Max(0, ppu?.scanline ?? 0);
        int dot = Math.Max(0, ppu?.scanlineCycle ?? 0);
        _ring[slot + PpuOff + 0] = ppu?.PPUCTRL ?? 0;
        _ring[slot + PpuOff + 1] = ppu?.PPUMASK ?? 0;
        _ring[slot + PpuOff + 2] = ppu?.PPUSTATUS ?? 0;
        _ring[slot + PpuOff + 3] = ppu?.OAMADDR ?? 0;
        _ring[slot + PpuOff + 4] = (byte)(scrollX & 0xFF);
        _ring[slot + PpuOff + 5] = (byte)(scrollY & 0xFF);
        _ring[slot + PpuOff + 6] = (byte)(vramAddr & 0xFF);
        _ring[slot + PpuOff + 7] = (byte)((vramAddr >> 8) & 0xFF);
        _ring[slot + PpuOff + 8] = (byte)(scrollX & 0xFF);
        _ring[slot + PpuOff + 9] = (byte)(scrollY & 0xFF);
        _ring[slot + PpuOff + 10] = (byte)(vramAddr & 0xFF);
        _ring[slot + PpuOff + 11] = (byte)((vramAddr >> 8) & 0xFF);
        _ring[slot + PpuOff + 12] = (byte)(scanline & 0xFF);
        _ring[slot + PpuOff + 13] = (byte)(dot & 0xFF);
        _ring[slot + PpuOff + 14] = (byte)((scanline >> 8) & 0xFF);
        _ring[slot + PpuOff + 15] = (byte)((dot >> 8) & 0xFF);

        byte[] oam = ppu?.oam ?? new byte[256];
        byte[] palette = ppu?.palette ?? new byte[32];
        byte[] nt = ppu?.vram ?? new byte[2048];
        Array.Copy(oam, 0, _ring, slot + OamOff, 256);
        Array.Copy(palette, 0, _ring, slot + PalOff, 32);
        for (int i = 0; i < 128; i++)
        {
            uint sum = 0;
            for (int j = 0; j < 16; j++) sum += nt[i * 16 + j];
            _ring[slot + NtOff + i] = (byte)((sum + 8) / 16);
        }

        for (int b = 0; b < 8; b++) _ring[slot + BtnOff + b] = (byte)(appliedButtons[b] ? 1 : 0);
        for (int b = 0; b < 8; b++) _ring[slot + HoldOff + b] = holdElapsed[b];
    }

    /// <summary>Sends the current 24-frame context + game_id and blocks for the response.
    /// Disconnects and returns null on any I/O failure - matches the source's own behavior of
    /// tearing down the pipe on error rather than trying to resync mid-stream.</summary>
    public (float[] ButtonLogits, float[] HoldRemaining)? Query(int gameId)
    {
        if (_pipe == null || !_pipe.IsConnected) return null;
        try
        {
            var request = new byte[RequestSize];
            Array.Copy(_ring, request, _ring.Length);
            uint gid = (uint)gameId;
            request[_ring.Length + 0] = (byte)(gid & 0xFF);
            request[_ring.Length + 1] = (byte)((gid >> 8) & 0xFF);
            request[_ring.Length + 2] = (byte)((gid >> 16) & 0xFF);
            request[_ring.Length + 3] = (byte)((gid >> 24) & 0xFF);

            _pipe.Write(request, 0, request.Length);
            _pipe.Flush();

            byte[] response = new byte[ResponseSize];
            int total = 0;
            while (total < ResponseSize)
            {
                int read = _pipe.Read(response, total, ResponseSize - total);
                if (read <= 0) throw new IOException("Pipe closed while reading response.");
                total += read;
            }

            var logits = new float[8];
            var hold = new float[8];
            for (int i = 0; i < 8; i++) logits[i] = BitConverter.ToSingle(response, i * 4);
            for (int i = 0; i < 8; i++) hold[i] = BitConverter.ToSingle(response, 32 + i * 4);
            return (logits, hold);
        }
        catch
        {
            Disconnect();
            return null;
        }
    }

    public void Dispose() => Disconnect();
}
