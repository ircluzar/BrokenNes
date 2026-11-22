using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NesEmulator.X86;

// nano98 package schema 2 (docs/projects/nano98-format.md, 2026-11-21): the standard drive layout.
//   C: = the shared seat base (Windows + launcher; read-only, the machine's writes land in its overlay), optionally embedded in the package (base/C.n98d)
//   D: = the game drive: the package's own sparse image (drives/D.n98d), always the standard size (StandardDriveMb), so every package boots on
//        the same machine; the CD only when the game needs it.
//   The game's registry / INI changes and its few files on C: are applied by the launcher at run time (command port, after "N98R"), so Windows
//   boots on the plain base and ONE warm-boot state (taken at the launcher's N98 BOOT, D: = the blank standard drive, no disc) fits every package:
//   after it the host puts the package's D: in the slot (Windows has only read sectors that are the same on every standard drive, see WarmDrive notes).
public sealed partial class N98Seat
{
    /// <summary>The standard game drive's size (MB): the blank drive every warm state is taken with, and the size of every schema-2 D: image.</summary>
    public const int StandardDriveMb = 2000;
    public bool V2;
    /// <summary>Schema 2: the package's game drive (D:), put in the slot at the warm point.</summary>
    public byte[]? GameDrive;
    /// <summary>Schema 2: the blank standard drive the machine boots with (Windows mounts D: from it before the launcher's N98 BOOT).</summary>
    public byte[]? BlankDrive;
    /// <summary>Schema 2: files the package puts on C: (system DLLs...), sent through the command port like the saves.</summary>
    public readonly List<(string guest, byte[] data)> SystemFiles = new();
    /// <summary>Schema 2: the "N98R" block (registry + INI ops) the launcher applies before the game starts.</summary>
    public byte[] SystemOps = Array.Empty<byte>();
    /// <summary>Schema 2: where the warm-boot state of this base lives (one per base + emulator build, shared by every package).</summary>
    public string WarmDir = "";

    private static int SchemaOf(byte[] pkg)
    {
        try
        {
            using var z = new ZipArchive(new MemoryStream(pkg), ZipArchiveMode.Read);
            var me = z.GetEntry("manifest.json");
            if (me == null) return 1;
            using var doc = JsonDocument.Parse(me.Open());
            return doc.RootElement.TryGetProperty("schema", out var sc) ? sc.GetInt32() : 1;
        }
        catch (InvalidDataException) { return 1; }
    }

