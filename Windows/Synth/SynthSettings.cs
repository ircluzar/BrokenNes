using System;
using System.IO;
using System.Text.Json;
using BrokenNes.SynthHost;

namespace BrokenNes.Windows.Synth
{
    /// <summary>
    /// What the standalone synth remembers: which devices, how MIDI is routed, the computer-keyboard switch (<c>synth.json</c>), and what the four
    /// plugin instances hold (<c>synth-state.bin</c>: sound chip, mode, game, volume... the same state FL stores in a project).
    /// Both live in %AppData%\BrokenNes beside the emulator's config.json. <c>BROKENNES_SYNTH_DIR</c> moves them (tests use it, so they never touch yours).
    /// </summary>
    internal sealed class SynthSettings
    {
        /// <summary>Friendly name of the audio output; empty means the system default.</summary>
        public string AudioDevice { get; set; } = "";
        /// <summary>Product name of the MIDI input; empty means the first one found, "none" means no MIDI input.</summary>
        public string MidiDevice { get; set; } = "";
        public MidiRouting Routing { get; set; } = MidiRouting.ByMidiChannel;
        /// <summary>The computer keyboard plays notes (A W S E D F T G Y H U J K O L P ;, Z / X shift the octave).</summary>
        public bool ComputerKeyboard { get; set; } = true;
        public int Octave { get; set; }
        public int Tab { get; set; }

        public const string NoMidi = "none";

        public static string Directory =>
            Environment.GetEnvironmentVariable("BROKENNES_SYNTH_DIR") is { Length: > 0 } d
                ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes");

        public static string SettingsPath => Path.Combine(Directory, "synth.json");
        public static string StatePath => Path.Combine(Directory, "synth-state.bin");

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static SynthSettings Load()
        {
            try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<SynthSettings>(File.ReadAllText(SettingsPath), Json) ?? new(); }
            catch (Exception) { /* a damaged settings file is the same as none: the defaults are fine */ }
            return new();
        }

        public void Save() => WriteAtomically(SettingsPath, File.WriteAllText, JsonSerializer.Serialize(this, Json));

        // ---- the plugin instances' state: "BN2S", count, then (length, bytes) per instance ----

        public static byte[][]? LoadStates()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                using var r = new BinaryReader(File.OpenRead(StatePath));
                if (r.ReadInt32() != 0x53324E42) return null;   // "BN2S"
                int n = r.ReadInt32();
                if (n is < 1 or > 16) return null;
                var states = new byte[n][];
                for (int i = 0; i < n; i++)
                {
                    int len = r.ReadInt32();
                    if (len is < 0 or > 64 << 20) return null;
                    states[i] = r.ReadBytes(len);
                }
                return states;
            }
            catch (Exception) { return null; }   // unreadable state: the instances start from their defaults
        }

        public static void SaveStates(byte[][] states)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(0x53324E42); w.Write(states.Length);
                foreach (var s in states) { w.Write(s.Length); w.Write(s); }
            }
            WriteAtomically(StatePath, File.WriteAllBytes, ms.ToArray());
        }

        /// <summary>Forgets the saved instance state (Reset settings): the next start is a fresh synth.</summary>
        public static void ForgetStates() { try { File.Delete(StatePath); } catch (Exception) { } }

        // write beside the target, then rename over it: a crash mid-write cannot leave half a file
        private static void WriteAtomically<T>(string path, Action<string, T> write, T content)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            write(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
