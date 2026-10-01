// Loads a native Fruity plugin DLL and calls into it through the vtable, exactly as FL does.
using System.Runtime.InteropServices;
using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

/// <summary>A plugin DLL: its CreatePlugInstance export.</summary>
public sealed unsafe class PluginLibrary : IDisposable
{
    private readonly nint module;
    private readonly delegate* unmanaged<nint, nint, NativePlug*> create;

    public string Path { get; }

    public PluginLibrary(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        module = NativeLibrary.Load(Path);
        create = (delegate* unmanaged<nint, nint, NativePlug*>)NativeLibrary.GetExport(module, "CreatePlugInstance");
    }

    public bool HasExport(string name) => NativeLibrary.TryGetExport(module, name, out _);

    public NativePlug* CreateRaw(HostSim host, nint tag) => create(host.Ptr, tag);

    public PluginInstance Create(HostSim? host = null, nint tag = 0x77)
    {
        bool owns = host == null;
        host ??= new HostSim();
        var p = CreateRaw(host, tag);
        if (p == null) throw new InvalidOperationException("CreatePlugInstance returned null");
        return new PluginInstance(p, host, ownsHost: owns);
    }

    public void Dispose() { /* kept loaded for the life of the process: a Native AOT DLL cannot be unloaded cleanly */ }
}

/// <summary>One plugin instance, driven the way FL drives it.</summary>
public sealed unsafe class PluginInstance : IDisposable
{
    private readonly NativePlug* p;
    private readonly void** vt;
    private bool destroyed;

    internal PluginInstance(NativePlug* plug, HostSim host, bool ownsHost)
    {
        p = plug;
        vt = plug->VTable;
        Host = host;
        host.Plugin = this;
        OwnsHost = ownsHost;
    }

    public HostSim Host { get; }
    public bool OwnsHost { get; }
    public nint EditorHandle => p->EditorHandle;
    public nint HostTag => p->HostTag;

    // ---- the TFruityPlugInfo block FL reads straight from the object ----
    public int SdkVersion => p->Info->SDKVersion;
    public string LongName => Marshal.PtrToStringAnsi((nint)p->Info->LongName) ?? "";
    public string ShortName => Marshal.PtrToStringAnsi((nint)p->Info->ShortName) ?? "";
    public int Flags => p->Info->Flags;
    public int NumParams => p->Info->NumParams;
    public bool InfoPointerStable => p->Info != null;

    // ---- calls (slot numbers: declaration order in fp_plugclass.h) ----
    public nint Dispatcher(int id, nint index = 0, nint value = 0) =>
        ((delegate* unmanaged<NativePlug*, nint, nint, nint, nint>)vt[1])(p, id, index, value);

    public int ProcessEvent(int id, int value, int flags = 0) =>
        ((delegate* unmanaged<NativePlug*, int, int, int, int>)vt[5])(p, id, value, flags);

    public int ProcessParam(int index, int value, int flags) =>
        ((delegate* unmanaged<NativePlug*, int, int, int, int>)vt[6])(p, index, value, flags);

    public string GetName(int section, int index, int value)
    {
        byte* buf = stackalloc byte[256];
        new Span<byte>(buf, 256).Fill(0xCC);   // a plugin that forgets the terminator shows up as garbage
        ((delegate* unmanaged<NativePlug*, int, int, int, byte*, void>)vt[4])(p, section, index, value, buf);
        return Marshal.PtrToStringAnsi((nint)buf) ?? "";
    }

    public int GetParam(int index) => ProcessParam(index, 0, Rec.GetValue);
    public void SetParam(int index, int value) => ProcessParam(index, value, Rec.UpdateValue);
    public void SetParamFromAutomation(int index, double normalized) =>
        ProcessParam(index, (int)(normalized * Rec.FromMidiMax), Rec.UpdateValue | Rec.FromMidi);

    public nint TriggerVoice(TVoiceParams* voice, nint setTag) =>
        ((delegate* unmanaged<NativePlug*, TVoiceParams*, nint, nint>)vt[9])(p, voice, setTag);

