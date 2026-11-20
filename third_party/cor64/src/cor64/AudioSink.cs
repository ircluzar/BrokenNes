// [cer64plus] The APU slot's low-level core: the AI DAC. The AI (RCP/Audio.cs) clocks samples out of its DMA FIFO
// against guest time and pushes each one here. This core buffers them for ReadSamples (the session/host pull) and,
// as tooling, can forward blocks to a live output device and capture a WAV. One instance per machine (was a static
// AudioSink, which would have mixed the sound of two sessions in one process).
using System;
using System.IO;
using cor64.Cores;

namespace cor64
{
    public interface IAudioOutput
    {
        void Write(short[] interleavedStereo, int frames, int rate);
    }

    public sealed class AiDac : IN64ApuCore
    {
        private const int Block = 512;
        private readonly short[] m_Buf = new short[Block * 2];
        private readonly byte[] m_Bytes = new byte[Block * 4];
        private int m_N;
        private int m_Rate = 32000;
        private FileStream m_Wav;
        private long m_WavFrames;
        private long m_WavLimitFrames;

        // samples waiting for ReadSamples
        private short[] m_Out = new short[48000 * 2];
        private int m_OutCount;

        public string Id => N64Cores.ApuAi;
        public string Description => "AI DAC (low-level; the RSP runs the audio microcode)";

        /// <summary>Optional live output (RunN64); blocks of 512 frames.</summary>
        public IAudioOutput Output;
        /// <summary>When false, nothing is kept for ReadSamples (hosts that only use Output or the WAV).</summary>
        public bool Capture = true;
        public long FramesPushed, NonSilentFrames;
        public int PeakAbs;

        public int SampleRate => m_Rate;

        public void OpenWav(string path, int seconds)
        {
            m_Wav = new FileStream(path, FileMode.Create, FileAccess.Write);
            m_Wav.Write(new byte[44], 0, 44);
            m_WavLimitFrames = seconds * 48000L;
        }

        public void Push(short l, short r, int rate)
        {
            FramesPushed++;
            if (l != 0 || r != 0) {
                NonSilentFrames++;
                int a = Math.Max(Math.Abs((int)l), Math.Abs((int)r));
                if (a > PeakAbs) PeakAbs = a;
            }
            m_Rate = rate;
            m_Buf[m_N * 2] = l;
            m_Buf[m_N * 2 + 1] = r;
            m_N++;
            if (m_N == Block) Flush();
        }

        // State boundaries: before a save, deliver the partial block; after a load, drop it (it belongs to the old timeline).
        public void FlushPending() { if (m_N > 0) Flush(); }
        public void DiscardPending() { m_N = 0; m_OutCount = 0; }

        public int ReadSamples(Span<short> dest)
        {
            int n = Math.Min(dest.Length & ~1, m_OutCount);
            m_Out.AsSpan(0, n).CopyTo(dest);
            Array.Copy(m_Out, n, m_Out, 0, m_OutCount - n);
            m_OutCount -= n;
            return n;
        }

        public bool TryHandleTask(in N64Task task) => false;

        private void Flush()
        {
            try { Output?.Write(m_Buf, m_N, m_Rate); } catch { Output = null; }

            if (Capture) {
                int n = m_N * 2;
                if (m_OutCount + n > m_Out.Length) Array.Resize(ref m_Out, Math.Max(m_Out.Length * 2, m_OutCount + n));
                Array.Copy(m_Buf, 0, m_Out, m_OutCount, n);
                m_OutCount += n;
            }

            if (m_Wav != null && m_WavFrames < m_WavLimitFrames) {
                Buffer.BlockCopy(m_Buf, 0, m_Bytes, 0, m_N * 4);
                m_Wav.Write(m_Bytes, 0, m_N * 4);
                m_WavFrames += m_N;
                WriteWavHeader();
            }
            m_N = 0;
        }

        private void WriteWavHeader()
        {
            long pos = m_Wav.Position;
            int dataBytes = (int)(m_WavFrames * 4);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)2);
            w.Write(m_Rate);
            w.Write(m_Rate * 4);
            w.Write((short)4);
            w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(dataBytes);
            m_Wav.Position = 0;
            m_Wav.Write(ms.ToArray(), 0, 44);
            m_Wav.Position = pos;
            m_Wav.Flush();
        }
    }
}
