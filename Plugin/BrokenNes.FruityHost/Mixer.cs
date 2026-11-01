using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

/// <summary>What the mixer actually did for one note (block-aligned): the analyser's ground truth for timing.</summary>
public sealed class NoteLog
{
    public SimNote Note = null!;
    public int StartSample, EndSample;   // note-on block start, note-off block start (sample index)
}

/// <summary>
/// FL's mixer, simplified: plays a <see cref="Scenario"/> into plugin instances block by block. BrokenNes2 is one instance per
/// channel, so a rack of instances is played together and summed, a note going to the instance on its channel.
/// Per block it does what FL does: note-ons call TriggerVoice, note-offs call Voice_Release, the live voice levels (pitch in
/// cents from C5, volume, pan) are rewritten from the slide/bend curves, each plugin gets its NewTick (which is when it hands
/// finished voices back), then Gen_Render.
/// </summary>
public sealed unsafe class Mixer
{
    public int SampleRate { get; init; } = 44100;
    public int BlockSize { get; init; } = 256;
    /// <summary>Called after every rendered block with the sample index of the block start (for register-level probes).</summary>
    public Action<int>? AfterBlock { get; init; }
    public List<NoteLog> Log { get; } = new();

    public float[] Run(PluginInstance plugin, Scenario scenario, double tailMs = 300) => Run(new[] { plugin }, scenario, tailMs);

    public float[] Run(IReadOnlyList<PluginInstance> rack, Scenario scenario, double tailMs = 300)
    {
        foreach (var plugin in rack)
        {
            foreach (var (index, value) in scenario.Params) plugin.SetParam(index, value);
            plugin.Dispatcher(Fpd.SetSampleRate, 0, SampleRate);
        }

        double samplesPerBeat = SampleRate * scenario.MsPerBeat / 1000.0;
        int total = (int)(scenario.LengthBeats * samplesPerBeat + tailMs * SampleRate / 1000.0);
        total = (total + BlockSize - 1) / BlockSize * BlockSize;
        var output = new float[total * 2];
        var tmp = new float[BlockSize * 2];

        PluginInstance Route(SimNote n) => rack.Count == 1 ? rack[0] : rack.FirstOrDefault(p => p.RackChannel == (n.Channel & 3)) ?? rack[0];

        var notes = scenario.Notes.OrderBy(n => n.StartBeat).ToList();
        var active = new List<(SimNote Note, PluginInstance Plugin, SimVoice Voice, NoteLog Log)>();
        int nextNote = 0;

        fixed (float* outPtr = output)
        fixed (float* tmpPtr = tmp)
        {
            for (int s = 0; s < total; s += BlockSize)
            {
                // note-offs first, then note-ons (a note ending exactly where the next starts hands over cleanly)
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var (n, p, v, log) = active[i];
                    if ((n.StartBeat + n.LengthBeats) * samplesPerBeat <= s + 0.5)
                    {
                        p.Host.ReleaseFromMixer(v);
                        log.EndSample = s;
                        active.RemoveAt(i);
                    }
                }
                while (nextNote < notes.Count && notes[nextNote].StartBeat * samplesPerBeat < s + BlockSize)
                {
                    var n = notes[nextNote++];
                    var p = Route(n);
                    var v = p.Host.NewVoice(0, (float)n.Velocity);
                    Update(v, n, 0);
                    v.Handle = p.TriggerVoice(v.Params, v.Tag);
                    var log = new NoteLog { Note = n, StartSample = s, EndSample = total };
                    Log.Add(log);
                    active.Add((n, p, v, log));
                }
                foreach (var (n, _, v, log) in active)
                    Update(v, n, (s - log.StartSample) / samplesPerBeat);

                for (int k = 0; k < rack.Count; k++)
                {
                    rack[k].NewTick();
                    if (k == 0) rack[k].GenRender(outPtr + s * 2, BlockSize);
                    else
                    {
                        rack[k].GenRender(tmpPtr, BlockSize);
                        for (int i = 0; i < BlockSize * 2; i++) outPtr[s * 2 + i] += tmpPtr[i];
                    }
                }
                AfterBlock?.Invoke(s);
            }
        }
        return output;
    }

    private static void Update(SimVoice v, SimNote n, double beatsIntoNote)
    {
        ref var f = ref v.Params->FinalLevels;
        f.Pitch = (float)Scenario.PitchCents(n, beatsIntoNote);
        f.Vol = (float)n.Velocity;
        f.Pan = (float)n.Pan;
        v.Params->InitLevels = f;
    }
}
