using NesEmulator.Corruption;

namespace BrokenNes.CorruptorModels
{
    // Models for corruptor/RTC/Glitch Harvester
    public interface ICorruptorEmulatorHooks
    {
        void ImagineFromPc(ushort pc, int bytesToGenerate);
    }
    public class DomainSel
    {
        public string Key = "";
        public string Label = "";
        public bool Selected = true;
        public int Size;
    }
    public class HarvesterBaseState
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        /// <summary>The NES's savestate (JSON text). Empty for a base taken on a console session, which has <see cref="Anchor"/> instead.</summary>
        public string State { get; set; } = string.Empty;
        public DateTime Created { get; set; } = DateTime.UtcNow;
        /// <summary>A base taken on a console session (SNES, Game Boy, Master System, Game Gear, Genesis): where that machine was, as a saved state or a replay from power-on (see NesEmulator.Corruption).</summary>
        public Anchor? Anchor { get; set; }
        /// <summary>The game and machine the base belongs to (console sessions).</summary>
        public GameRef? Game { get; set; }
        public MachineSpec? Machine { get; set; }
    }
    public class BlastInstruction
    {
        public string Domain { get; set; } = string.Empty;
        public int Address { get; set; }
        public byte Value { get; set; }
        /// <summary>The byte that was there when the corruption was made (-1: not recorded; the NES path does not).</summary>
        public int Was { get; set; } = -1;
        /// <summary>Frames after the anchor at which the write lands (0: at the anchor).</summary>
        public int At { get; set; }
    }
    public class HarvestEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string BaseStateId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public List<BlastInstruction> Writes { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        // ---- the stockpile file's richer entry (NesEmulator.Corruption): optional, so the NES path and Lite's list keep working unchanged ----
        public string Description { get; set; } = string.Empty;
        /// <summary>Console-session entries: where the machine is when the writes land. The NES's entries keep it in <see cref="State"/>.</summary>
        public Anchor? Anchor { get; set; }
        public GameRef? Game { get; set; }
        public MachineSpec? Machine { get; set; }
        public BlastSource? Source { get; set; }
        /// <summary>Set when the entry came from an older file in a form that lost something ("no-state").</summary>
        public string Legacy { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
    }
    public class CorruptorSnapshot
    {
        public int CorruptIntensity { get; set; }
        public string BlastType { get; set; } = string.Empty;
        public List<DomainSel> MemoryDomains { get; set; } = new();
        public bool AutoCorrupt { get; set; }
        public string LastBlastInfo { get; set; } = string.Empty;
        public bool StubbornMode { get; set; }
        public string CrashBehavior { get; set; } = string.Empty;
        public List<HarvesterBaseState> GhBaseStates { get; set; } = new();
        public List<HarvestEntry> GhStash { get; set; } = new();
        public List<HarvestEntry> GhStockpile { get; set; } = new();
        public string GhSelectedBaseId { get; set; } = string.Empty;
        public bool GhLoadOnOperation { get; set; } = true;
    }
}
