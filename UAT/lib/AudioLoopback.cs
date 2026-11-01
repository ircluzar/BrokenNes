// Compiled by UAT/entrypoints/audio-pacing-desktop-smoke.ps1 (Add-Type, referencing the NAudio assemblies next to the app under test).
// Three jobs, none needing a download:
//   ProcessLoopbackRecorder  records what ONE process (and its children) plays, as the audio engine gets it (WASAPI process
//                     loopback, Windows 10 2004+): other programs on the same device - the user's own BrokenNes windows - are left out.
//   LoopbackRecorder  records what the default playback device really plays (WASAPI loopback: the mix after every app, the
//                     same samples the speakers get) to a float WAV. The fallback, and the "is the device quiet" check.
//   GapAnalyzer       counts DROPOUTS in a recording: runs of exact digital silence 0.5-30 ms long inside sound. A device that
//                     is fed too late (an underrun) or a driver that pads a short buffer with silence (WinMM on the Focusrite,
//                     the 2026-10-29 bug) makes exactly these; game sound almost never does, which the headless Workshop render
//                     of the same ROM shows (its count is the yardstick).
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

public sealed class ProcessLoopbackRecorder : IDisposable
{
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    private sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public int Hr; public object Iface;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { op.GetActivateResult(out Hr, out Iface); Done.Set(); }
    }

    // Every COM call is made on one MTA thread of our own: the caller (pwsh) may be STA, where the activated object cannot be cast.
    private AudioClient client;
    private AudioCaptureClient capture;
    private readonly WaveFileWriter writer;
    private readonly WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private readonly Thread thread;
    private readonly int processId;
    private readonly ManualResetEvent ready = new ManualResetEvent(false), go = new ManualResetEvent(false);
    private Exception failure;
    private volatile bool running = true;
    public long Bytes, SilentPackets, Packets;
    public string Format { get { return format.ToString() + " (process loopback)"; } }

    public ProcessLoopbackRecorder(int processId, string path)
    {
        this.processId = processId;
        writer = new WaveFileWriter(path, format);
        thread = new Thread(Run) { IsBackground = true, Name = "process loopback" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!ready.WaitOne(8000) && failure == null) failure = new TimeoutException("process loopback did not start");
        if (failure != null) { running = false; go.Set(); try { writer.Dispose(); } catch { } throw failure; }
    }

    private void Open()
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK (1), TargetProcessId, ProcessLoopbackMode = INCLUDE_TARGET_PROCESS_TREE (0) }
        IntPtr p = Marshal.AllocHGlobal(12);
        Marshal.WriteInt32(p, 0, 1); Marshal.WriteInt32(p, 4, processId); Marshal.WriteInt32(p, 8, 0);
        // PROPVARIANT, VT_BLOB (65): vt at 0, cbSize at 8, pBlobData at 8 + pointer alignment
        IntPtr pv = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
        Marshal.WriteInt16(pv, 0, 65); Marshal.WriteInt32(pv, 8, 12); Marshal.WriteIntPtr(pv, IntPtr.Size == 8 ? 16 : 12, p);
        var h = new Handler();
        try
        {
            IActivateAudioInterfaceAsyncOperation op;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, pv, h, out op);
            if (!h.Done.WaitOne(5000)) throw new TimeoutException("process loopback activation timed out");
            if (h.Hr != 0) Marshal.ThrowExceptionForHR(h.Hr);
        }
        finally { Marshal.FreeHGlobal(pv); Marshal.FreeHGlobal(p); }
        client = new AudioClient((IAudioClient)h.Iface);
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 2000000, 0, format, Guid.Empty);
        capture = client.AudioCaptureClient;
    }

    private void Run()
    {
        try { Open(); } catch (Exception ex) { failure = ex; ready.Set(); return; }
        ready.Set();
        go.WaitOne();
        if (!running) { try { client.Dispose(); } catch { } return; }
        int block = format.BlockAlign;
        var bytes = new byte[0];
        try
        {
            client.Start();
            while (running)
            {
                Thread.Sleep(5);
                while (capture.GetNextPacketSize() > 0)
                {
                    int frames; AudioClientBufferFlags flags;
                    IntPtr data = capture.GetBuffer(out frames, out flags);
                    int n = frames * block;
                    if (bytes.Length < n) bytes = new byte[n];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) { Array.Clear(bytes, 0, n); SilentPackets++; }
                    else Marshal.Copy(data, bytes, 0, n);
                    capture.ReleaseBuffer(frames);
                    writer.Write(bytes, 0, n); Bytes += n; Packets++;
                }
            }
            client.Stop();
        }
        catch (Exception ex) { failure = ex; }
        finally { try { client.Dispose(); } catch { } }
    }

    public void Start() { go.Set(); }

    public void Stop()
    {
        running = false; go.Set();
        thread.Join(3000);
        writer.Flush();
        if (failure != null) throw failure;
    }

    public void Dispose() { running = false; go.Set(); try { writer.Dispose(); } catch { } }
}

