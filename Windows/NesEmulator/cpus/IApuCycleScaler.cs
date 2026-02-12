namespace NesEmulator
{
    // Optional interface for CPU cores that scale APU cycles independently.
    public interface IApuCycleScaler
    {
        int ScaleApuCycles(int cpuCycles);
    }
}
