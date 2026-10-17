using System.Text.Json;
using BrokenNes.Nano;
using NesEmulator.Plugin;

// NanoParity: the Core side of the Nano parity proof (Nano/test/parity.mjs drives it).
//
//   NanoParity replay <dir>   for every <name>.trace.json in <dir>: replay the register timeline through Core's NesApuInstrument
//                             and write <name>.core.f32 (raw little-endian float32 mono PCM)
//   NanoParity notes <dir>    for every <name>.scn.json in <dir>: run the note script through the C# reference of Nano's instrument layer on
//                             Core's NesApuInstrument; write <name>.ref.f32 and <name>.ref.trace.json (the chip writes it made)
//
// A trace is {"core":"FIX","rate":48000,"mask":15,"ops":[[0,reg,value],[1,samples],...]}: op 0 writes a register (0 = $4000 ... 0x17 = $4017),
// op 1 renders that many samples. The web module records one while it plays a script; this tool plays the same thing on the chip the FL plugin uses.

if (args.Length < 2 || args[0] is not ("replay" or "notes"))
{
    Console.Error.WriteLine("usage: NanoParity replay|notes <dir>");
    return 2;
}

static void WriteF32(string path, float[] pcm)
{
    var bytes = new byte[pcm.Length * 4];
    Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
    File.WriteAllBytes(path, bytes);
}

int done = 0;
if (args[0] == "replay")
{
    foreach (var path in Directory.GetFiles(args[1], "*.trace.json").OrderBy(p => p, StringComparer.Ordinal))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        string core = root.GetProperty("core").GetString()!;
        int rate = root.GetProperty("rate").GetInt32();
        byte mask = (byte)root.GetProperty("mask").GetInt32();
        var chip = new NesApuInstrument(core, rate, mask);

        var ops = root.GetProperty("ops");
        long total = 0;
        foreach (var op in ops.EnumerateArray()) if (op[0].GetInt32() == 1) total += op[1].GetInt32();
        var pcm = new float[total];
        int at = 0;
        foreach (var op in ops.EnumerateArray())
        {
            if (op[0].GetInt32() == 0) chip.Write(op[1].GetInt32(), (byte)op[2].GetInt32());
            else
            {
                int n = op[1].GetInt32();
                chip.Render(pcm.AsSpan(at, n));
                at += n;
            }
        }
        WriteF32(path.Replace(".trace.json", ".core.f32"), pcm);
        done++;
    }
    Console.WriteLine($"replayed {done} traces");
}
else
{
    foreach (var path in Directory.GetFiles(args[1], "*.scn.json").OrderBy(p => p, StringComparer.Ordinal))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var (engine, total) = RefEngine.FromScenario(doc.RootElement);
        var pcm = engine.Render(total);
        WriteF32(path.Replace(".scn.json", ".ref.f32"), pcm);
        File.WriteAllText(path.Replace(".scn.json", ".ref.trace.json"), JsonSerializer.Serialize(engine.Trace));
        done++;
    }
    Console.WriteLine($"ran {done} scripts through the C# reference");
}
return 0;
