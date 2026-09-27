using System;

namespace NesEmulator.Snes;

/// <summary>
/// Silent stand-in for the SNES audio unit: emulates the IPL boot loader's upload protocol at a
/// high level instead of running an SPC700, so games get past their boot-time sound upload.
///   ports read $AA,$BB -> CPU puts address in ports 2/3, a command in port 1, $CC in port 0;
///   the IPL echoes port 0. Command != 0: transfer, where each byte arrives in port 1 with its
///   index (0,1,2..) in port 0, and the IPL echoes the index. A jump of the index by >= 2 starts
///   the next command. Command 0: execute the uploaded program at the address.
/// Once "running" there is no program to answer, so ports simply echo.
/// Kept as a fallback and as a diagnostic: if a game boots on this but not on APU_SFC, the bug is
/// in the SPC700/DSP, not in the CPU or board.
/// </summary>
public sealed class APU_HLE : ISnesApu
{
    public string CoreName => "HLE";

    private readonly byte[] apuToCpu = new byte[4];
    private readonly byte[] cpuToApu = new byte[4];
    public readonly byte[] Aram = new byte[0x10000];
    private enum IplState { WaitKick, Transfer, Running }
    private IplState state;
    private byte index;
    private ushort address;
    public int EntryPoint { get; private set; } = -1;
    public int BytesUploaded { get; private set; }

    public APU_HLE() { Reset(); }

    public void Reset()
    {
        apuToCpu[0] = 0xAA; apuToCpu[1] = 0xBB; apuToCpu[2] = apuToCpu[3] = 0;
        state = IplState.WaitKick;
    }

    public byte ReadPort(int port) => apuToCpu[port & 3];
    public void RunTo(long masterClock) { }
    public int SampleRate => 32000;
    public int ReadSamples(Span<short> dest) => 0;

    public string Describe() =>
        $"HLE ports={apuToCpu[0]:X2} {apuToCpu[1]:X2} {apuToCpu[2]:X2} {apuToCpu[3]:X2} uploaded={BytesUploaded:N0} entry={(EntryPoint < 0 ? "none" : "$" + EntryPoint.ToString("X4"))}";

    public void WritePort(int port, byte value)
    {
        port &= 3;
        cpuToApu[port] = value;
        if (state == IplState.Running)
        {
            // HEURISTIC (no driver to run): Nintendo's own sound driver - Super Mario World's - jumps
            // back into the IPL when the CPU writes $FF to port 1, so it can take another upload.
            if (port == 1 && value == 0xFF) { Reset(); return; }
            apuToCpu[port] = value;
            return;
        }
        if (port != 0) return;   // the IPL only reacts to port 0; ports 1-3 are read when it does

        if (state == IplState.WaitKick)
        {
            if (value == 0xCC) Command(value);
            return;
        }
        if (value == index)
        {
            Aram[(ushort)(address + index)] = cpuToApu[1];
            BytesUploaded++;
            apuToCpu[0] = value;
            index++;
            if (index == 0) address += 0x100;
        }
        else if ((sbyte)(index - value) < 0) Command(value);
    }

    private void Command(byte port0)
    {
        address = (ushort)(cpuToApu[2] | cpuToApu[3] << 8);
        apuToCpu[0] = port0;
        if (cpuToApu[1] != 0) { state = IplState.Transfer; index = 0; }
        else { state = IplState.Running; EntryPoint = address; }
    }
}
