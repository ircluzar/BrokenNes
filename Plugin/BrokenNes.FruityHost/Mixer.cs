using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

/// <summary>What the mixer actually did for one note (block-aligned): the analyser's ground truth for timing.</summary>
public sealed class NoteLog
{
    public SimNote Note = null!;
    public int StartSample, EndSample;   // note-on block start, note-off block start (sample index)
}

/// <summary>
/// FL's mixer, simplified: plays a <see cref="Scenario"/> into a plugin instance block by block.
/// Per block it does what FL does: note-ons call TriggerVoice, note-offs call Voice_Release, the live
/// voice levels (pitch in cents from C5, volume, pan) are rewritten from the slide/bend curves, the
/// plugin gets its NewTick (which is when it hands finished voices back), then Gen_Render.
/// </summary>
public sealed unsafe class Mixer
{
    public int SampleRate { get; init; } = 44100;
    public int BlockSize { get; init; } = 256;
    /// <summary>Called after every rendered block with the sample index of the block start (for register-level probes).</summary>
    public Action<int>? AfterBlock { get; init; }
    public List<NoteLog> Log { get; } = new();

    public float[] Run(PluginInstance plugin, Scenario scenario, double tailMs = 300)
    {
        foreach (var (index, value) in scenario.Params) plugin.SetParam(index, value);
        plugin.Dispatcher(Fpd.SetSampleRate, 0, SampleRate);

        double samplesPerBeat = SampleRate * scenario.MsPerBeat / 1000.0;
        int total = (int)(scenario.LengthBeats * samplesPerBeat + tailMs * SampleRate / 1000.0);
        total = (total + BlockSize - 1) / BlockSize * BlockSize;
        var output = new float[total * 2];

        var notes = scenario.Notes.OrderBy(n => n.StartBeat).ToList();
        var active = new List<(SimNote Note, SimVoice Voice, NoteLog Log)>();
        int nextNote = 0;

        fixed (float* outPtr = output)
        {
            for (int s = 0; s < total; s += BlockSize)
            {
                // note-offs first, then note-ons (a note ending exactly where the next starts hands over cleanly)
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var (n, v, log) = active[i];
                    if ((n.StartBeat + n.LengthBeats) * samplesPerBeat <= s + 0.5)
                    {
                        plugin.Host.ReleaseFromMixer(v);
                        log.EndSample = s;
                        active.RemoveAt(i);
                    }
                }
                while (nextNote < notes.Count && notes[nextNote].StartBeat * samplesPerBeat < s + BlockSize)
                {
                    var n = notes[nextNote++];
                    var v = plugin.Host.NewVoice(n.Color, (float)n.Velocity);
                    Update(v, n, 0);
                    v.Handle = plugin.TriggerVoice(v.Params, v.Tag);
                    var log = new NoteLog { Note = n, StartSample = s, EndSample = total };
                    Log.Add(log);
                    active.Add((n, v, log));
                }
                foreach (var (n, v, log) in active)
                    Update(v, n, (s - log.StartSample) / samplesPerBeat);

                plugin.NewTick();
                plugin.GenRender(outPtr + s * 2, BlockSize);
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
