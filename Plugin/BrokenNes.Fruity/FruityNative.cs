// The C++ vtable of TFruityPlug, built by hand from [UnmanagedCallersOnly] function pointers.
// On x64 Windows there is a single calling convention, so _stdcall needs no special handling.
// Anything thrown into native code would take FL down, so entry points that can fail catch and log.
using System.Runtime.InteropServices;
using System.Text;

namespace BrokenNes.Fruity;

/// <summary>Turns a managed <see cref="FruityPluginBase"/> into the native object FL expects.
/// A plugin DLL exports one function, CreatePlugInstance, and implements it with <see cref="Create"/>.</summary>
public static unsafe class FruityNative
{
    private const int NameBufferSize = 256;
    private const int VTableSlots = 21;

    private static void** vtable;
    private static readonly object gate = new();
    private static readonly Dictionary<string, nint> infos = new();

    /// <summary>Call from the DLL's exported CreatePlugInstance(host, tag).</summary>
    public static NativePlug* Create(nint host, nint tag, FruityPluginInfo info, int numParams,
                                     Func<FruityHost, nint, FruityPluginBase> factory)
    {
        try
        {
            Diag.Log($"CreatePlugInstance {info.LongName} host=0x{host:X} tag=0x{tag:X}");
            EnsureVTable();
            var native = (NativePlug*)NativeMemory.AllocZeroed((nuint)sizeof(NativePlug));
            native->VTable = vtable;
            native->HostTag = tag;
            native->Info = InfoFor(info, numParams);
            var plugin = factory(new FruityHost(host), tag);
            native->ManagedHandle = GCHandle.ToIntPtr(GCHandle.Alloc(plugin));
            return native;
        }
        catch (Exception e)
        {
            Diag.Log($"CreatePlugInstance failed: {e}");
            return null;
        }
    }

    private static TFruityPlugInfo* InfoFor(FruityPluginInfo info, int numParams)
    {
        lock (gate)
        {
            // One TFruityPlugInfo per plugin type, shared by its instances and never freed (FL keeps the pointer).
            if (infos.TryGetValue(info.LongName, out var existing)) return (TFruityPlugInfo*)existing;
            var p = (TFruityPlugInfo*)NativeMemory.AllocZeroed((nuint)sizeof(TFruityPlugInfo));
            p->SDKVersion = 1;
            p->LongName = (byte*)Marshal.StringToHGlobalAnsi(info.LongName);
            p->ShortName = (byte*)Marshal.StringToHGlobalAnsi(info.ShortName);
            p->Flags = info.Flags;
            p->NumParams = numParams;
            p->DefPoly = info.DefPoly;
            infos[info.LongName] = (nint)p;
            return p;
        }
    }

    private static void EnsureVTable()
    {
        lock (gate)
        {
            if (vtable != null) return;
            var v = (void**)NativeMemory.AllocZeroed(VTableSlots, (nuint)sizeof(void*));
            v[0] = (delegate* unmanaged<NativePlug*, void>)&DestroyObject;
            v[1] = (delegate* unmanaged<NativePlug*, nint, nint, nint, nint>)&Dispatcher;
            v[2] = (delegate* unmanaged<NativePlug*, void>)&Idle;
            v[3] = (delegate* unmanaged<NativePlug*, nint, int, void>)&SaveRestoreState;
            v[4] = (delegate* unmanaged<NativePlug*, int, int, int, byte*, void>)&GetName;
            v[5] = (delegate* unmanaged<NativePlug*, int, int, int, int>)&ProcessEvent;
            v[6] = (delegate* unmanaged<NativePlug*, int, int, int, int>)&ProcessParam;
            v[7] = (delegate* unmanaged<NativePlug*, float*, float*, int, void>)&EffRender;
            v[8] = (delegate* unmanaged<NativePlug*, float*, int*, void>)&GenRender;
            v[9] = (delegate* unmanaged<NativePlug*, TVoiceParams*, nint, nint>)&TriggerVoice;
            v[10] = (delegate* unmanaged<NativePlug*, nint, void>)&VoiceRelease;
            v[11] = (delegate* unmanaged<NativePlug*, nint, void>)&VoiceKill;
            v[12] = (delegate* unmanaged<NativePlug*, nint, nint, nint, nint, int>)&VoiceProcessEvent;
            v[13] = (delegate* unmanaged<NativePlug*, nint, float*, int*, int>)&VoiceRender;
            v[14] = (delegate* unmanaged<NativePlug*, void>)&NewTick;
            v[15] = (delegate* unmanaged<NativePlug*, void>)&MidiTick;
            v[16] = (delegate* unmanaged<NativePlug*, int*, void>)&MidiIn;
            v[17] = (delegate* unmanaged<NativePlug*, nint, void>)&MsgIn;
            v[18] = (delegate* unmanaged<NativePlug*, nint, nint, nint, nint, int>)&OutputVoiceProcessEvent;
            v[19] = (delegate* unmanaged<NativePlug*, nint, void>)&OutputVoiceKill;
            v[20] = (delegate* unmanaged<NativePlug*, uint, void*>)&ScalarDeletingDtor; // virtual ~TFruityPlug()
            vtable = v;
        }
    }

