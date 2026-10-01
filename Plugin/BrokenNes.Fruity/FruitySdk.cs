// C# mirror of the FL Studio native plugin SDK (fp_plugclass.h / fp_def.h), the layout Bendy uses
// (github.com/superjoebob/bendy_redo, src/fl). Mind the #pragma pack blocks in the header: not
// every struct uses default alignment (TFruityPlugInfo is pack(4): getting that wrong crashed FL's
// engine on load).
using System.Runtime.InteropServices;

namespace BrokenNes.Fruity;

public static class Fpf
{
    public const int Generator = 1;
    public const int GetNoteInput = 1 << 4;
    public const int WantNewTick = 1 << 5;
    public const int NewVoiceParams = 1 << 21;
    public const int TypeFullGen = Generator | GetNoteInput | NewVoiceParams;
}

/// <summary>Plugin dispatcher IDs (host -> plugin).</summary>
public static class Fpd
{
    public const int ShowEditor = 0;
    public const int SetBlockSize = 3;
    public const int SetSampleRate = 4;
    public const int UseVoiceLevels = 7;
    public const int SetEnabled = 11;
    public const int SetPlaying = 12;
    public const int SetSamplesPerTick = 20;
    public const int SetFocus = 22;
    public const int GetParamInfo = 26;
    public const int PluginLoaded = 40;

    /// <summary>Test-only dispatcher IDs (never sent by FL): the test host asks the plugin about its internals.</summary>
    public const int TestBase = 0x6000;
}

/// <summary>Host dispatcher IDs (plugin -> host).</summary>
public static class Fhd
{
    public const int ParamMenu = 0;
    public const int GetParamMenuEntry = 18;
}

public static class Fpn
{
    public const int Param = 0;
    public const int ParamValue = 1;
    public const int VoiceLevel = 4;
}

/// <summary>ProcessParam flags.</summary>
public static class Rec
{
    public const int UpdateValue = 1;
    public const int GetValue = 2;
    public const int ShowHint = 4;
    public const int UpdateControl = 16;
    public const int FromMidi = 32;
    public const int FromMidiMax = 1 << 30;
}

public static class Fpe
{
    public const int Tempo = 0;
    public const int MaxPoly = 1;
    public const int MidiPan = 2;
    public const int MidiVol = 3;
    public const int MidiPitch = 4;
}

/// <summary>Voice_ProcessEvent IDs.</summary>
public static class Fpv
{
    public const int GetLength = 1;
    public const int GetColor = 2;
    public const int GetVelocity = 3;
    public const int GetRelVelocity = 4;
    public const int SetLinkVelocity = 6;
}

/// <summary>FPD_GetParamInfo result flags.</summary>
public static class Pi
{
    public const int CantInterpolate = 1;
    public const int Float = 2;
    public const int Centered = 4;
}

public static class Fhp
{
    public const int Disabled = 1;
    public const int Checked = 2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct TFruityPlugInfo
{
    public int SDKVersion;
    public byte* LongName;
    public byte* ShortName;
    public int Flags;
    public int NumParams;
    public int DefPoly;
    public int NumOutCtrls;
    public int NumOutVoices;
    public fixed int Reserved[30];
}

/// <summary>Per-voice levels, all floats. Pitch is in cents relative to C5 (MIDI 60) and already
/// includes piano-roll slides and per-note pitch automation.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TLevelParams
{
    public float Pan;   // -1..1
    public float Vol;   // 0..1 (can exceed 1)
    public float Pitch; // cents
    public float FCut;  // 0..1 (piano roll Mod X)
    public float FRes;  // 0..1 (piano roll Mod Y)
}

[StructLayout(LayoutKind.Sequential)]
public struct TVoiceParams
{
    public TLevelParams InitLevels;
    public TLevelParams FinalLevels;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TParamMenuEntry
{
    public byte* Name; // "-" means separator
    public int Flags;
}

/// <summary>Memory image of a C++ TFruityPlug object: vtable pointer, then the public fields FL reads
/// directly (HostTag, Info, EditorHandle). Our own data follows Reserved.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativePlug
{
    public void** VTable;
    public nint HostTag;
    public TFruityPlugInfo* Info;
    public nint EditorHandle;
    public int MonoRender;
    public fixed int Reserved[32];
    public nint ManagedHandle; // GCHandle to the managed plugin
}

/// <summary>Calls into the host's C++ TFruityPlugHost vtable (slot numbers = declaration order in fp_plugclass.h).</summary>
public unsafe readonly struct FruityHost(nint ptr)
{
    public nint Ptr => ptr;
    void** VTable => *(void***)ptr;

    /// <summary>Slot 0.</summary>
    public nint Dispatcher(nint sender, nint id, nint index, nint value) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)VTable[0])(ptr, sender, id, index, value);

    /// <summary>Slot 1: lets FL record automation / "last tweaked".</summary>
    public void OnParamChanged(nint sender, int index, int value) =>
        ((delegate* unmanaged<nint, nint, int, int, void>)VTable[1])(ptr, sender, index, value);

    /// <summary>Slot 5: hand a finished voice back to FL.</summary>
    public void VoiceKill(nint voiceTag, bool killHandle) =>
        ((delegate* unmanaged<nint, nint, int, void>)VTable[5])(ptr, voiceTag, killHandle ? 1 : 0);

    /// <summary>Slot 6: ask FL about a voice (colour, velocity...).</summary>
    public int VoiceProcessEvent(nint voiceTag, nint eventId, nint eventValue, nint flags) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, int>)VTable[6])(ptr, voiceTag, eventId, eventValue, flags);
}

/// <summary>The COM IStream FL passes to SaveRestoreState (vtable: QueryInterface, AddRef, Release, Read, Write, ...).</summary>
public unsafe readonly struct ComStream(nint ptr)
{
    void** VTable => *(void***)ptr;

    public bool Read(void* buffer, uint count)
    {
        uint read = 0;
        int hr = ((delegate* unmanaged<nint, void*, uint, uint*, int>)VTable[3])(ptr, buffer, count, &read);
        return hr >= 0 && read == count;
    }

    public bool Write(void* buffer, uint count)
    {
        uint written = 0;
        int hr = ((delegate* unmanaged<nint, void*, uint, uint*, int>)VTable[4])(ptr, buffer, count, &written);
        return hr >= 0 && written == count;
    }
}
