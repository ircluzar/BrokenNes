namespace BrokenNes.Models
{
    // General models for NES controller and UI
    public record ShaderOption(string Key, string Label);
    public class RomOption
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }
    }
    public class UploadedRom
    {
        public string name { get; set; } = string.Empty;
        public string base64 { get; set; } = string.Empty;
    }
    public class BenchHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string Rom { get; set; } = "";
        public string CpuCore { get; set; } = "";
        public string PpuCore { get; set; } = "";
        public string ApuCore { get; set; } = "";
        public string Display { get; set; } = "";
    }
    public record TimelinePoint(DateTime When, double MsPerIter, long Reads, long Writes, long Apu, long Oam, string CpuCore, string PpuCore, string ApuCore, string Rom);
    public record DiffRow(string Name, double CurMs, double PrevMs, double DeltaMs, double DeltaPct, long ReadsDelta, long WritesDelta, long ApuDelta, long OamDelta);
    public record HoverTooltip(string Target, string TimeLabel, double MsPerIter, long Reads, long Writes, long ApuCycles, long OamWrites, string CpuCore, string PpuCore, string ApuCore, string Rom);
}
