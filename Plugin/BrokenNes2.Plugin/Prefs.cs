using System.Runtime.InteropServices;

namespace BrokenNes2;

/// <summary>
/// The plugin's remembered settings, per Windows user, not per project: whether the About window has been seen, and the input mappings.
/// They live under <c>HKEY_CURRENT_USER\Software\Bogue\BrokenNes2</c>. For tests, the environment variable BROKENNES_PREFS_FILE names a text
/// file (one <c>name=value</c> per line) that is used instead, so a test run never touches the registry. Nothing here can fail loudly:
/// a setting that cannot be read is simply absent, one that cannot be written is simply not remembered.
/// </summary>
internal static unsafe partial class Prefs
{
    private const string RegistryPath = @"Software\Bogue\BrokenNes2";
    private static readonly object gate = new();

    public static string? Get(string name)
    {
        lock (gate)
        {
            try
            {
                var file = Environment.GetEnvironmentVariable("BROKENNES_PREFS_FILE");
                if (!string.IsNullOrEmpty(file)) return ReadFile(file).GetValueOrDefault(name);
                if (RegCreateKeyEx(HkeyCurrentUser, RegistryPath, 0, null, 0, KeyAllAccess, 0, out nint key, out _) != 0) return null;
                try
                {
                    uint type = 0, size = 0;
                    if (RegQueryValueEx(key, name, 0, &type, null, &size) != 0 || size == 0) return null;
                    var buf = new byte[size + 2];
                    fixed (byte* p = buf)
                    {
                        uint n = size;
                        if (RegQueryValueEx(key, name, 0, &type, p, &n) != 0) return null;
                        if (type == RegDword) return BitConverter.ToUInt32(buf, 0).ToString();
                        return new string((char*)p, 0, (int)n / 2).TrimEnd('\0');
                    }
                }
                finally { RegCloseKey(key); }
            }
            catch { return null; }
        }
    }

    public static int GetInt(string name, int fallback) => int.TryParse(Get(name), out int v) ? v : fallback;

    public static void Set(string name, string value)
    {
        lock (gate)
        {
            try
            {
                var file = Environment.GetEnvironmentVariable("BROKENNES_PREFS_FILE");
                if (!string.IsNullOrEmpty(file))
                {
                    var d = ReadFile(file);
                    d[name] = value;
                    File.WriteAllLines(file, d.Select(kv => kv.Key + "=" + kv.Value));
                    return;
                }
                if (RegCreateKeyEx(HkeyCurrentUser, RegistryPath, 0, null, 0, KeyAllAccess, 0, out nint key, out _) != 0) return;
                try
                {
                    var bytes = System.Text.Encoding.Unicode.GetBytes(value + "\0");
                    fixed (byte* p = bytes) RegSetValueEx(key, name, 0, RegSz, p, (uint)bytes.Length);
                }
                finally { RegCloseKey(key); }
            }
            catch { }
        }
    }

    public static void SetInt(string name, int value) => Set(name, value.ToString());

    public static void Delete(string name)
    {
        lock (gate)
        {
            try
            {
                var file = Environment.GetEnvironmentVariable("BROKENNES_PREFS_FILE");
                if (!string.IsNullOrEmpty(file))
                {
                    var d = ReadFile(file);
                    if (d.Remove(name)) File.WriteAllLines(file, d.Select(kv => kv.Key + "=" + kv.Value));
                    return;
                }
                if (RegCreateKeyEx(HkeyCurrentUser, RegistryPath, 0, null, 0, KeyAllAccess, 0, out nint key, out _) != 0) return;
                try { RegDeleteValue(key, name); }
                finally { RegCloseKey(key); }
            }
            catch { }
        }
    }

    private static Dictionary<string, string> ReadFile(string file)
    {
        var d = new Dictionary<string, string>();
        if (!File.Exists(file)) return d;
        foreach (var line in File.ReadAllLines(file))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq]] = line[(eq + 1)..];
        }
        return d;
    }

    private static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001);
    private const uint KeyAllAccess = 0xF003F, RegSz = 1, RegDword = 4;

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyEx(nint key, string subKey, int reserved, string? cls, uint options, uint sam, nint security, out nint result, out uint disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueEx(nint key, string name, nint reserved, uint* type, byte* data, uint* size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueEx(nint key, string name, int reserved, uint type, byte* data, uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteValue(nint key, string name);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint key);
}
