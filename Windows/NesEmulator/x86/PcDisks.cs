// Boot disks for the x86 console: a DOS program wrapped in a FreeDOS boot disk. Written for BrokenNes.
// FreeDOS itself is never part of this code or of the repository: the base image is a separate file (the FreeDOS 1.4 LiteUSB image, GPL-2.0
// and others) that the desktop release carries beside the app with its licence texts and source offer (Windows/x86/freedos/README.txt).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NesEmulator.X86;

public static class PcDisks
{
    /// <summary>The FreeDOS 1.4 LiteUSB image's file name (a 32 MB FAT16 hard disk with the kernel, FreeCOM and \FREEDOS\BIN).</summary>
    public const string FreeDosImageName = "FD14LITE.img";

    public const string DefaultConfig = "!FILES=40\r\n!BUFFERS=20\r\n!LASTDRIVE=Z\r\nDOS=HIGH\r\nSHELL=\\COMMAND.COM \\ /E:1024 /P=\\FDAUTO.BAT\r\n";
    public const string DefaultAutoexec = "@ECHO OFF\r\nSET DOSDIR=\\FREEDOS\r\nSET PATH=%DOSDIR%\\BIN\r\nVER\r\n";

    /// <summary>
    /// Where the FreeDOS base image is: <c>BROKENNES_FREEDOS</c> (a file), else <c>x86\freedos\FD14LITE.img</c> beside the app (a release carries it there), else the developer toolkit's copy
    /// (<c>C:\BrokenNes-tools\dos\freedos</c>). Null when there is none: a DOS program then cannot be opened, a disk image still can.
    /// </summary>
    public static string? FindFreeDos()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_FREEDOS") is { Length: > 0 } env && File.Exists(env)) return env;
        foreach (var p in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "x86", "freedos", FreeDosImageName),
                     Path.Combine(@"C:\BrokenNes-tools\dos\freedos", FreeDosImageName),
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>A FreeDOS boot disk from <paramref name="baseImage"/>: our FDCONFIG.SYS / FDAUTO.BAT replace the installer's (it boots to a prompt, or runs a program), SETUP.BAT goes,
    /// <paramref name="drop"/> trees are removed and <paramref name="puts"/> files added.</summary>
    public static byte[] BuildDisk(byte[] baseImage, string config, string autoexec, IEnumerable<(byte[] data, string dos)> puts, IEnumerable<string>? drop = null)
    {
        var img = (byte[])baseImage.Clone();
        var fat = new FatImage(img);
        fat.WriteFile("FDCONFIG.SYS", Encoding.ASCII.GetBytes(Crlf(config)));
        fat.WriteFile("FDAUTO.BAT", Encoding.ASCII.GetBytes(Crlf(autoexec)));
        fat.Delete("SETUP.BAT");
        foreach (var d in drop ?? Array.Empty<string>()) fat.DeleteTree(d);
        foreach (var (data, dos) in puts) fat.WriteFile(dos, data);
        return img;
    }

    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>True for a DOS program: a .COM by its name, or an MZ executable that is not a Windows (PE / NE) one. A DOS-extender program (DOS/32A, DOS4GW: an LE / LX inside) counts.</summary>
    public static bool IsDosProgram(byte[] data, string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".com") return data.Length is > 0 and <= 65280;
        if (ext != ".exe" || data.Length < 64 || data[0] != 'M' || data[1] != 'Z') return false;
        int lfanew = BitConverter.ToInt32(data, 0x3c);
        if (lfanew > 0 && lfanew + 2 <= data.Length && BitConverter.ToUInt16(data, 0x18) >= 0x40)
        {
            char a = (char)data[lfanew], b = (char)data[lfanew + 1];
            if ((a == 'P' && b == 'E') || (a == 'N' && b == 'E')) return false;   // Windows
        }
        return true;
    }

    /// <summary>
    /// A boot disk that runs one DOS program: FreeDOS from <paramref name="freeDos"/>, the program in the root of C: (its 8.3 name; a name FreeDOS uses there gets a G in front), FDAUTO.BAT
    /// starting it; when it returns, the DOS prompt. Whatever the program writes (its save file) stays on this disk, which the app keeps as the game's save.
    /// </summary>
    /// <param name="env">Extra FDAUTO.BAT lines run before the program (the sound card's SET BLASTER / ULTRASND, <see cref="PcSound.DosEnv"/>).</param>
    public static byte[] ForProgram(byte[] freeDos, byte[] program, string fileName, string env = "")
    {
        string name = DosName(fileName);
        string stem = name.Contains('.') ? name[..name.IndexOf('.')] : name, ext = name.Contains('.') ? name[name.IndexOf('.')..] : "";
        if (new FatImage((byte[])freeDos.Clone()).Exists(name) || stem is "FDAUTO" or "FDCONFIG" or "SETUP") name = "G" + stem[..Math.Min(7, stem.Length)] + ext;
        string auto = "@ECHO OFF\r\nSET DOSDIR=\\FREEDOS\r\nSET PATH=%DOSDIR%\\BIN\r\n" + env + "\\" + name + "\r\n";   // with its path: a name like BEEP is never taken for a shell command
        return BuildDisk(freeDos, DefaultConfig, auto, new[] { (program, name) }, new[] { "PACKAGES" });
    }

    /// <summary>The 8.3 upper-case DOS name of a host file name (letters, digits and _-~ kept, others become _).</summary>
    public static string DosName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName).TrimStart('.');
        static string Clean(string s, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToUpperInvariant()) { if (sb.Length == max) break; sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '~' ? c : '_'); }
            return sb.ToString();
        }
        string a = Clean(stem, 8), b = Clean(ext, 3);
        if (a.Length == 0) a = "GAME";
        return b.Length > 0 ? a + "." + b : a;
    }
}
