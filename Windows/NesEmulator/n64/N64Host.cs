// [brokennes_int2] Registers the N64 with BrokenNes's ConsoleSessions (registration.patch adds the hooks). Lands at
// Windows/NesEmulator/n64/N64Host.cs. Only the hosts that reference cor64 call it (desktop, Workshop, BrokenDev MCP).

using NesEmulator.Systems;
using cor64.Cores;

namespace NesEmulator.N64;

public static class N64Host
{
    /// <summary>Offers the N64: ROM detection, menus and ConsoleSessions.Create. Safe to call more than once.</summary>
    public static void Register()
    {
        ConsoleSessions.N64Factory = (rom, fileName, ppu, apu) => new N64ConsoleSession(rom, fileName, ppu, apu);
        ConsoleSessions.N64PpuAvailable = id => N64Cores.IsAvailable(N64Slot.Ppu, id);
        Consoles.N64Enabled = true;
    }

    /// <summary>The cor64 debug target (named memory domains, registers, disassembly, symbols) for a running N64 session.</summary>
    public static cor64.Debug.N64DebugTarget DebugTarget(N64ConsoleSession s, ulong romSize) => new(s.Core, romSize);
}