    private static N98Seat PrepareV2(byte[] pkg, string fileName, N98Seat s)
    {
        s.V2 = true;
        using var z = new ZipArchive(new MemoryStream(pkg), ZipArchiveMode.Read);
        using var doc = JsonDocument.Parse(z.GetEntry("manifest.json")!.Open());
        var r = doc.RootElement;
        int schema = r.GetProperty("schema").GetInt32();
        if (schema > 2) throw new InvalidDataException($"{fileName}: package schema {schema} is newer than this app knows (2)");
        s.Title = r.TryGetProperty("title", out var t) ? t.GetString() ?? s.Id : s.Id;
        if (r.TryGetProperty("saves", out var sv) && sv.ValueKind == JsonValueKind.Array)
            s.SavePatterns = sv.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 3).ToArray();
        string hw = r.TryGetProperty("hardware", out var h) && h.TryGetProperty("machine", out var mm) ? mm.GetString() ?? "" : "";
        s.Machine = hw.Contains("VOODOO", StringComparison.OrdinalIgnoreCase) ? "win98-p166-voodoo-nano" : "win98-p166-nano";
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_MACHINE") is { Length: > 0 } mo) s.Machine = mo;
        if (r.TryGetProperty("cd", out var cds) && cds.ValueKind == JsonValueKind.Array)
            foreach (var cd in cds.EnumerateArray())
                if (cd.TryGetProperty("ref", out var rf) && rf.GetString() is { Length: > 0 } name)
                    foreach (var dir in CdDirs(s.CacheDir))
                        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name))) { s.CdPath ??= Path.Combine(dir, name); break; }

        // C: the embedded base (extracted once into the cache) or the shared one
        string baseImg;
        if (z.GetEntry("base/C.n98d") is { } eb && Environment.GetEnvironmentVariable("BROKENNES_N98_SHAREDBASE") != "1")
        {
            baseImg = Path.Combine(s.CacheDir, "base.n98d");
            if (!File.Exists(baseImg) || new FileInfo(baseImg).Length != eb.Length)
            {
                using (var o = File.Create(baseImg + ".tmp")) using (var i = eb.Open()) i.CopyTo(o);
                File.Move(baseImg + ".tmp", baseImg, true);
            }
        }
        else baseImg = FindBase();
        s.ImagePath = baseImg;
        s.Image = DiskImage.ReadImageFile(baseImg);
        string baseHash = DiskImage.HashOf(s.Image);
        s.WarmDir = Path.Combine(Path.GetDirectoryName(s.CacheDir) is { Length: > 0 } pd ? pd : s.CacheDir, "warm", baseHash.Replace("p256:", "")[..16] + "-" + EmulatorVersion);
        // a warm state shipped with the base (build_seat.py: BASE.n98d.warm\<hash>-<build>) is used where it is
        string shipped = Path.Combine(baseImg + ".warm", Path.GetFileName(s.WarmDir));
        if (File.Exists(Path.Combine(shipped, "warm.bin"))) s.WarmDir = shipped;
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_WARMDIR") is { Length: > 0 } wd) s.WarmDir = wd;

        // D: the package's game drive, and the blank standard drive the machine boots with
        var dEntry = z.GetEntry("drives/D.n98d") ?? throw new InvalidDataException($"{fileName}: a schema-2 package without its game drive (drives/D.n98d)");
        using (var ds = dEntry.Open()) s.GameDrive = DiskImage.ReadSparse(ds, fileName + ":D");
        if (s.GameDrive.LongLength != (long)StandardDriveMb << 20) throw new InvalidDataException($"{fileName}: the game drive is {s.GameDrive.LongLength} bytes, not the standard {StandardDriveMb} MB");
        s.BlankDrive = MakeBlankDrive(StandardDriveMb);
        // the blank drive's identity hash (what the warm state names it by): cached beside the warm state, made once in the background
        try
        {
            string bh = Path.Combine(s.WarmDir, "blank.p256");
            if (File.Exists(bh) && File.ReadAllText(bh).Trim() is { } bt && bt.StartsWith("p256:", StringComparison.Ordinal)) DiskImage.SeedHash(s.BlankDrive, bt);
            else { var b = s.BlankDrive; System.Threading.Tasks.Task.Run(() => { string hh = DiskImage.HashOf(b); try { Directory.CreateDirectory(s.WarmDir); File.WriteAllText(bh, hh); } catch (IOException) { } }); }
        }
        catch (IOException) { }
        s.Image2 = s.BlankDrive;

        // the launcher's command (no Python needed for schema 2)
        var L = r.GetProperty("launch");
        string W(string p) => p.Replace('/', '\\');
        var cmd = new StringBuilder("N98CMD1\r\n");
        cmd.Append("run=").Append(W(L.GetProperty("exe").GetString()!)).Append("\r\n");
        string args = L.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_PRESET") is { Length: > 0 } pn && r.TryGetProperty("presets", out var ps) && ps.TryGetProperty(pn, out var pr)
            && pr.TryGetProperty("args", out var pa)) args = (args + " " + pa.GetString()).Trim();
        if (args.Length > 0) cmd.Append("args=").Append(args).Append("\r\n");
        if (L.TryGetProperty("cwd", out var cw) && cw.GetString() is { Length: > 0 } cwd) cmd.Append("dir=").Append(W(cwd)).Append("\r\n");
        cmd.Append("exit=poweroff\r\n");
        s.Command = Encoding.Latin1.GetBytes(cmd.ToString());

        // C: files + registry + INI, applied by the launcher
        if (r.TryGetProperty("c_files", out var cf))
            foreach (var f in cf.EnumerateArray())
            {
                var e = z.GetEntry(f.GetProperty("zip").GetString()!) ?? throw new InvalidDataException($"{fileName}: missing {f.GetProperty("zip").GetString()}");
                using var es = e.Open(); var ms = new MemoryStream(); es.CopyTo(ms);
                s.SystemFiles.Add((W(f.GetProperty("path").GetString()!), ms.ToArray()));
            }
        s.SystemOps = BuildSystemOps(r);

        string savesRoot = Environment.GetEnvironmentVariable("BROKENNES_SAVES_DIR") is { Length: > 0 } sr ? sr
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes");
        s.SavesDir = Path.Combine(savesRoot, "N98Saves", "n98" + s.Id, "files");
        return s;
    }

    /// <summary>"N98R" + records (see n98launch.c): registry ops (root 1 = HKLM from SYSTEM.DAT, 2 = HKEY_USERS from USER.DAT), INI edits, 0.</summary>
    private static byte[] BuildSystemOps(JsonElement r)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        void S(string? x) { if (x == null) { w.Write(0xffffffffu); return; } var b = Encoding.Latin1.GetBytes(x); w.Write((uint)b.Length); w.Write(b); }
        w.Write(0x5238394Eu);
        if (r.TryGetProperty("registry", out var reg))
            foreach (var hive in reg.EnumerateObject())
            {
                byte root = hive.Name.StartsWith("USER", StringComparison.OrdinalIgnoreCase) ? (byte)2 : (byte)1;
                foreach (var op in hive.Value.GetProperty("ops").EnumerateArray())
                {
                    string kind = op.GetProperty("op").GetString()!, key = op.GetProperty("key").GetString()!;
                    switch (kind)
                    {
                        case "key": w.Write((byte)1); w.Write(root); S(key); break;
                        case "set":
                            w.Write((byte)2); w.Write(root); S(key); S(op.GetProperty("name").GetString());
                            w.Write((uint)op.GetProperty("type").GetInt32());
                            var data = Convert.FromHexString(op.GetProperty("data").GetString() ?? ""); w.Write((uint)data.Length); w.Write(data); break;
                        case "delval": w.Write((byte)3); w.Write(root); S(key); S(op.GetProperty("name").GetString()); break;
                        case "delkey": w.Write((byte)4); w.Write(root); S(key); break;
                    }
                }
            }
        if (r.TryGetProperty("ini", out var ini))
            foreach (var f in ini.EnumerateArray())
                foreach (var op in f.GetProperty("ops").EnumerateArray())
                {
                    w.Write((byte)5); S(f.GetProperty("path").GetString()!.Replace('/', '\\'));
                    S(op.GetProperty("section").GetString()); S(op.GetProperty("key").GetString());
                    S(op.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null);
                }
        w.Write((byte)0); w.Flush();
        return ms.ToArray();
    }

    /// <summary>The blank standard game drive (byte-identical to tools/nano98/pkg/fat32.py mkfs): MBR + one FAT32 LBA partition at sector 63,
    /// 4 KB clusters, fixed volume serial, FSInfo free count and next-free "unknown", label NANO98GAME, the reserved file + the empty GAME folder (what Windows reads at mount is the same on every game drive).</summary>
    public static byte[] MakeBlankDrive(int sizeMb)
    {
        const int Sec = 512, Part = 63, Rsvd = 32, NFats = 2, Spc = 8; const uint Serial = 0x4E393844;
        long total = (long)sizeMb * 1024 * 1024 / Sec; long psec = total - Part;
        long fatsz = 1;
        while (true) { long nclus = (psec - Rsvd - NFats * fatsz) / Spc; long need = ((nclus + 2) * 4 + Sec - 1) / Sec; if (need <= fatsz) break; fatsz = need; }
        var d = new byte[total * Sec];
        byte[] Chs(long lba) { long c = lba / (255 * 63), rr = lba % (255 * 63), hh = rr / 63, ss = rr % 63; c = Math.Min(c, 1023); return new[] { (byte)hh, (byte)(((c >> 2) & 0xc0) | (ss + 1)), (byte)(c & 0xff) }; }
        d[446] = 0; Chs(Part).CopyTo(d, 447); d[450] = 0x0c; Chs(total - 1).CopyTo(d, 451);
        BitConverter.GetBytes((uint)Part).CopyTo(d, 454); BitConverter.GetBytes((uint)psec).CopyTo(d, 458);
        BitConverter.GetBytes(Serial).CopyTo(d, 440); d[510] = 0x55; d[511] = 0xaa;
        var label = Encoding.ASCII.GetBytes("NANO98GAME ");
        var bs = new byte[Sec];
        bs[0] = 0xeb; bs[1] = 0x58; bs[2] = 0x90; Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(bs, 3);
        var bw = new BinaryWriter(new MemoryStream(bs)); bw.Seek(11, SeekOrigin.Begin);
        bw.Write((ushort)Sec); bw.Write((byte)Spc); bw.Write((ushort)Rsvd); bw.Write((byte)NFats); bw.Write((ushort)0); bw.Write((ushort)0); bw.Write((byte)0xF8);
        bw.Write((ushort)0); bw.Write((ushort)63); bw.Write((ushort)255); bw.Write((uint)Part); bw.Write((uint)psec);
        bw.Write((uint)fatsz); bw.Write((ushort)0); bw.Write((ushort)0); bw.Write(2u); bw.Write((ushort)1); bw.Write((ushort)6);
        bw.Seek(64, SeekOrigin.Begin); bw.Write((byte)0x80); bw.Write((byte)0); bw.Write((byte)0x29); bw.Write(Serial); bw.Write(label); bw.Write(Encoding.ASCII.GetBytes("FAT32   ")); bw.Flush();
        bs[510] = 0x55; bs[511] = 0xaa;
        var fi = new byte[Sec];
        Encoding.ASCII.GetBytes("RRaA").CopyTo(fi, 0); Encoding.ASCII.GetBytes("rrAa").CopyTo(fi, 484);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(fi, 488); BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(fi, 492); fi[510] = 0x55; fi[511] = 0xaa;
        foreach (int b0 in new[] { 0, 6 })
        {
            bs.CopyTo(d, (Part + b0) * Sec); fi.CopyTo(d, (Part + b0 + 1) * Sec);
            d[(Part + b0 + 2) * Sec + 510] = 0x55; d[(Part + b0 + 2) * Sec + 511] = 0xaa;
        }
        // the standard layout (fat32.py mkfs): FAT sector 0 = root (2), N98RSVD.SYS (3..127), GAME (128); root = label, N98RSVD.SYS, GAME
        var fat = new byte[Sec * 2];
        BitConverter.GetBytes(0x0FFFFFF8u).CopyTo(fat, 0); BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fat, 4); BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fat, 8);
        for (int c = 3; c < 127; c++) BitConverter.GetBytes((uint)(c + 1)).CopyTo(fat, c * 4);
        BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fat, 127 * 4); BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fat, 128 * 4);
        long data = Part + Rsvd + NFats * fatsz;
        // FAT sector 408 and the cluster of data sector 3598978 (both read by Windows at mount): bad clusters on every standard drive
        long probe = (3598978 - data) / Spc + 2;
        for (int k = 0; k < NFats; k++)
        {
            long fl = (Part + Rsvd + k * fatsz) * Sec;
            fat.CopyTo(d, fl);
            for (int i = 0; i < Sec; i += 4) BitConverter.GetBytes(0x0FFFFFF7u).CopyTo(d, fl + 408 * Sec + i);
            BitConverter.GetBytes(0x0FFFFFF7u).CopyTo(d, fl + probe * 4);
        }
        void Ent(long at, string name, byte attr, uint cl, uint size)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(d, at); d[at + 11] = attr;
            BitConverter.GetBytes((ushort)0x21).CopyTo(d, at + 16); BitConverter.GetBytes((ushort)0x21).CopyTo(d, at + 18);
            BitConverter.GetBytes((ushort)(cl >> 16)).CopyTo(d, at + 20); BitConverter.GetBytes((ushort)0x21).CopyTo(d, at + 24);
            BitConverter.GetBytes((ushort)(cl & 0xffff)).CopyTo(d, at + 26); BitConverter.GetBytes(size).CopyTo(d, at + 28);
        }
        long root = data * Sec; label.CopyTo(d, root); d[root + 11] = 0x08;
        Ent(root + 32, "N98RSVD SYS", 0x07, 3, 125u * Spc * Sec); Ent(root + 64, "GAME       ", 0x10, 128, 0);
        long g = (data + (128 - 2) * Spc) * Sec; Ent(g, ".          ", 0x10, 128, 0); Ent(g + 32, "..         ", 0x10, 0, 0);
        return d;
    }
}
