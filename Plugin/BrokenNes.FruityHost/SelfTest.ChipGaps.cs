// `chip-gaps <dll>` (UI overhaul, 2026-11-13): which APU cores of the shared emulator tree the plugin does NOT offer as a chip, with their family / ported engine,
// so "every APU core that is not offered yet" is checked against the real registry instead of a hand-made list.
using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    public static int ChipGaps(string dll)
    {
        var lib = new PluginLibrary(dll);
        List<string> chips;
        using (var p = lib.Create()) chips = Chips(p);
        var offered = new HashSet<string>(chips, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"{chips.Count} chips offered, {CoreRegistry.ApuIds.Count} APU core ids registered");
        int n = 0;
        foreach (var id in CoreRegistry.ApuIds.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
        {
            if (offered.Contains(id)) continue;
            var bridge = CoreCatalog.PortedBridgeEngine(id);
            Console.WriteLine($"  {id,-14} family {CoreCatalog.NesFamilyOf(CoreSlot.Apu, id),-14} {(bridge is { } b ? "ported bridge on " + b.Engine : "")}");
            n++;
        }
        Console.WriteLine($"{n} not offered");
        return 0;
    }
}
