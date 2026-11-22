// TAS workline: --tas render (offline replay -> MP4) on Workshop/Movies/Mp4Writer (Media Foundation H.264/AAC).
// Timestamps come from the frame count at the console's true rate and from the sample count, so the video is lag-free at any host speed.
// Output size: an integer upscale of the console picture that fits 1280x720 (NES 256x240 x3), or --mp4size WxH. AAC takes 44100/48000 Hz:
// other core rates are resampled linearly to 48000.
using System;
using System.Runtime.InteropServices;
using BrokenNes.Workshop.Movies;

namespace BrokenNes.Workshop.Tas;

internal sealed class Mp4Sink : ITasFrameSink
{
    readonly string path; readonly int ow, oh, kbps;
    Mp4Writer? w; int[] px = Array.Empty<int>(); int inRate, outRate; double phase; short l0, r0; bool have0;
    short[] rs = new short[8192];
    public long Frames => w?.Frames ?? 0;
    public double Seconds => w?.Seconds ?? 0;

    public Mp4Sink(string path, int width = 0, int height = 0, int videoKbps = 6000) { this.path = path; ow = width; oh = height; kbps = videoKbps; }

    public void Begin(int width, int height, double fps, int sampleRate)
    {
        if (w != null) return;   // a later power-on chapter: same video
        if (width < 64 || height < 64) { width = 320; height = 240; }   // no picture yet (N64 before the first VI): 4:3
        int scale = Math.Max(1, Math.Min(1280 / Math.Max(1, width), 720 / Math.Max(1, height)));
        int W = ow > 0 ? ow : width * scale, H = oh > 0 ? oh : height * scale;
        inRate = sampleRate > 0 ? sampleRate : 48000; outRate = inRate is 44100 or 48000 ? inRate : 48000;
        w = new Mp4Writer(path, W, H, (uint)Math.Round(fps * 1000), 1000, outRate, 2, kbps);
    }

    public void Frame(uint[] argb, int width, int height)
    {
        int n = width * height; if (px.Length < n) px = new int[n];
        Buffer.BlockCopy(argb, 0, px, 0, n * 4);
        w!.AddFrame(px, width, height);
    }

    public void Rate(int r) { if (r > 0) inRate = r; }   // resample from the core's current rate (output stays at the rate chosen in Begin)

    public void Audio(ReadOnlySpan<short> s)
    {
        if (w == null || s.Length < 2) return;
        if (inRate == outRate) { w.AddAudio(MemoryMarshal.AsBytes(s)); return; }
        double step = (double)inRate / outRate; int frames = s.Length / 2; int o = 0;
        int need = (int)(frames / step + 4) * 2; if (rs.Length < need) rs = new short[need];
        if (!have0) { l0 = s[0]; r0 = s[1]; have0 = true; }
        // phase = position in this call's input; -1..0 interpolates from the previous call's last sample
        while (phase < frames - 1)
        {
            int i = (int)Math.Floor(phase); double f = phase - i;
            short la = i < 0 ? l0 : s[2 * i], ra = i < 0 ? r0 : s[2 * i + 1], lb = s[2 * (i + 1)], rb = s[2 * (i + 1) + 1];
            rs[o++] = (short)(la + (lb - la) * f); rs[o++] = (short)(ra + (rb - ra) * f);
            phase += step;
        }
        phase -= frames; l0 = s[2 * (frames - 1)]; r0 = s[2 * (frames - 1) + 1];
        w.AddAudio(MemoryMarshal.AsBytes(rs.AsSpan(0, o)));
    }

    public void End() => w?.Finish();
}
