using System.Reflection;
using System.Text.Json;
using NesEmulator.Sega;

// NanoSmsParity: the C# side of Nano SMS's parity proof (Nano/sms/test/parity.mjs drives it).
//
//   NanoSmsParity replay <dir>   every <name>.trace.json ({"rate":48000,"clock":3579545.0,"ops":[[0,v] PSG write,[2,v] stereo write,[1,cpuClocks] Run,[3,frames] read]})
//                                through Core's Sn76489 (variant Sega) -> <name>.core.f32: left then right, float32 = the chip's 16-bit sample / 32768
//   NanoSmsParity kernel <file>  BandLimitedMixer's kernel (32 phases x 16 taps, float32) -> <file>

if (args.Length < 2 || args[0] is not ("replay" or "kernel")) { Console.Error.WriteLine("usage: NanoSmsParity replay <dir> | kernel <file>"); return 2; }

if (args[0] == "kernel")
{
    var k = (float[,])typeof(BandLimitedMixer).GetField("Kernel", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var flat = new float[k.Length]; Buffer.BlockCopy(k, 0, flat, 0, k.Length * 4);
    var bytes = new byte[flat.Length * 4]; Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
    File.WriteAllBytes(args[1], bytes);
    return 0;
}

int done = 0;
foreach (var path in Directory.GetFiles(args[1], "*.trace.json").OrderBy(p => p, StringComparer.Ordinal))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    int rate = doc.RootElement.GetProperty("rate").GetInt32();
    double clock = doc.RootElement.GetProperty("clock").GetDouble();
    var ops = doc.RootElement.GetProperty("ops");
    long total = 0;
    foreach (var op in ops.EnumerateArray()) if (op[0].GetInt32() == 3) total += op[1].GetInt32();
    var L = new float[total]; var R = new float[total]; int at = 0;
    var psg = new Sn76489(clock, rate, PsgVariant.Sega);
    var buf = new short[2 * 8192];
    foreach (var op in ops.EnumerateArray())
    {
        int kind = op[0].GetInt32(), v = op[1].GetInt32();
        switch (kind)
        {
            case 0: psg.Write((byte)v); break;
            case 2: psg.WriteStereo((byte)v); break;
            case 1: psg.Run(v); break;
            case 3:
                {
                    int got = psg.ReadSamples(buf.AsSpan(0, 2 * v));
                    if (got != 2 * v) throw new InvalidOperationException($"{Path.GetFileName(path)}: asked for {v} frames, the chip had {got / 2}");
                    for (int i = 0; i < v; i++) { L[at] = buf[2 * i] / 32768f; R[at] = buf[2 * i + 1] / 32768f; at++; }
                    break;
                }
        }
    }
    var bytes = new byte[(L.Length + R.Length) * 4];
    Buffer.BlockCopy(L, 0, bytes, 0, L.Length * 4);
    Buffer.BlockCopy(R, 0, bytes, L.Length * 4, R.Length * 4);
    File.WriteAllBytes(path.Replace(".trace.json", ".core.f32"), bytes);
    done++;
}
Console.WriteLine($"replayed {done}");
return 0;
