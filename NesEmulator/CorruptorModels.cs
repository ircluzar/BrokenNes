namespace BrokenNes.CorruptorModels
{
    // Models for corruptor/RTC/Glitch Harvester
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
        public string State { get; set; } = string.Empty;
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
    public class BlastInstruction
    {
        public string Domain { get; set; } = string.Empty;
        public int Address { get; set; }
        public byte Value { get; set; }
    }
    public class HarvestEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string BaseStateId { get; set; } = string.Empty;
        public List<BlastInstruction> Writes { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}
