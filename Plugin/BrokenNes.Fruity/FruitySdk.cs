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
    /// <summary>FPF_MIDIOut: "plugin will send MIDI out messages (only those will be enabled when rendering to a MIDI file)".</summary>
    public const int MidiOut = 1 << 14;
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
    /// <summary>FPD_SongPosChanged: "song position has been relocated (by other means than by playing of course)". FL 2026 sends it at every Play and Stop too (the host log of 2026-10-18).</summary>
    public const int SongPosChanged = 13;
    public const int SetSamplesPerTick = 20;
    public const int SetFocus = 22;
    public const int GetParamInfo = 26;
    /// <summary>FPD_RoutingChanged: "mixer routing changed ... See RCV_ constants for the meaning of the Value parameter" (RCV_NamesChanged = 1: "only one or more track names have changed").</summary>
    public const int RoutingChanged = 25;
    /// <summary>FPD_ProjLoaded: "called after a project has been loaded".</summary>
    public const int ProjLoaded = 27;
    /// <summary>FPD_PluginLoaded: "done opening the plugin - note that SaveRestoreState is called before this!"</summary>
    public const int PluginLoaded = 40;
    /// <summary>FPD_ContextInfoChanged: "Index holds the type of information (see CI_ constants), call FHD_GetContextInfo for the new value(s)".</summary>
    public const int ContextInfoChanged = 41;

    /// <summary>Test-only dispatcher IDs (never sent by FL): the test host asks the plugin about its internals.</summary>
    public const int TestBase = 0x6000;
}

/// <summary>Host dispatcher IDs (plugin -> host).</summary>
public static class Fhd
{
    public const int ParamMenu = 0;
    /// <summary>FHD_EditorResized (fp_plugclass.h): "to notify the host that the editor (EditorHandle) has been resized". The plugin resizes its own editor
    /// window first, then calls Dispatcher(HostTag, EditorResized, 0, 0); FL then fits its frame around the editor window.</summary>
    public const int EditorResized = 2;
    /// <summary>FHD_ActivateMIDI: "makes the host enable its MIDI output, useful when a MIDI out plugin is created".</summary>
    public const int ActivateMidi = 4;
    public const int GetParamMenuEntry = 18;
    /// <summary>FHD_GetMixingTime: "get mixer time, Index is the time format required (see GT_... constants)"; Value is a pointer to a <see cref="TFPTime"/>, "filled with an optional offset in samples".</summary>
    public const int GetMixingTime = 36;
    /// <summary>FHD_GetPlaybackTime: the playback time, the same way.</summary>
    public const int GetPlaybackTime = 37;
    /// <summary>FHD_GetContextInfo: "get information about various things. Index is the information type (see CI_ constants), Value and result depend on the type"; the header's note on the CI_ block:
    /// "requested info is returned as the result of the function". For <see cref="Ci.TrackName"/> the result is a pointer to a UTF-8 string FL owns (0: none). Which "track" a generator gets (its
    /// channel or the mixer track it feeds) the header does not say.</summary>
    public const int GetContextInfo = 76;
}

/// <summary>Context information types for <see cref="Fhd.GetContextInfo"/> and <see cref="Fpd.ContextInfoChanged"/> (fp_plugclass.h, CI_*).</summary>
public static class Ci
{
    /// <summary>CI_TrackName: "(R/W) PAnsiChar encoded as UTF-8".</summary>
    public const int TrackName = 0;
    /// <summary>CI_TrackIndex: "(R)".</summary>
    public const int TrackIndex = 1;
}

/// <summary>Time formats for <see cref="Fhd.GetMixingTime"/> (fp_plugclass.h, GT_*).</summary>
public static class Gt
{
    public const int Beats = 0, AbsoluteMs = 1, RunningMs = 2, MsSinceStart = 3, Ticks = 4;
    public const int TimeFormatMask = 0xFF;
    /// <summary>GT_LocalTime: "time relative to song start" (a flag).</summary>
    public const int LocalTime = 1 << 31;
}

/// <summary>TFPTime (fp_plugclass.h): two doubles.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TFPTime
{
    public double T, T2;
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
    /// <summary>FPV_GetLength: "retrieve length in ticks (not reliable) in Result (-1 if undefined)".</summary>
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

    /// <summary>Slot 9: MIDIOut_Delayed, "delayed MIDI out message (see TMIDIOutMsg) (will be sent once the MIDI tick has reached the current mixer tick)". <paramref name="msg"/> is a
    /// TMIDIOutMsg in an integer: Status | Data1 &lt;&lt; 8 | Data2 &lt;&lt; 16 | Port &lt;&lt; 24. Slots 7 and 8 (LockMix_Old, UnlockMix_Old) come before it.</summary>
    public void MidiOutDelayed(nint sender, nint msg) =>
        ((delegate* unmanaged<nint, nint, nint, void>)VTable[9])(ptr, sender, msg);

    /// <summary>Slot 10: MIDIOut, "direct MIDI out message" (same packing as <see cref="MidiOutDelayed"/>).</summary>
    public void MidiOut(nint sender, nint msg) =>
        ((delegate* unmanaged<nint, nint, nint, void>)VTable[10])(ptr, sender, msg);
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
