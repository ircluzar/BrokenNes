using System;

namespace NesEmulator
{
    // Secret PPU wrapper that builds on PPU_BFR while adding a hidden post-process.
    public class PPU_EXE : IPPU
    {
        public string CoreName => "EXE";
        public string Description => "Classified.";
        public int Performance => -18;
        public int Rating => 3;
        public string Category => "Secret";

        private const int ScreenWidth = 256;
        private const int ScreenHeight = 240;
        private const int BytesPerPixel = 4;

        private readonly PPU_BFR inner;
        private byte[]? echoBuffer;
        private int phase;
        private uint noiseState = 0xA7F1C3D5u;

        public PPU_EXE(Bus bus)
        {
            inner = new PPU_BFR(bus);
        }

        public void Step(int cycles) => inner.Step(cycles);
        public byte[] GetFrameBuffer() => inner.GetFrameBuffer();

        public void UpdateFrameBuffer()
        {
            inner.UpdateFrameBuffer();
            ApplyEcho(inner.GetFrameBuffer());
        }

        public object GetState() => inner.GetState();
        public void SetState(object state) => inner.SetState(state);
        public byte ReadPPURegister(ushort address) => inner.ReadPPURegister(address);
        public void WritePPURegister(ushort address, byte value) => inner.WritePPURegister(address, value);
        public void WriteOAMDMA(byte page) => inner.WriteOAMDMA(page);

        public void GenerateStaticFrame()
        {
            inner.GenerateStaticFrame();
            ApplyEcho(inner.GetFrameBuffer());
        }

        public void ClearBuffers()
        {
            inner.ClearBuffers();
            echoBuffer = null;
            phase = 0;
            noiseState = 0xA7F1C3D5u;
        }

        private void EnsureEchoBuffer(int length, byte[] frame)
        {
            if (echoBuffer == null || echoBuffer.Length != length)
            {
                echoBuffer = new byte[length];
                Buffer.BlockCopy(frame, 0, echoBuffer, 0, length);
            }
        }

        private void ApplyEcho(byte[] frame)
        {
            if (frame == null || frame.Length != ScreenWidth * ScreenHeight * BytesPerPixel) return;

            EnsureEchoBuffer(frame.Length, frame);
            if (echoBuffer == null) return;

            int stride = ScreenWidth * BytesPerPixel;
            int localPhase = phase++;
            uint baseSeed = noiseState ^ (uint)(localPhase * 1103515245);

            for (int y = 0; y < ScreenHeight; y++)
            {
                int rowBase = y * stride;
                int srcY = y + (((localPhase >> 2) & 3) - 1);
                if (srcY < 0) srcY = 0; else if (srcY >= ScreenHeight) srcY = ScreenHeight - 1;
                int srcBase = srcY * stride;
                int shift = ((y * 5 + localPhase) & 15) - 8;
                uint n = baseSeed + (uint)(y * 0x9E3779B9u);

                for (int x = 0; x < ScreenWidth; x++)
                {
                    int sx = x + shift;
                    if (sx < 0) sx = 0; else if (sx >= ScreenWidth) sx = ScreenWidth - 1;

                    int fi = rowBase + x * BytesPerPixel;
                    int si = srcBase + sx * BytesPerPixel;

                    n = unchecked(n * 1664525u + 1013904223u);
                    int a = 24 + (int)((n >> 26) & 0x3F); // 24..87

                    byte cr = frame[fi + 0];
                    byte cg = frame[fi + 1];
                    byte cb = frame[fi + 2];

                    byte er = echoBuffer[si + 0];
                    byte eg = echoBuffer[si + 1];
                    byte eb = echoBuffer[si + 2];

                    frame[fi + 0] = (byte)((cr * (256 - a) + er * a) >> 8);
                    frame[fi + 1] = (byte)((cg * (256 - a) + eg * a) >> 8);
                    frame[fi + 2] = (byte)((cb * (256 - a) + eb * a) >> 8);
                    frame[fi + 3] = 255;

                    // Slow feedback decay with mild channel skew.
                    echoBuffer[fi + 0] = (byte)((echoBuffer[fi + 0] * 6 + cr + ((cg + cb) >> 1)) >> 3);
                    echoBuffer[fi + 1] = (byte)((echoBuffer[fi + 1] * 6 + cg + ((cr + cb) >> 1)) >> 3);
                    echoBuffer[fi + 2] = (byte)((echoBuffer[fi + 2] * 6 + cb + ((cr + cg) >> 1)) >> 3);
                    echoBuffer[fi + 3] = 255;
                }
            }

            noiseState = baseSeed ^ (uint)(localPhase * 0x85EBCA6Bu);
        }
    }
}
