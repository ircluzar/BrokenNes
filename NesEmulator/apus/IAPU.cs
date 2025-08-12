namespace NesEmulator
{
    public interface IAPU
    {
        void Step(int cpuCycles);
        void WriteAPURegister(ushort address, byte value);
        byte ReadAPURegister(ushort address);
        float[] GetAudioSamples(int maxSamples = 0);
        int GetQueuedSampleCount();
        int GetSampleRate();
        object GetState();
        void SetState(object state);
    }
}
