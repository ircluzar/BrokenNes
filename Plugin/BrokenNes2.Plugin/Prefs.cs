using System.Runtime.InteropServices;

namespace BrokenNes2;

/// <summary>
/// The plugin's remembered settings, per Windows user, not per project: whether the About window has been seen, and the input mappings.
/// They live under <c>HKEY_CURRENT_USER\Software\BrokenNes\BrokenNes2</c>. For tests, the environment variable BROKENNES_PREFS_FILE names a text
/// file (one <c>name=value</c> per line) that is used instead, so a test run never touches the registry. Nothing here can fail loudly:
/// a setting that cannot be read is simply absent, one that cannot be written is simply not remembered.
///
/// Migration. Before the plugin got its plain name the settings lived under <see cref="OldRegistryPath"/>. The first time a process reads or writes
/// a settings store whose new key is empty, every value of the old key is copied to the new one, once; the old key is only ever read: it is never
/// changed, emptied or deleted (an older build of the plugin still reads it). A new key that already has values is left alone. Tests drive the same
/// code with other keys (BROKENNES_PREFS_REGKEY and BROKENNES_PREFS_OLD_REGKEY, registry paths under HKCU) or with a fake old file
/// (BROKENNES_PREFS_OLD_FILE, next to BROKENNES_PREFS_FILE).
/// </summary>
internal static unsafe partial class Prefs
{
    private const string RegistryPath = @"Software\BrokenNes\BrokenNes2";
    /// <summary>Where the settings lived before the rename. Read by the migration, never written.</summary>
    private const string OldRegistryPath = @"Software\Bogue\BrokenNes2";
    private static readonly object gate = new();
    private static readonly HashSet<string> migrated = new();

    private static string? FileMode => Environment.GetEnvironmentVariable("BROKENNES_PREFS_FILE") is { Length: > 0 } f ? f : null;
    private static string NewPath => Environment.GetEnvironmentVariable("BROKENNES_PREFS_REGKEY") is { Length: > 0 } p ? p : RegistryPath;
    private static string OldPath => Environment.GetEnvironmentVariable("BROKENNES_PREFS_OLD_REGKEY") is { Length: > 0 } p ? p : OldRegistryPath;

    public static string? Get(string name)
    {
        lock (gate)
        {
            try
            {
                MigrateOnce();
                var file = FileMode;
                if (file != null) return ReadFile(file).GetValueOrDefault(name);
                if (RegCreateKeyEx(HkeyCurrentUser, NewPath, 0, null, 0, KeyAllAccess, 0, out nint key, out _) != 0) return null;
                try { return ReadValue(key, name); }
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
                MigrateOnce();
                var file = FileMode;
                if (file != null)
                {
                    var d = ReadFile(file);
                    d[name] = value;
                    File.WriteAllLines(file, d.Select(kv => kv.Key + "=" + kv.Value));
                    return;
                }
                WriteValue(NewPath, name, value);
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
                MigrateOnce();
                var file = FileMode;
                if (file != null)
                {
                    var d = ReadFile(file);
                    if (d.Remove(name)) File.WriteAllLines(file, d.Select(kv => kv.Key + "=" + kv.Value));
                    return;
                }
                if (RegCreateKeyEx(HkeyCurrentUser, NewPath, 0, null, 0, KeyAllAccess, 0, out nint key, out _) != 0) return;
                try { RegDeleteValue(key, name); }
                finally { RegCloseKey(key); }
            }
            catch { }
        }
    }

    // ---- the one-time carry-over from the old key ----

    /// <summary>Once per settings store and process: when the new store has no values, copy every value of the old one into it. Never touches the old store.</summary>
    private static void MigrateOnce()
    {
        var file = FileMode;
        string id = file != null ? "file:" + file : "reg:" + NewPath;
        if (!migrated.Add(id)) return;
        try
        {
            if (file != null)
            {
                var oldFile = Environment.GetEnvironmentVariable("BROKENNES_PREFS_OLD_FILE");
                if (string.IsNullOrEmpty(oldFile) || ReadFile(file).Count != 0) return;
                var fromFile = ReadFile(oldFile);
                if (fromFile.Count != 0) File.WriteAllLines(file, fromFile.Select(kv => kv.Key + "=" + kv.Value));
                return;
            }
            var current = ReadAllRegistry(NewPath);
            if (current is { Count: > 0 }) return;                         // the new key already has its settings
            var old = ReadAllRegistry(OldPath);                            // null when there is no old key (opened for reading only: it is not created)
            if (old == null || old.Count == 0) return;
            foreach (var kv in old) WriteValue(NewPath, kv.Key, kv.Value);
        }
        catch { }
    }

    // ---- registry plumbing ----

    /// <summary>A value of an open key as text (a DWORD as its decimal digits), or null when it is absent or empty.</summary>
    private static string? ReadValue(nint key, string name)
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

    private static void WriteValue(string path, string name, string value)
    {
        if (RegCreateKeyEx(HkeyCurrentUser, path, 0, null, 0, KeyAllAccess, 0, out nint key, out _) != 0) return;
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(value + "\0");
            fixed (byte* p = bytes) RegSetValueEx(key, name, 0, RegSz, p, (uint)bytes.Length);
        }
        finally { RegCloseKey(key); }
    }

    /// <summary>Every value under an HKCU key as name -> text, or null when the key does not exist. The key is opened for reading only (never created).</summary>
    private static Dictionary<string, string>? ReadAllRegistry(string path)
    {
        if (RegOpenKeyEx(HkeyCurrentUser, path, 0, KeyRead, out nint key) != 0) return null;
        try
        {
            var d = new Dictionary<string, string>();
            char* nameBuf = stackalloc char[256];
            for (uint i = 0; i < 1000; i++)
            {
                uint nameLen = 256;
                int rc = RegEnumValue(key, i, nameBuf, &nameLen, 0, null, null, null);
                if (rc == ErrorNoMoreItems) break;
                if (rc != 0) continue;                                    // a name too long for the buffer: skip it
                string name = new string(nameBuf, 0, (int)nameLen);
                var v = ReadValue(key, name);
                if (v != null) d[name] = v;
            }
            return d;
        }
        finally { RegCloseKey(key); }
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
    private const uint KeyAllAccess = 0xF003F, KeyRead = 0x20019, RegSz = 1, RegDword = 4;
    private const int ErrorNoMoreItems = 259;

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyEx(nint key, string subKey, int reserved, string? cls, uint options, uint sam, nint security, out nint result, out uint disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(nint key, string subKey, uint options, uint sam, out nint result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW")]
    private static partial int RegEnumValue(nint key, uint index, char* name, uint* nameLen, nint reserved, uint* type, byte* data, uint* dataLen);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueEx(nint key, string name, nint reserved, uint* type, byte* data, uint* size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueEx(nint key, string name, int reserved, uint type, byte* data, uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteValue(nint key, string name);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint key);
}
