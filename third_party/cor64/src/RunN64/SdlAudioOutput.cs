// [cer64plus] Audio output through SDL2's queue API. SDL2.dll ships with the Veldrid.SDL2 package.
using System;
using System.Runtime.InteropServices;
using cor64;
using NLog;

namespace RunN64
{
    public sealed class SdlAudioOutput : IAudioOutput
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [StructLayout(LayoutKind.Sequential)]
        private struct SDL_AudioSpec
        {
            public int freq;
            public ushort format;
            public byte channels;
            public byte silence;
            public ushort samples;
            public ushort padding;
            public uint size;
            public IntPtr callback;
            public IntPtr userdata;
        }

        private const uint SDL_INIT_AUDIO = 0x00000010;
        private const ushort AUDIO_S16LSB = 0x8010;

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_InitSubSystem(uint flags);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern uint SDL_OpenAudioDevice(IntPtr device, int iscapture, ref SDL_AudioSpec desired, out SDL_AudioSpec obtained, int allowed_changes);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_CloseAudioDevice(uint dev);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_PauseAudioDevice(uint dev, int pause_on);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_QueueAudio(uint dev, IntPtr data, uint len);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern uint SDL_GetQueuedAudioSize(uint dev);

        private uint m_Dev;
        private int m_Rate;
        private const int MaxQueuedMs = 250;

        public static SdlAudioOutput TryCreate()
        {
            try {
                if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0) { Log.Warn("SDL audio init failed; sound disabled"); return null; }
                return new SdlAudioOutput();
            }
            catch (Exception e) {
                Log.Warn("SDL2 not available, sound disabled: " + e.Message);
                return null;
            }
        }

        private void Open(int rate)
        {
            if (m_Dev != 0) SDL_CloseAudioDevice(m_Dev);
            var want = new SDL_AudioSpec { freq = rate, format = AUDIO_S16LSB, channels = 2, samples = 1024 };
            m_Dev = SDL_OpenAudioDevice(IntPtr.Zero, 0, ref want, out _, 0);
            m_Rate = rate;
            if (m_Dev != 0) {
                SDL_PauseAudioDevice(m_Dev, 0);
                Log.Info("Audio output: SDL2, {0} Hz stereo", rate);
            }
        }

        public unsafe void Write(short[] interleavedStereo, int frames, int rate)
        {
            if (rate != m_Rate) Open(rate);
            if (m_Dev == 0) return;
            // Drop audio rather than build an ever-growing delay while emulation runs below real time.
            uint queued = SDL_GetQueuedAudioSize(m_Dev);
            if (queued > (uint)(m_Rate * 4 * MaxQueuedMs / 1000)) return;
            fixed (short* p = interleavedStereo) SDL_QueueAudio(m_Dev, (IntPtr)p, (uint)(frames * 4));
        }
    }
}