    public void VoiceRelease(nint handle) => ((delegate* unmanaged<NativePlug*, nint, void>)vt[10])(p, handle);
    public void VoiceKill(nint handle) => ((delegate* unmanaged<NativePlug*, nint, void>)vt[11])(p, handle);
    public void NewTick() => ((delegate* unmanaged<NativePlug*, void>)vt[14])(p);

    /// <summary>Gen_Render: <paramref name="frames"/> stereo frames into <paramref name="interleaved"/> (L,R,L,R...).</summary>
    public void GenRender(float* interleaved, int frames)
    {
        int len = frames;
        ((delegate* unmanaged<NativePlug*, float*, int*, void>)vt[8])(p, interleaved, &len);
    }

    public void Idle() => ((delegate* unmanaged<NativePlug*, void>)vt[2])(p);

    // ---- state, through a fake IStream ----
    public byte[] SaveState()
    {
        using var s = new FakeStream();
        ((delegate* unmanaged<NativePlug*, nint, int, void>)vt[3])(p, s.Ptr, 1);
        return s.Bytes();
    }

    public void LoadState(byte[] data)
    {
        using var s = new FakeStream(data);
        ((delegate* unmanaged<NativePlug*, nint, int, void>)vt[3])(p, s.Ptr, 0);
    }

    // ---- editor ----
    public void ShowEditor(nint parent) => Dispatcher(Fpd.ShowEditor, 0, parent);
    public void HideEditor() => Dispatcher(Fpd.ShowEditor, 0, 0);

    // ---- test hooks answered by BrokenNes2 (Fpd.TestBase + n) ----
    public long Test(int n, nint index = 0) => Dispatcher(Fpd.TestBase + n, index);

    public void Destroy()
    {
        if (destroyed) return;
        destroyed = true;
        ((delegate* unmanaged<NativePlug*, void>)vt[0])(p);
        Host.Plugin = null;
        if (OwnsHost) Host.Dispose();
    }

    public void Dispose() => Destroy();
}

/// <summary>A COM IStream backed by memory, as FL passes to SaveRestoreState (QueryInterface, AddRef, Release, Read, Write at slots 0-4).</summary>
internal sealed unsafe class FakeStream : IDisposable
{
    private static readonly Dictionary<nint, FakeStream> Live = new();
    private static void** vtable;
    private readonly MemoryStream mem;
    private readonly nint self;

    public nint Ptr => self;

    public FakeStream(byte[]? initial = null)
    {
        if (vtable == null)
        {
            var v = (void**)NativeMemory.AllocZeroed(16, (nuint)sizeof(void*));
            v[3] = (delegate* unmanaged<nint, byte*, uint, uint*, int>)&Read;
            v[4] = (delegate* unmanaged<nint, byte*, uint, uint*, int>)&Write;
            vtable = v;
        }
        mem = initial == null ? new MemoryStream() : new MemoryStream(initial, writable: false);
        self = (nint)NativeMemory.AllocZeroed(16);
        *(void***)self = vtable;
        lock (Live) Live[self] = this;
    }

    public byte[] Bytes() => mem.ToArray();

    [UnmanagedCallersOnly]
    private static int Read(nint self, byte* buffer, uint count, uint* read)
    {
        FakeStream? s; lock (Live) Live.TryGetValue(self, out s);
        if (s == null) return unchecked((int)0x80004005);
        int n = s.mem.Read(new Span<byte>(buffer, (int)count));
        if (read != null) *read = (uint)n;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int Write(nint self, byte* buffer, uint count, uint* written)
    {
        FakeStream? s; lock (Live) Live.TryGetValue(self, out s);
        if (s == null) return unchecked((int)0x80004005);
        s.mem.Write(new ReadOnlySpan<byte>(buffer, (int)count));
        if (written != null) *written = count;
        return 0;
    }

    public void Dispose()
    {
        lock (Live) Live.Remove(self);
        NativeMemory.Free((void*)self);
    }
}