    private static FruityPluginBase P(NativePlug* self) => (FruityPluginBase)GCHandle.FromIntPtr(self->ManagedHandle).Target!;

    [UnmanagedCallersOnly]
    private static void DestroyObject(NativePlug* self)
    {
        Diag.Log("DestroyObject");
        try
        {
            var plugin = P(self);
            plugin.Editor?.Destroy();
            plugin.Editor = null;
            plugin.Destroy();
        }
        catch (Exception e) { Diag.Log($"DestroyObject exception: {e}"); }
        GCHandle.FromIntPtr(self->ManagedHandle).Free();
        NativeMemory.Free(self);
    }

    [UnmanagedCallersOnly]
    private static void* ScalarDeletingDtor(NativePlug* self, uint flags) => self;

    [UnmanagedCallersOnly]
    private static nint Dispatcher(NativePlug* self, nint id, nint index, nint value)
    {
        if (id != Fpd.SetSamplesPerTick && id != Fpd.SetFocus && id < Fpd.TestBase)
            Diag.Log($"Dispatcher id={id} index={index} value=0x{value:X}");
        try
        {
            var plugin = P(self);
            if (id == Fpd.ShowEditor)
            {
                ShowEditor(self, plugin, value);
                return 0;
            }
            return plugin.Dispatcher(id, index, value);
        }
        catch (Exception e)
        {
            Diag.Log($"Dispatcher exception: {e}");
            return 0;
        }
    }

    private static void ShowEditor(NativePlug* self, FruityPluginBase plugin, nint parent)
    {
        if (parent == 0)
        {
            plugin.Editor?.Destroy();
            plugin.Editor = null;
            self->EditorHandle = 0;
        }
        else if (plugin.Editor == null)
        {
            plugin.Editor = new FruityEditor(plugin, parent);
            self->EditorHandle = plugin.Editor.Hwnd;
        }
        else
        {
            Win32.SetParent(plugin.Editor.Hwnd, parent);
        }
    }

    [UnmanagedCallersOnly] private static void Idle(NativePlug* self) { }

    [UnmanagedCallersOnly]
    private static void SaveRestoreState(NativePlug* self, nint stream, int save)
    {
        Diag.Log($"SaveRestoreState save={save}");
        try { P(self).SaveRestoreState(new ComStream(stream), save != 0); }
        catch (Exception e) { Diag.Log($"SaveRestoreState exception: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void GetName(NativePlug* self, int section, int index, int value, byte* name)
    {
        if (name == null) return;
        string text;
        try { text = P(self).GetName(section, index, value); }
        catch (Exception e) { Diag.Log($"GetName exception: {e}"); text = ""; }
        int n = Encoding.ASCII.GetBytes(text, new Span<byte>(name, NameBufferSize - 1));
        name[n] = 0;
    }

    [UnmanagedCallersOnly]
    private static int ProcessEvent(NativePlug* self, int id, int value, int flags) => P(self).ProcessEvent(id, value, flags);

    [UnmanagedCallersOnly]
    private static int ProcessParam(NativePlug* self, int index, int value, int recFlags) => P(self).ProcessParam(index, value, recFlags);

    [UnmanagedCallersOnly] private static void EffRender(NativePlug* self, float* src, float* dst, int length) { }

    [UnmanagedCallersOnly]
    private static void GenRender(NativePlug* self, float* dest, int* length)
    {
        try { P(self).Render(dest, *length); }
        catch (Exception e) { Diag.Log($"Render exception: {e}"); new Span<float>(dest, *length * 2).Clear(); }
    }

    [UnmanagedCallersOnly]
    private static nint TriggerVoice(NativePlug* self, TVoiceParams* p, nint setTag)
    {
        try { return P(self).TriggerVoice(p, setTag); }
        catch (Exception e) { Diag.Log($"TriggerVoice exception: {e}"); return 0; }
    }

    [UnmanagedCallersOnly] private static void VoiceRelease(NativePlug* self, nint h) => P(self).ReleaseVoice(h);
    [UnmanagedCallersOnly] private static void VoiceKill(NativePlug* self, nint h) => P(self).KillVoice(h);
    [UnmanagedCallersOnly] private static int VoiceProcessEvent(NativePlug* self, nint h, nint id, nint val, nint flags) => 0;
    [UnmanagedCallersOnly] private static int VoiceRender(NativePlug* self, nint h, float* dst, int* length) => 0;

    [UnmanagedCallersOnly]
    private static void NewTick(NativePlug* self)
    {
        try { P(self).NewTick(); }
        catch (Exception e) { Diag.Log($"NewTick exception: {e}"); }
    }

    [UnmanagedCallersOnly] private static void MidiTick(NativePlug* self) { }
    [UnmanagedCallersOnly] private static void MidiIn(NativePlug* self, int* msg) { }
    [UnmanagedCallersOnly] private static void MsgIn(NativePlug* self, nint msg) { }
    [UnmanagedCallersOnly] private static int OutputVoiceProcessEvent(NativePlug* self, nint h, nint id, nint val, nint flags) => 0;
    [UnmanagedCallersOnly] private static void OutputVoiceKill(NativePlug* self, nint h) { }
}
