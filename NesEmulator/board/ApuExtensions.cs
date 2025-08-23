using System;

namespace NesEmulator
{
    internal static class ApuExtensions
    {
        // Fill up to span.Length samples from the APU into the provided span.
        // Returns the number of samples written. Remaining entries (if any) are zeroed by the caller if needed.
        public static int FillAudioSamples(this IAPU apu, Span<float> destination)
        {
            if (destination.Length <= 0) return 0;
            var pulled = apu.GetAudioSamples(destination.Length);
            int n = pulled.Length;
            if (n > 0)
            {
                if (n > destination.Length) n = destination.Length;
                pulled.AsSpan(0, n).CopyTo(destination);
            }
            return n;
        }
    }
}