public sealed class LoopbackRecorder : IDisposable
{
    private readonly WasapiLoopbackCapture cap;
    private readonly WaveFileWriter writer;
    private readonly object gate = new object();
    public long Bytes;
    public string Format;

    public LoopbackRecorder(string path)
    {
        cap = new WasapiLoopbackCapture();
        Format = cap.WaveFormat.ToString();
        writer = new WaveFileWriter(path, cap.WaveFormat);
        cap.DataAvailable += (s, e) => { lock (gate) { writer.Write(e.Buffer, 0, e.BytesRecorded); Bytes += e.BytesRecorded; } };
    }

    public void Start() { cap.StartRecording(); }

    public void Stop()
    {
        cap.StopRecording();
        System.Threading.Thread.Sleep(200);
        lock (gate) writer.Flush();
    }

    public void Dispose()
    {
        try { cap.Dispose(); } catch { }
        lock (gate) { try { writer.Dispose(); } catch { } }
    }
}

public sealed class GapReport
{
    public double Seconds, AudibleSeconds;
    public int Dropouts;
    public double DropoutMsTotal, LongestDropoutMs;
    public int SampleRate, Channels;
    public double DropoutsPerAudibleSecond { get { return AudibleSeconds > 0 ? Dropouts / AudibleSeconds : 0; } }
    public override string ToString()
    {
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F1} s ({1:F1} s audible), {2} dropouts ({3:F2}/audible s, {4:F0} ms in all, longest {5:F1} ms) @ {6} Hz x{7}",
            Seconds, AudibleSeconds, Dropouts, DropoutsPerAudibleSecond, DropoutMsTotal, LongestDropoutMs, SampleRate, Channels);
    }
}

public static class GapAnalyzer
{
    /// <summary>Counts dropouts in channel 0 of <paramref name="path"/>. With <paramref name="quantizeGain"/> &gt; 0 every sample
    /// is first scaled by it and truncated to 16 bits, as the app does before the device (so a reference render is judged by the
    /// same zero the app's output has). <paramref name="skipSeconds"/> leaves out the start.</summary>
    public static GapReport Analyze(string path, double quantizeGain, double skipSeconds)
    {
        using (var wr = new WaveFileReader(path))
        {
            ISampleProvider r = wr.ToSampleProvider();
            int rate = r.WaveFormat.SampleRate, ch = r.WaveFormat.Channels;
            int minRun = Math.Max(1, rate / 2000);          // 0.5 ms
            int maxRun = rate * 30 / 1000;                  // 30 ms: longer silence is the game being quiet, not a dropout
            int win = rate / 100;                           // 10 ms windows for "audible"
            long skip = (long)(skipSeconds * rate);
            var buf = new float[4096 * ch];
            long frame = 0; int run = 0; bool seenSound = false;
            int winPos = 0; float winPeak = 0; long audibleWins = 0, totalWins = 0;
            var rep = new GapReport { SampleRate = rate, Channels = ch };
            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i + ch - 1 < n; i += ch, frame++)
                {
                    if (frame < skip) continue;
                    double x = buf[i];
                    bool zero;
                    if (quantizeGain > 0) { zero = (int)(x * quantizeGain * 32767.0) == 0; x *= quantizeGain; }   // (short)(x * 32767) truncates, as AudioManager.AddPcm does
                    else zero = Math.Abs(x) < 1e-7;
                    float ax = (float)Math.Abs(x);
                    if (ax > winPeak) winPeak = ax;
                    if (++winPos == win) { totalWins++; if (winPeak > 1e-4f) audibleWins++; winPos = 0; winPeak = 0; }
                    if (zero) { run++; continue; }
                    if (run > 0 && seenSound && run >= minRun && run <= maxRun)
                    {
                        rep.Dropouts++;
                        double ms = run * 1000.0 / rate;
                        rep.DropoutMsTotal += ms;
                        if (ms > rep.LongestDropoutMs) rep.LongestDropoutMs = ms;
                    }
                    run = 0; seenSound = true;
                }
            }
            rep.Seconds = totalWins / 100.0;
            rep.AudibleSeconds = audibleWins / 100.0;
            return rep;
        }
    }
}
