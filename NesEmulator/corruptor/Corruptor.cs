using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes
{
    // Handles corruption logic (RTC, Glitch Harvester, etc.)
    public class Corruptor
    {

    // Corruptor state
    public List<DomainSel> MemoryDomains { get; set; } = new();
    public int CorruptIntensity { get; set; } = 1;
    public string BlastType { get; set; } = "RANDOM";
    public bool AutoCorrupt { get; set; } = false;
    public Random CorruptRnd { get; } = new();
    public string LastBlastInfo = string.Empty;
    public bool LetItRipUsed = false;
    public string CrashBehavior = "IgnoreErrors";

    // Glitch Harvester state
    public List<HarvesterBaseState> GhBaseStates { get; set; } = new();
    public List<HarvestEntry> GhStash { get; set; } = new();
    public List<HarvestEntry> GhStockpile { get; set; } = new();
    public string GhSelectedBaseId { get; set; } = string.Empty;
    public string GhNewBaseName { get; set; } = string.Empty;
    public int GhStashCounter { get; set; } = 0;
    public int GhStockpileCounter { get; set; } = 0;
    public string? GhRenamingId { get; set; } = null;
    public string GhRenameText { get; set; } = string.Empty;

    // Methods to be filled in with logic
        public void Blast(NES nes)
        {
            var selected = MemoryDomains.Where(d => d.Selected && d.Size > 0).ToList();
            if (selected.Count == 0) return;
            int writes = Math.Clamp(CorruptIntensity, 1, 4096);
            // Imagine integration: treat BlastType IMAGINENEXT/IMAGINERANDOM specially and bail after one op (intensity governs bytes length)
            var modeUpper = BlastType?.ToUpperInvariant() ?? "";
            if (modeUpper == "IMAGINENEXT" || modeUpper == "IMAGINERANDOM")
            {
                TryImagine(nes, modeUpper == "IMAGINENEXT");
                LastBlastInfo = $"Imagine {(modeUpper=="IMAGINENEXT"?"Next":"Random")}: {writes} byte(s)";
                return;
            }
            for (int i = 0; i < writes; i++)
            {
                var d = selected[CorruptRnd.Next(selected.Count)];
                int addr = CorruptRnd.Next(d.Size);
                byte orig = 0;
                switch (d.Key)
                {
                    case "PRG": orig = nes.PeekPrg(addr); break;
                    case "PRGRAM": orig = nes.PeekPrgRam(addr); break;
                    case "CHR": orig = nes.PeekChr(addr); break;
                    case "RAM": orig = nes.PeekSystemRam(addr); break;
                }
                string mode = BlastType;
                if (mode == "RANDOMTILT") mode = CorruptRnd.Next(2) == 0 ? "RANDOM" : "TILT";
                byte newVal = orig;
                switch (mode)
                {
                    case "RANDOM": newVal = (byte)CorruptRnd.Next(256); break;
                    case "TILT": newVal = (byte)(orig + (CorruptRnd.Next(2) == 0 ? 1 : 255)); break;
                    case "NOP": newVal = 0xEA; break;
                    case "BITFLIP": int bit = CorruptRnd.Next(8); newVal = (byte)(orig ^ (1 << bit)); break;
                }
                switch (d.Key)
                {
                    case "PRG": nes.PokePrg(addr, newVal); break;
                    case "PRGRAM": nes.PokePrgRam(addr, newVal); break;
                    case "CHR": nes.PokeChr(addr, newVal); break;
                    case "RAM": nes.PokeSystemRam(addr, newVal); break;
                }
            }
            LastBlastInfo = AutoCorrupt ? $"Auto {writes} ({BlastType})/{selected.Count} domain(s)" : $"{BlastType}: {writes} writes over {selected.Count} domain(s)";
        }
        private void TryImagine(NES nes, bool next)
        {
            try
            {
                // Use NES hooks to peek PC and PRG range
                ushort pc = 0;
                try { pc = nes.GetCpuRegs().PC; } catch {}
                if (next)
                {
                    // Use emulator singleton registry (set by Emulator) to call Imagine pipeline
                    EmulatorHooks?.ImagineFromPc(pc, Math.Clamp(CorruptIntensity,1,32));
                }
                else
                {
                    // Random PRG address in $8000..$FFFF
                    int span = 0x10000 - 0x8000; int off = CorruptRnd.Next(span);
                    ushort addr = (ushort)(0x8000 + off);
                    EmulatorHooks?.ImagineFromPc(addr, Math.Clamp(CorruptIntensity,1,32));
                }
            }
            catch { }
        }
        // Bridge set by Emulator to access Imagine APIs without circular deps
        public ICorruptorEmulatorHooks? EmulatorHooks { get; set; }
        public void LetItRip()
        {
            CorruptIntensity = 1;
            foreach (var d in MemoryDomains)
            {
                d.Selected = d.Key == "PRG" || d.Key == "RAM";
            }
            if (!AutoCorrupt)
            {
                AutoCorrupt = true;
                LastBlastInfo = "Auto-corrupt enabled (Let it rip)";
            }
            else
            {
                LastBlastInfo = "Let it rip engaged";
            }
            LetItRipUsed = true;
        }
        public void DomainsChanged(IEnumerable<string> selectedKeys)
        {
            foreach (var d in MemoryDomains)
            {
                d.Selected = selectedKeys.Contains(d.Key);
            }
        }
        public void OnIntensityChange(int value)
        {
            CorruptIntensity = Math.Clamp(value, 1, 65535);
        }
        public void OnBlastTypeChanged(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                BlastType = value.Trim().ToUpperInvariant();
            }
        }

    // Glitch Harvester methods
        public void GhAddBaseState(NES nes)
        {
            var raw = nes.SaveState();
            if (string.IsNullOrEmpty(raw)) return;
            var name = string.IsNullOrWhiteSpace(GhNewBaseName) ? $"Base {GhBaseStates.Count + 1}" : GhNewBaseName.Trim();
            var b = new HarvesterBaseState { Name = name, State = raw };
            GhBaseStates.Add(b);
            GhSelectedBaseId = b.Id;
            GhNewBaseName = string.Empty;
        }
        public void GhDeleteSelectedBase()
        {
            var b = GhBaseStates.FirstOrDefault(x => x.Id == GhSelectedBaseId);
            if (b == null) return;
            GhBaseStates.Remove(b);
            if (!GhBaseStates.Any()) GhSelectedBaseId = string.Empty;
            else GhSelectedBaseId = GhBaseStates.Last().Id;
        }
        public void GhPromoteEntry(HarvestEntry e)
        {
            GhStash.Remove(e);
            e.Name = $"Entry {++GhStockpileCounter}";
            GhStockpile.Add(e);
        }
        public void GhDeleteStash(string id)
        {
            var e = GhStash.FirstOrDefault(x => x.Id == id);
            if (e != null) GhStash.Remove(e);
        }
        public void GhDeleteStock(string id)
        {
            var e = GhStockpile.FirstOrDefault(x => x.Id == id);
            if (e != null) GhStockpile.Remove(e);
            if (GhRenamingId == id) { GhRenamingId = null; GhRenameText = ""; }
        }
        public void GhClearStash()
        {
            GhStash.Clear();
        }
        public void GhBeginRename(HarvestEntry e)
        {
            GhRenamingId = e.Id;
            GhRenameText = e.Name;
        }
        public void GhCancelRename()
        {
            GhRenamingId = null;
            GhRenameText = "";
        }
        public void GhCommitRename(string id)
        {
            var e = GhStockpile.FirstOrDefault(x => x.Id == id);
            if (e != null && !string.IsNullOrWhiteSpace(GhRenameText)) e.Name = GhRenameText.Trim();
            GhCancelRename();
        }
        
        public List<BlastInstruction> GenerateBlastLayer(int writes)
        {
            var result = new List<BlastInstruction>();
            var selected = MemoryDomains.Where(d => d.Selected && d.Size > 0).ToList(); 
            if (selected.Count == 0) return result; 
            writes = Math.Clamp(writes, 1, 4096);
            
            for (int i = 0; i < writes; i++)
            {
                var d = selected[CorruptRnd.Next(selected.Count)];
                int addr = CorruptRnd.Next(d.Size);
                string mode = BlastType; 
                if (mode == "RANDOMTILT") mode = CorruptRnd.Next(2) == 0 ? "RANDOM" : "TILT"; 
                
                byte newVal = 0;
                switch (mode)
                { 
                    case "RANDOM": newVal = (byte)CorruptRnd.Next(256); break;
                    case "TILT": newVal = (byte)CorruptRnd.Next(256); break; // Will be adjusted in ApplyBlastLayer
                    case "NOP": newVal = 0xEA; break; 
                    case "BITFLIP": newVal = (byte)CorruptRnd.Next(256); break; // Will be adjusted in ApplyBlastLayer
                }
                result.Add(new BlastInstruction { Domain = d.Key, Address = addr, Value = newVal });
            }
            return result;
        }

        public void ApplyBlastLayer(IEnumerable<BlastInstruction> writes, NES nes)
        {
            if (nes == null) return; 
            foreach (var w in writes)
            { 
                try 
                { 
                    switch (w.Domain)
                    { 
                        case "PRG": nes.PokePrg(w.Address, w.Value); break; 
                        case "PRGRAM": nes.PokePrgRam(w.Address, w.Value); break; 
                        case "CHR": nes.PokeChr(w.Address, w.Value); break; 
                        case "RAM": nes.PokeSystemRam(w.Address, w.Value); break; 
                    }
                } 
                catch { } 
            }
        }
        
    public bool GhHasSelectedBase => GhBaseStates.Any(b => b.Id == GhSelectedBaseId);
    // Add more methods as needed for corruptor logic
    }
}
