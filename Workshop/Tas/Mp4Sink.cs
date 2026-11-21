// TAS workline: --tas render (offline replay -> MP4). Placeholder until the Media Foundation encoder lands.
using System;
namespace BrokenNes.Workshop.Tas;
internal sealed class Mp4Sink : ITasFrameSink
{
    public Mp4Sink(string path) => throw new NotSupportedException("--tas render: the MP4 encoder is not built yet (use --frames-dir)");
    public void Begin(int w, int h, double fps, int rate) { }
    public void Frame(uint[] argb, int w, int h) { }
    public void Audio(ReadOnlySpan<short> s) { }
    public void End() { }
}
