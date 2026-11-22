using System;
using System.Runtime.InteropServices;

namespace BrokenNes.Workshop.Movies;

/// <summary>
/// Offline MP4 encoder (H.264 video + AAC audio) on Windows Media Foundation (built into Windows, no downloads).
/// Console-neutral: feed it whole frames (0xAARRGGBB, any size: scaled nearest into the output size, letterboxed)
/// and 16-bit stereo PCM; timestamps come from the frame count and the sample count, never the wall clock,
/// so the video is lag-free at the console's true frame rate however slow the host runs.
/// COM is called through vtable slots (IMFAttributes 3-32, IMFSample 33+, IMFMediaBuffer, IMFSinkWriter).
/// </summary>
public sealed unsafe class Mp4Writer : IDisposable
{
    public readonly int Width, Height;
    readonly uint _fpsNum, _fpsDen; readonly int _rate, _channels;
    IntPtr _writer; uint _vs, _as; long _frames, _samples; bool _done;
    readonly int[] _scaled;
    public long Frames => _frames;
    public double Seconds => (double)_frames * _fpsDen / _fpsNum;

    /// <param name="fpsNum">frame rate numerator (Win98 VGA: 25175000), <paramref name="fpsDen"/> denominator (449*800)</param>
    public Mp4Writer(string path, int width, int height, uint fpsNum, uint fpsDen, int audioRate = 44100, int channels = 2, int videoKbps = 4000, int audioKbps = 160)
    {
        Width = width & ~1; Height = height & ~1; _fpsNum = fpsNum; _fpsDen = fpsDen; _rate = audioRate; _channels = channels;
        _scaled = new int[Width * Height];
        Check(MFStartup(0x00020070, 0), "MFStartup");
        IntPtr attr; Check(MFCreateAttributes(out attr, 2), "MFCreateAttributes");
        SetU32(attr, MF_SINK_WRITER_DISABLE_THROTTLING, 1);
        SetU32(attr, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 0);   // (software MFT: the same output on every PC)
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attr, out _writer), "MFCreateSinkWriterFromURL");
        Release(attr);

        // video: H.264 out, RGB32 in
        var vo = NewType(); SetGuid(vo, MF_MT_MAJOR_TYPE, MFMediaType_Video); SetGuid(vo, MF_MT_SUBTYPE, MFVideoFormat_H264);
        SetU32(vo, MF_MT_AVG_BITRATE, (uint)videoKbps * 1000); SetU32(vo, MF_MT_INTERLACE_MODE, 2);
        SetU64(vo, MF_MT_FRAME_SIZE, Pack((uint)Width, (uint)Height)); SetU64(vo, MF_MT_FRAME_RATE, Pack(fpsNum, fpsDen)); SetU64(vo, MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        SetU32(vo, MF_MT_MPEG2_PROFILE, 100);   // High
        uint si; Check(VT<int>(_writer, 3, vo, &si), "AddStream video"); _vs = si; Release(vo);
        var vi = NewType(); SetGuid(vi, MF_MT_MAJOR_TYPE, MFMediaType_Video); SetGuid(vi, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        SetU32(vi, MF_MT_INTERLACE_MODE, 2); SetU64(vi, MF_MT_FRAME_SIZE, Pack((uint)Width, (uint)Height)); SetU64(vi, MF_MT_FRAME_RATE, Pack(fpsNum, fpsDen));
        SetU64(vi, MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1)); SetU32(vi, MF_MT_DEFAULT_STRIDE, (uint)(Width * 4));
        Check(VT2(_writer, 4, _vs, vi, IntPtr.Zero), "SetInputMediaType video"); Release(vi);

        // audio: AAC out, PCM 16-bit in
        var ao = NewType(); SetGuid(ao, MF_MT_MAJOR_TYPE, MFMediaType_Audio); SetGuid(ao, MF_MT_SUBTYPE, MFAudioFormat_AAC);
        SetU32(ao, MF_MT_AUDIO_BITS_PER_SAMPLE, 16); SetU32(ao, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audioRate); SetU32(ao, MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
        SetU32(ao, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)audioKbps * 1000 / 8);
        Check(VT<int>(_writer, 3, ao, &si), "AddStream audio (AAC wants 44100/48000, 1-2 ch, 96/128/160/192 kbps)"); _as = si; Release(ao);
        var ai = NewType(); SetGuid(ai, MF_MT_MAJOR_TYPE, MFMediaType_Audio); SetGuid(ai, MF_MT_SUBTYPE, MFAudioFormat_PCM);
        SetU32(ai, MF_MT_AUDIO_BITS_PER_SAMPLE, 16); SetU32(ai, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audioRate); SetU32(ai, MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
        SetU32(ai, MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(2 * channels)); SetU32(ai, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(2 * channels * audioRate));
        Check(VT2(_writer, 4, _as, ai, IntPtr.Zero), "SetInputMediaType audio"); Release(ai);

        Check(VT0(_writer, 5), "BeginWriting");
    }

    long FrameTime(long f) => (long)((Int128)f * 10_000_000 * _fpsDen / _fpsNum);

    /// <summary>One frame of the picture (0xAARRGGBB, w x h): nearest-scaled to fit, aspect kept, black borders.</summary>
    public void AddFrame(int[] px, int w, int h)
    {
        int[] src = _scaled;
        if (w == Width && h == Height) Array.Copy(px, _scaled, Width * Height);
        else
        {
            Array.Clear(_scaled);
            double s = Math.Min((double)Width / w, (double)Height / h); int dw = Math.Min(Width, (int)(w * s)), dh = Math.Min(Height, (int)(h * s));
            int ox = (Width - dw) / 2, oy = (Height - dh) / 2;
            for (int y = 0; y < dh; y++) { int sy = y * h / dh * w, d = (oy + y) * Width + ox; for (int x = 0; x < dw; x++) _scaled[d + x] = px[sy + x * w / dw]; }
        }
        int bytes = Width * Height * 4;
        var buf = NewBuffer(bytes, out byte* p);
        fixed (int* sp = src) Buffer.MemoryCopy(sp, p, bytes, bytes);
        Write(_vs, buf, bytes, FrameTime(_frames), FrameTime(_frames + 1) - FrameTime(_frames));
        _frames++;
    }

    /// <summary>Interleaved 16-bit PCM (little-endian bytes), any length (whole sample frames).</summary>
    public void AddAudio(ReadOnlySpan<byte> pcm)
    {
        int block = 2 * _channels; int n = pcm.Length / block * block; if (n == 0) return;
        var buf = NewBuffer(n, out byte* p);
        pcm[..n].CopyTo(new Span<byte>(p, n));
        long t0 = (long)((Int128)_samples * 10_000_000 / _rate); _samples += n / block; long t1 = (long)((Int128)_samples * 10_000_000 / _rate);
        Write(_as, buf, n, t0, t1 - t0);
    }

    IntPtr NewBuffer(int bytes, out byte* p)
    {
        Check(MFCreateMemoryBuffer((uint)bytes, out var buf), "MFCreateMemoryBuffer");
        byte* bp; uint max, cur; Check(VT<int>(buf, 3, &bp, &max, &cur), "Lock"); p = bp;
        return buf;
    }
    void Write(uint stream, IntPtr buf, int bytes, long time, long dur)
    {
        VT0(buf, 4);   // Unlock
        Check(VTu(buf, 6, (uint)bytes), "SetCurrentLength");
        Check(MFCreateSample(out var smp), "MFCreateSample");
        Check(VTp(smp, 42, buf), "AddBuffer");
        Check(VTl(smp, 36, time), "SetSampleTime"); Check(VTl(smp, 38, dur), "SetSampleDuration");
        Check(VTup(_writer, 6, stream, smp), "WriteSample");
        Release(smp); Release(buf);
    }

    public void Finish()
    {
        if (_done) return; _done = true;
        Check(VT0(_writer, 11), "Finalize");
        Release(_writer); _writer = IntPtr.Zero; MFShutdown();
    }
    public void Dispose() { try { Finish(); } catch { } }

    // ---- Media Foundation plumbing ----
    static IntPtr NewType() { Check(MFCreateMediaType(out var t), "MFCreateMediaType"); return t; }
    static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;
    static void SetU32(IntPtr a, Guid k, uint v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(a, 21))(a, &k, v), "SetUINT32");
    static void SetU64(IntPtr a, Guid k, ulong v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Slot(a, 22))(a, &k, v), "SetUINT64");
    static void SetGuid(IntPtr a, Guid k, Guid v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(a, 24))(a, &k, &v), "SetGUID");
    static IntPtr Slot(IntPtr o, int i) => (*(IntPtr**)o)[i];
    static int VT0(IntPtr o, int i) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(o, i))(o);
    static int VTu(IntPtr o, int i, uint a) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(o, i))(o, a);
    static int VTl(IntPtr o, int i, long a) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(o, i))(o, a);
    static int VTp(IntPtr o, int i, IntPtr a) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(o, i))(o, a);
    static int VTup(IntPtr o, int i, uint a, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Slot(o, i))(o, a, b);
    static int VT2(IntPtr o, int i, uint a, IntPtr b, IntPtr c) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(o, i))(o, a, b, c);
    static int VT<T>(IntPtr o, int i, IntPtr a, uint* b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Slot(o, i))(o, a, b);
    static int VT<T>(IntPtr o, int i, byte** a, uint* b, uint* c) => ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(o, i))(o, a, b, c);
    static void Release(IntPtr o) { if (o != IntPtr.Zero) Marshal.Release(o); }
    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"mp4: {what} failed 0x{hr:X8}"); }


    /// <summary>Reads an MP4 back (Media Foundation decoders): video frame count + duration, PNGs of the frames at <paramref name="grabSeconds"/>,
    /// and the decoded audio as a 16-bit WAV (for a sync check against the emulator's own WAV). Returns a one-line report.</summary>
    public static string Check(string path, double[] grabSeconds, string grabPrefix, string? wavOut)
    {
        Check(MFStartup(0x00020070, 0), "MFStartup");
        IntPtr attr; Check(MFCreateAttributes(out attr, 1), "MFCreateAttributes"); SetU32(attr, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
        Check(MFCreateSourceReaderFromURL(path, attr, out var rd), "MFCreateSourceReaderFromURL"); Release(attr);
        const uint V = 0xFFFFFFFC, A = 0xFFFFFFFD;
        var vt = NewType(); SetGuid(vt, MF_MT_MAJOR_TYPE, MFMediaType_Video); SetGuid(vt, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        Check(VT2(rd, 7, V, IntPtr.Zero, vt), "SetCurrentMediaType video"); Release(vt);
        var at = NewType(); SetGuid(at, MF_MT_MAJOR_TYPE, MFMediaType_Audio); SetGuid(at, MF_MT_SUBTYPE, MFAudioFormat_PCM); SetU32(at, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        Check(VT2(rd, 7, A, IntPtr.Zero, at), "SetCurrentMediaType audio"); Release(at);
        IntPtr cur; Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(rd, 6))(rd, V, &cur), "GetCurrentMediaType");
        ulong fs; var k = MF_MT_FRAME_SIZE; Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(cur, 8))(cur, &k, &fs), "GetUINT64"); Release(cur);
        int w = (int)(fs >> 32), h = (int)(uint)fs;
        long frames = 0, lastTs = 0, firstTs = -1, maxGap = 0, prevTs = -1; int gi = 0; var pcm = new System.IO.MemoryStream(); long aFirst = -1, aLast = -1, aBeforeLast = 0;
        foreach (var (idx, isVideo) in new[] { (V, true), (A, false) })
            while (true)
            {
                uint actual, flags; long ts; IntPtr smp;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(rd, 9))(rd, idx, 0, &actual, &flags, &ts, &smp), "ReadSample");
                if ((flags & 2) != 0 || (flags & 1) != 0) { Release(smp); break; }   // end of stream / error
                if (smp == IntPtr.Zero) continue;
                IntPtr buf; Check(VTpp(smp, 41, &buf), "ConvertToContiguousBuffer");
                byte* bp; uint max, len; Check(VT<int>(buf, 3, &bp, &max, &len), "Lock");
                if (isVideo)
                {
                    if (firstTs < 0) firstTs = ts; if (prevTs >= 0) maxGap = Math.Max(maxGap, ts - prevTs); prevTs = ts; lastTs = ts; frames++;
                    while (gi < grabSeconds.Length && ts >= grabSeconds[gi] * 1e7)
                    {
                        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                        var r = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                        int stride = (int)len / h; for (int y = 0; y < h; y++) Buffer.MemoryCopy(bp + y * stride, (byte*)r.Scan0 + y * r.Stride, w * 4, w * 4);
                        bmp.UnlockBits(r); bmp.Save($"{grabPrefix}_{grabSeconds[gi]:F1}s.png", System.Drawing.Imaging.ImageFormat.Png); gi++;
                    }
                }
                else { if (aFirst < 0) aFirst = ts; aLast = ts; aBeforeLast = pcm.Length; pcm.Write(new ReadOnlySpan<byte>(bp, (int)len)); }
                VT0(buf, 4); Release(buf); Release(smp);
            }
        Release(rd); MFShutdown();
        int rate = 44100;   // from the audio timestamps (bytes before the last buffer over its time), snapped to the AAC rates (was a fixed 44100: SM64 is 48000)
        if (aLast > aFirst && aBeforeLast > 0) { double est = aBeforeLast / 4.0 / ((aLast - aFirst) / 1e7); rate = System.Linq.Enumerable.First(System.Linq.Enumerable.OrderBy(new[] { 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000 }, x => Math.Abs(x - est))); }
        if (wavOut != null)
        {
            var d = pcm.ToArray(); using var bw = new System.IO.BinaryWriter(System.IO.File.Create(wavOut));
            bw.Write("RIFF"u8); bw.Write(36 + d.Length); bw.Write("WAVEfmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16); bw.Write("data"u8); bw.Write(d.Length); bw.Write(d);
        }
        return $"mp4check {path}: {w}x{h}, {frames} video frames, first {firstTs / 1e7:F3} s last {lastTs / 1e7:F3} s, max frame gap {maxGap / 1e4:F1} ms, audio {pcm.Length / 4} samples at {rate} Hz ({pcm.Length / 4 / (double)rate:F2} s, first ts {aFirst / 1e7:F3} s), {gi} grabs";
    }
    static int VTpp(IntPtr o, int i, IntPtr* a) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(o, i))(o, a);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSourceReaderFromURL(string url, IntPtr attr, out IntPtr reader);
    static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IntPtr attr, uint size);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint max, out IntPtr buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attr, out IntPtr writer);

    static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"),
        MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"),
        MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d"),
        MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"),
        MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71"), MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71"),
        MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71"), MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71"),
        MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71"), MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71"),
        MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0"), MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
}
