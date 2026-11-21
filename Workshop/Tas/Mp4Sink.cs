// TAS workline: --tas render (offline replay -> MP4) on the Media Foundation writer (Movies/Mp4Writer.cs).
using System;
using System.Runtime.InteropServices;
using BrokenNes.Workshop.Movies;
namespace BrokenNes.Workshop.Tas;
internal sealed class Mp4Sink : ITasFrameSink
{
    readonly string _path; Mp4Writer? _w; int[] _buf = Array.Empty<int>();
    public Mp4Sink(string path) => _path = path;
    public void Begin(int w, int h, double fps, int rate)
    {
        // exact rationals for the common rates, else millihertz
        (uint n, uint d) = Math.Abs(fps - 25175000.0 / 359200) < 1e-3 ? (25175000u, 359200u) : ((uint)Math.Round(fps * 1000), 1000u);
        int sw = w, sh = h; while (sw < 480) { sw *= 2; sh *= 2; }   // small consoles: integer nearest upscale
        _w = new Mp4Writer(_path, sw, sh, n, d, rate is 44100 or 48000 ? rate : 48000);
    }
    public void Frame(uint[] argb, int w, int h) { if (_buf.Length != argb.Length) _buf = new int[argb.Length]; Buffer.BlockCopy(argb, 0, _buf, 0, argb.Length * 4); _w!.AddFrame(_buf, w, h); }
    public void Audio(ReadOnlySpan<short> s) => _w!.AddAudio(MemoryMarshal.AsBytes(s));
    public void End() => _w?.Finish();
}
