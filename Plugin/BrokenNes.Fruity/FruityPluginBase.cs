namespace BrokenNes.Fruity;

/// <summary>One automatable parameter. FL-native parameters are integers in [Min, Max]; FL
/// automation arrives either in that range or as 0..FromMidiMax with REC_FromMIDI.</summary>
public sealed record FruityParam(string Name, int Min, int Max, int Default, int InfoFlags, Func<int, string> Format);

/// <summary>What the host reads from the plugin object (TFruityPlugInfo).</summary>
public sealed record FruityPluginInfo(string LongName, string ShortName, int Flags = Fpf.TypeFullGen | Fpf.WantNewTick, int DefPoly = 0);

/// <summary>
/// Base class for a managed FL native plugin. Implement the voice and render methods; parameters,
/// automation translation, saved state, names and the editor are handled here.
/// <para>Threading (as in FL): <see cref="Render"/>, <see cref="NewTick"/>, voice calls and
/// <see cref="ProcessParam"/> may come from the mixer thread, the editor from the GUI thread. Parameter
/// values are plain ints read and written with Volatile; everything else belongs to the mixer thread.</para>
/// </summary>
public abstract unsafe class FruityPluginBase
{
    private const uint StateMagic = 0x504E4642; // "BFNP"
    private const int StateVersion = 1;

    protected FruityPluginBase(FruityHost host, nint hostTag, FruityParam[] parameters)
    {
        Host = host;
        HostTag = hostTag;
        Params = parameters;
        Values = new int[parameters.Length];
        for (int i = 0; i < parameters.Length; i++) Values[i] = parameters[i].Default;
    }

    public FruityHost Host { get; }
    public nint HostTag { get; }
    public FruityParam[] Params { get; }
    /// <summary>Current parameter values. Use <see cref="Get"/> / <see cref="SetFromUi"/> from threads other than the mixer.</summary>
    public int[] Values { get; }
    public FruityEditor? Editor { get; internal set; }

    public int Get(int index) => Volatile.Read(ref Values[index]);

    /// <summary>A change made in the editor: store it and tell FL, so it can record automation / "last tweaked".</summary>
    public void SetFromUi(int index, int value)
    {
        value = Math.Clamp(value, Params[index].Min, Params[index].Max);
        if (value == Get(index)) return;
        Volatile.Write(ref Values[index], value);
        OnParamChanged(index, value);
        Host.OnParamChanged(HostTag, index, value);
    }

    /// <summary>Called after a value changed (from FL automation or from the editor).</summary>
    protected virtual void OnParamChanged(int index, int value) { }

    /// <summary>Text for the editor's live readout (GUI thread; keep it cheap and tolerant of torn reads).</summary>
    public virtual string GetReadout() => "";

    // ---- host -> plugin ----

    public virtual nint Dispatcher(nint id, nint index, nint value)
    {
        if (id == Fpd.GetParamInfo && index >= 0 && index < Params.Length)
            return Params[index].InfoFlags;
        if (id >= Fpd.TestBase)
            return TestDispatcher((int)id, index, value);
        return 0;
    }

    /// <summary>Answers the test host's private questions (IDs from <see cref="Fpd.TestBase"/>); 0 by default.</summary>
    protected virtual nint TestDispatcher(int id, nint index, nint value) => 0;

    public virtual int ProcessEvent(int id, int value, int flags) => 0;

    public virtual int ProcessParam(int index, int value, int recFlags)
    {
        if ((uint)index >= (uint)Params.Length) return 0;
        var p = Params[index];
        if ((recFlags & Rec.FromMidi) != 0)
            value = p.Min + (int)Math.Round(value * (1.0 / Rec.FromMidiMax) * (p.Max - p.Min));
        if ((recFlags & Rec.UpdateValue) != 0)
        {
            value = Math.Clamp(value, p.Min, p.Max);
            Volatile.Write(ref Values[index], value);
            OnParamChanged(index, value);
        }
        else if ((recFlags & Rec.GetValue) != 0)
            value = Get(index);
        return value;
    }

    public virtual string GetName(int section, int index, int value) => section switch
    {
        Fpn.Param when (uint)index < (uint)Params.Length => Params[index].Name,
        Fpn.ParamValue when (uint)index < (uint)Params.Length => Params[index].Format(value),
        _ => "",
    };

    public virtual void SaveRestoreState(ComStream stream, bool save)
    {
        if (save)
        {
            uint magic = StateMagic;
            int version = StateVersion, count = Params.Length;
            stream.Write(&magic, 4);
            stream.Write(&version, 4);
            stream.Write(&count, 4);
            for (int i = 0; i < count; i++)
            {
                int v = Get(i);
                stream.Write(&v, 4);
            }
            return;
        }

        // Anything else in the stream (another plugin's state, a damaged project) is ignored: defaults stay.
        uint readMagic = 0;
        int readVersion = 0, readCount = 0;
        if (!stream.Read(&readMagic, 4) || readMagic != StateMagic) return;
        if (!stream.Read(&readVersion, 4) || readVersion > StateVersion) return;
        if (!stream.Read(&readCount, 4) || readCount < 0 || readCount > 4096) return;
        for (int i = 0; i < readCount; i++)
        {
            int v = 0;
            if (!stream.Read(&v, 4)) return;
            if (i < Params.Length)
            {
                v = Math.Clamp(v, Params[i].Min, Params[i].Max);
                Volatile.Write(ref Values[i], v);
                OnParamChanged(i, v);
            }
        }
    }

    public abstract nint TriggerVoice(TVoiceParams* voice, nint setTag);
    public abstract void ReleaseVoice(nint handle);
    public abstract void KillVoice(nint handle);
    public virtual void NewTick() { }
    /// <summary>Fill <paramref name="length"/> stereo frames (interleaved L,R) at the host's sample rate.</summary>
    public abstract void Render(float* dest, int length);

    /// <summary>The plugin object is going away (host called DestroyObject).</summary>
    public virtual void Destroy() { }
}
