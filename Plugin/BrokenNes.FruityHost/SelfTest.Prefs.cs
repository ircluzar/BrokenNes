// Round 5 (inputs): the plugin's remembered settings moved to a new registry path; the old values are carried over once and the old key is never touched.
using System.Security.Cryptography;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using Microsoft.Win32;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    // ------------------------------------------------------------------------------------------------
    // The migration, through the plugin's own Prefs code (editor hooks 93 = read an integer setting by name, 94 = write one). Two kinds of fake old store:
    //   * files: BROKENNES_PREFS_FILE is the new store, BROKENNES_PREFS_OLD_FILE the old one;
    //   * the real registry code, on test keys under HKCU\Software\BrokenNes_Test\<guid>: BROKENNES_PREFS_REGKEY / BROKENNES_PREFS_OLD_REGKEY name the new and old key.
    // The real user's keys are never touched. NOT tested: the two production paths themselves (they are constants in Prefs.cs, and the real old key would have to be written);
    // the code that reads and writes them is the code under test here.

    private static int PrefInt(PluginInstance q, string name)
    {
        fixed (char* p = name) return (int)q.Test(93, 0, (nint)p);
    }

    private static void PrefSetInt(PluginInstance q, string name, int value)
    {
        fixed (char* p = name) q.Test(94, (nint)p, value);
    }

    private static string FileHash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "(absent)";

    private void PrefsMigrationTest(T t)
    {
        const int Absent = -999;
        string? savedFile = Environment.GetEnvironmentVariable("BROKENNES_PREFS_FILE");
        string tag = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(Path.GetTempPath(), "bn2_prefs_" + tag);
        string regRoot = @"Software\BrokenNes_Test\" + tag;
        Directory.CreateDirectory(dir);

        using var q = RomPlugin();                                    // the editor is opened first, while the preferences are the test file: its start-up reads and writes go there
        nint parent = Win32Host.CreateParent();
        try
        {
            q.ShowEditor(parent);
            for (int k = 0; k < 3; k++) { RenderBlocks(q, 0.1); Win32Host.Pump(40); }
            PrefSetInt(q, "HookProbe", 41);
            t.Expect(PrefInt(q, "HookProbe") == 41 && PrefsText().Contains("HookProbe=41"), "the hooks do not read and write the test preferences file");

            // ---- files ----
            string oldFile = Path.Combine(dir, "old.txt"), newFile = Path.Combine(dir, "new.txt");
            File.WriteAllLines(oldFile, new[] { "InputKeys=38,40,37,39,88,90,83,65,81,87,13,161", "InputAllowOpposite=1", "AboutRevisionSeen=7" });
            string oldHash = FileHash(oldFile);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_FILE", newFile);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_FILE", oldFile);
            t.Expect(PrefInt(q, "InputAllowOpposite") == 1 && PrefInt(q, "AboutRevisionSeen") == 7, $"file: the old store's values were not carried over (read {PrefInt(q, "InputAllowOpposite")}, {PrefInt(q, "AboutRevisionSeen")})");
            string moved = File.Exists(newFile) ? File.ReadAllText(newFile) : "";
            t.Expect(moved.Contains("InputKeys=38,40,37,39,88,90,83,65,81,87,13,161") && moved.Contains("InputAllowOpposite=1") && moved.Contains("AboutRevisionSeen=7"), "file: the new store does not hold every old value");
            PrefSetInt(q, "Probe", 5);
            t.Expect(PrefInt(q, "Probe") == 5 && File.ReadAllText(newFile).Contains("Probe=5"), "file: a new setting did not go to the new store");
            t.Expect(FileHash(oldFile) == oldHash, "file: the old store was changed");

            // a new store that already has settings is not overwritten and gets nothing from the old one
            string newFile2 = Path.Combine(dir, "new2.txt");
            File.WriteAllLines(newFile2, new[] { "InputAllowOpposite=0" });
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_FILE", newFile2);
            t.Expect(PrefInt(q, "InputAllowOpposite") == 0 && PrefInt(q, "AboutRevisionSeen") == Absent, $"file: a non-empty new store was overwritten ({PrefInt(q, "InputAllowOpposite")}, {PrefInt(q, "AboutRevisionSeen")})");
            t.Expect(File.ReadAllLines(newFile2).Length == 1 && FileHash(oldFile) == oldHash, "file: the non-empty new store changed, or the old one did");

            // no old store: nothing happens, nothing is created at the old path
            string newFile3 = Path.Combine(dir, "new3.txt"), missingOld = Path.Combine(dir, "never-written.txt");
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_FILE", newFile3);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_FILE", missingOld);
            t.Expect(PrefInt(q, "InputAllowOpposite") == Absent && !File.Exists(missingOld), "file: with no old store a setting appeared, or the old path was created");
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_FILE", null);

            // ---- the real registry code on test keys ----
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_FILE", null);
            string oldKey = regRoot + @"\Old", newKey = regRoot + @"\New";
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_REGKEY", newKey);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_REGKEY", oldKey);
            using (var k = Registry.CurrentUser.CreateSubKey(oldKey))
            {
                k.SetValue("InputKeys", "38,40,37,39,88,90,83,65,81,87,13,161", RegistryValueKind.String);   // the old plugin wrote text
                k.SetValue("InputAllowOpposite", "1", RegistryValueKind.String);
                k.SetValue("AboutRevisionSeen", "7", RegistryValueKind.String);
                k.SetValue("InputKeyboard", 0, RegistryValueKind.DWord);                                     // and a number, to cover that kind too
            }
            t.Expect(PrefInt(q, "InputAllowOpposite") == 1 && PrefInt(q, "AboutRevisionSeen") == 7 && PrefInt(q, "InputKeyboard") == 0, $"registry: the old key's values were not carried over ({PrefInt(q, "InputAllowOpposite")}, {PrefInt(q, "AboutRevisionSeen")}, {PrefInt(q, "InputKeyboard")})");
            using (var nk = Registry.CurrentUser.OpenSubKey(newKey))
            {
                var names = nk?.GetValueNames().OrderBy(n => n).ToArray() ?? [];
                t.Expect(names.SequenceEqual(new[] { "AboutRevisionSeen", "InputAllowOpposite", "InputKeyboard", "InputKeys" }) && nk!.GetValue("InputKeys") as string == "38,40,37,39,88,90,83,65,81,87,13,161" && nk.GetValue("InputKeyboard")?.ToString() == "0",
                    "registry: the new key does not hold exactly the old values: " + string.Join(",", names));
            }
            using (var ok = Registry.CurrentUser.OpenSubKey(oldKey))
            {
                var names = ok?.GetValueNames().OrderBy(n => n).ToArray() ?? [];
                t.Expect(ok != null && names.Length == 4 && ok.GetValueKind("InputKeyboard") == RegistryValueKind.DWord && ok.GetValue("InputAllowOpposite") as string == "1" && ok.GetValue("AboutRevisionSeen") as string == "7",
                    "registry: the old key was changed (values " + string.Join(",", names) + ")");
            }
            PrefSetInt(q, "Probe", 5);
            using (var nk = Registry.CurrentUser.OpenSubKey(newKey)) t.Expect(nk?.GetValue("Probe") as string == "5", "registry: a new setting did not go to the new key");
            using (var ok = Registry.CurrentUser.OpenSubKey(oldKey)) t.Expect(ok != null && ok.GetValue("Probe") == null && ok.GetValueNames().Length == 4, "registry: a new setting reached the old key");

            // a new key that already has a value is left alone
            string oldKey2 = regRoot + @"\Old2", newKey2 = regRoot + @"\New2";
            using (var k = Registry.CurrentUser.CreateSubKey(oldKey2)) { k.SetValue("InputAllowOpposite", "1"); k.SetValue("Extra", "9"); }
            using (var k = Registry.CurrentUser.CreateSubKey(newKey2)) k.SetValue("InputAllowOpposite", "0");
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_REGKEY", newKey2);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_REGKEY", oldKey2);
            t.Expect(PrefInt(q, "InputAllowOpposite") == 0 && PrefInt(q, "Extra") == Absent, $"registry: a non-empty new key was overwritten or topped up ({PrefInt(q, "InputAllowOpposite")}, {PrefInt(q, "Extra")})");
            using (var nk = Registry.CurrentUser.OpenSubKey(newKey2)) t.Expect(nk != null && nk.GetValueNames().Length == 1, "registry: the non-empty new key changed");

            // no old key: nothing is read, and the old key is not created
            string oldKey3 = regRoot + @"\Old3-never-written", newKey3 = regRoot + @"\New3";
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_REGKEY", newKey3);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_REGKEY", oldKey3);
            t.Expect(PrefInt(q, "InputAllowOpposite") == Absent, "registry: with no old key a setting appeared");
            using (var ok = Registry.CurrentUser.OpenSubKey(oldKey3)) t.Expect(ok == null, "registry: reading created the old key");

            q.HideEditor();
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_FILE", savedFile);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_FILE", null);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_REGKEY", null);
            Environment.SetEnvironmentVariable("BROKENNES_PREFS_OLD_REGKEY", null);
            Win32Host.DestroyParent(parent);
            try { Registry.CurrentUser.DeleteSubKeyTree(regRoot, throwOnMissingSubKey: false); } catch { }
            try { Registry.CurrentUser.DeleteSubKey(@"Software\BrokenNes_Test", throwOnMissingSubKey: false); } catch { }     // the parent, when nothing else is under it
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
