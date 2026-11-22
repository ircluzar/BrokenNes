using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// Tools &amp; Activities > nano98 Packaging (dev): one window over the host-side nano98 tools (docs/projects/nano98-format.md, tools/nano98/).
    /// No packaging logic lives here: every button runs one existing script (build_base.py, seat.py, seat_look.py, pkg/n98.py, pkg/state2img.py,
    /// slim.py when present) and shows the exact command line and its output in the log, so each step can also be run by hand.
    /// Fields are remembered in %LOCALAPPDATA%\BrokenNes\nano98\packaging.json (paths only; never package content).
    /// </summary>
    public sealed class N98PackagingWindow : Form
    {
        private readonly Action<string> openInApp;
        private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.Gainsboro };
        private readonly Dictionary<string, Control> fields = new();
        private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
        private readonly Label busy = new() { Dock = DockStyle.Bottom, Height = 20, Text = "idle", Padding = new Padding(4, 2, 0, 0) };
        private readonly PictureBox lookPreview = new() { SizeMode = PictureBoxSizeMode.Zoom, Width = 320, Height = 240, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black };
        private Process? running;

        private static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "nano98");
        private static string SettingsPath => Path.Combine(LocalRoot, "packaging.json");

        public N98PackagingWindow(Action<string> openInApp)
        {
            this.openInApp = openInApp;
            Text = "nano98 Packaging (dev)";
            Width = 1000; Height = 820; StartPosition = FormStartPosition.CenterParent;
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 470 };
            split.Panel1.Controls.Add(tabs);
            var logBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30 };
            logBar.Controls.Add(Btn("Clear log", () => log.Clear()));
            logBar.Controls.Add(Btn("Stop running step", () => { try { running?.Kill(true); } catch (InvalidOperationException) { } }));
            logBar.Controls.Add(Btn("Open tools folder", () => Explore(ToolsDir())));
            logBar.Controls.Add(Btn("Open format doc", () => { var d = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ToolsDir()))!, "docs", "projects", "nano98-format.md"); if (File.Exists(d)) Process.Start(new ProcessStartInfo(d) { UseShellExecute = true }); }));
            split.Panel2.Controls.Add(log); split.Panel2.Controls.Add(logBar);
            Controls.Add(split); Controls.Add(busy);

            BuildBaseTab(); BuildLayersTab(); BuildDrivesTab(); BuildManifestTab(); BuildCaptureTab(); BuildLookTab(); BuildTestTab(); BuildWarmTab();
            LoadSettings();
            FormClosing += (_, _) => SaveSettings();
            Log($"tools: {SafeToolsDir()}   python: {string.Join(" ", Python())}");
        }

        // ---------- tabs ----------

        private void BuildBaseTab()
        {
            var t = Page("Base image", "The seat base every package runs on (seat_base.img). Made once from your own Win98 SE disc: build_base.py, then seat.py install (launcher shell + seat look). The base is a personal copy: never publish or commit it.");
            PathRow(t, "base", "Seat base (C:)", PathKind.Open, "Disk images|*.img;*.n98d|All|*.*");
            Row(t, "", Btn("Use the app's base", () => { var b = AppBase(); if (b != null) Set("base", b); else Log("no seat_base.img in the app's search places (BROKENNES_N98_BASE, app x86\\nano98, %LOCALAPPDATA%\\BrokenNes\\nano98, C:\\BrokenNes-work\\nano98_work)"); }));
            Heading(t, "Rebuild from the disc (build_base.py)");
            PathRow(t, "bb_iso", "Win98 SE ISO", PathKind.Open, "ISO|*.iso|All|*.*");
            PathRow(t, "bb_work", "Work folder", PathKind.Folder);
            TextRow(t, "bb_size", "Size MB", "1024");
            PathRow(t, "bb_key", "Key file", PathKind.Open, "Text|*.txt|All|*.*");
            CheckRow(t, "bb_eula", "--accept-eula (only if the user accepted the Windows 98 licence in chat)");
            Row(t, "", Btn("Run build_base.py", () => Run(Py(), Script("build_base.py"), Args("--iso", V("bb_iso"), "--work", V("bb_work"), "--size-mb", V("bb_size"), "--keyfile", V("bb_key")).Concat(C("bb_eula") ? new[] { "--accept-eula" } : Array.Empty<string>()))));
            Heading(t, "Seat it");
            PathRow(t, "fatpy", "pyfatfs python (seat.py)", PathKind.Open, "python.exe|python.exe|All|*.*");
            Row(t, "", Btn("seat.py install (launcher + look)", () => Run(V("fatpy") is { Length: > 0 } fp ? new[] { fp } : Py(), Script("seat.py"), new[] { "install", Need("base") })),
                Btn("seat_look.py only", () => Run(Py(), Script("seat_look.py"), new[] { Need("base") })));
        }

        private void BuildLayersTab()
        {
            var t = Page("Dependency layers", "Packages applied under the game, in order (shared runtimes, drivers, a GeForce driver layer...). Compose = n98.py apply --full of each onto the result of the previous one; the last image is then a base for capture or a test.");
            var list = new ListBox { Width = 700, Height = 160, Name = "layers" }; fields["layers"] = list;
            Row(t, "Layers", list);
            Row(t, "", Btn("Add .n98...", () => { using var d = new OpenFileDialog { Filter = "nano98 package|*.n98", Multiselect = true }; if (d.ShowDialog(this) == DialogResult.OK) list.Items.AddRange(d.FileNames); }),
                Btn("Remove", () => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); }),
                Btn("Up", () => MoveItem(list, -1)), Btn("Down", () => MoveItem(list, 1)),
                Btn("Info", () => { if (list.SelectedItem is string p) Run(Py(), Script("pkg/n98.py"), new[] { "info", p }); }));
            Heading(t, "Driver overrides (all packages on this PC): a package pins its layer ids; old=new;old2=new2 runs another layer instead (e.g. nv532=nv4523). Empty = pinning. BROKENNES_N98_LAYER_MAP wins when set.");
            TextRow(t, "layer_map", "Override map", NesEmulator.X86.N98Drivers.SavedMap());
            Row(t, "", Btn("Save overrides", () => { try { NesEmulator.X86.N98Drivers.SaveMap(V("layer_map")); Log("driver overrides: " + (V("layer_map").Length > 0 ? V("layer_map") : "none (pinned layers)")); NesEmulator.X86.N98WarmPrebuild.Kick(); } catch (Exception ex) { Log("could not save: " + ex.Message); } }),
                Btn("Show driver catalog", () => { try { foreach (var d in NesEmulator.X86.N98Drivers.Catalog()) Log(d!.ToJsonString()); } catch (Exception ex) { Log(ex.Message); } }));
            PathRow(t, "layers_out", "Compose into folder", PathKind.Folder);
            Row(t, "", Btn("Compose layers onto the base", ComposeLayers));
            Heading(t, "Schema-2 system layers: n98.py layer cuts one from a captured install; n98.py variant = base .n98d + the listed layers -> a variant base with its own warm state");
            PathRow(t, "ly_src", "Cut from package", PathKind.Open, "nano98 package|*.n98");
            TextRow(t, "ly_id", "Layer id", "");
            TextRow(t, "ly_keep", "--keep file globs", "C:/WINDOWS/*");
            TextRow(t, "ly_reg", "--reg key globs (empty = all)", "");
            PathRow(t, "ly_out", "Layer output", PathKind.Save, "nano98 layer|*.n98");
            Row(t, "", Btn("Cut layer (n98.py layer)", () => Run(Py(), Script("pkg/n98.py"), new[] { "layer", Need("ly_src"), Need("ly_out"), "--id", Need("ly_id"), "--keep" }.Concat(SplitArgs(V("ly_keep")))
                    .Concat(V("ly_reg").Length > 0 ? new[] { "--reg" }.Concat(SplitArgs(V("ly_reg"))) : Array.Empty<string>()))),
                Btn("Add it to the list", () => { if (V("ly_out").Length > 0) list.Items.Add(V("ly_out")); }));
            PathRow(t, "var_out", "Variant output (.n98d)", PathKind.Save, "nano98 base|*.n98d");
            Row(t, "", Btn("Build variant from the list (n98.py variant)", () => Run(Py(), Script("pkg/n98.py"), new[] { "variant", Need("var_out"), "--base", Need("base"), "--layers" }.Concat(list.Items.Cast<string>()).Concat(new[] { "--work", Path.GetDirectoryName(Need("var_out"))! }))),
                Btn("Use variant as Base", () => Set("base", V("var_out"))));
        }

        private void BuildDrivesTab()
        {
            var t = Page("Drives", "The images each drive is captured against and applied onto. C: = the seat base (or a composed layer image); D: = the game drive for packages that write D: (StarCraft); the CD is a reference only: the user's own rip, never inside the package.");
            PathRow(t, "drive_c", "C: base", PathKind.Open, "Disk images|*.img|All|*.*");
            Row(t, "", Btn("= Base image", () => Set("drive_c", V("base"))), Btn("= last composed layer", () => Set("drive_c", LastComposed() ?? "")));
            PathRow(t, "drive_d", "D: game drive (optional)", PathKind.Open, "Disk images|*.img|All|*.*");
            Heading(t, "CD image reference (written into the profile's cd list)");
            TextRow(t, "cd_label", "Volume label", "");
            PathRow(t, "cd_ref", "Image (name kept only)", PathKind.Open, "Disc images|*.iso;*.cue;*.bin|All|*.*");
            CheckRow(t, "cd_required", "required");
            TextRow(t, "cd_note", "Note", "user's own disc rip; never shipped");
            Row(t, "", Btn("Put this CD into the profile", () => { var p = ReadProfile(); var cd = new JsonArray(); if (V("cd_label").Length + V("cd_ref").Length > 0) cd.Add(new JsonObject { ["label"] = V("cd_label"), ["ref"] = Path.GetFileName(V("cd_ref")), ["required"] = C("cd_required"), ["note"] = V("cd_note") }); p["cd"] = cd; WriteProfile(p); }));
        }

        private void BuildManifestTab()
        {
            var t = Page("Manifest / profile", "The hand-written part of the manifest = profiles/GAME.json (hardware, launch, cd, presets, saves), read by capture. Load a profile, edit, Save; Load again shows what is on disk.");
            PathRow(t, "profile", "Profile .json", PathKind.Save, "Profile|*.json");
            Row(t, "", Btn("Load profile", LoadProfileIntoFields), Btn("Save profile", () => WriteProfile(ProfileFromFields())), Btn("Open profiles folder", () => Explore(Path.Combine(SafeToolsDir(), "pkg", "profiles"))));
            Heading(t, "Package");
            TextRow(t, "m_title", "Title (--title)", ""); TextRow(t, "m_id", "Id (--id, optional)", "");
            Heading(t, "Hardware");
            TextRow(t, "h_machine", "machine", "WIN98-P166"); TextRow(t, "h_video", "video", "S3"); TextRow(t, "h_ram", "ram_mb", "64");
            TextRow(t, "h_mincpu", "min_cpu", "P90"); TextRow(t, "h_sound", "sound", "SB16_D072"); TextRow(t, "h_3d", "3d (optional)", ""); TextRow(t, "h_p2cpi", "p2cpi (Pentium II cost per game, empty = 1.0; e.g. 5.3)", "");
            Heading(t, "Launch program");
            TextRow(t, "l_exe", "exe (C:/...)", ""); TextRow(t, "l_args", "args", ""); TextRow(t, "l_cwd", "cwd", ""); TextRow(t, "l_display", "display", "800x600x8");
            Heading(t, "Saves globs (one per line)");
            MultiRow(t, "saves", 70);
            Heading(t, "Presets (JSON object)");
            MultiRow(t, "presets", 70, "{\"default\": {}}");
        }

        private void BuildCaptureTab()
        {
            var t = Page("Capture", "Before/after disks (or an install made in a running x86 state) -> .n98. The before images are the Drives tab's; the profile is the Manifest tab's.");
            PathRow(t, "after_c", "After C: (installed)", PathKind.Open, "Disk images|*.img|All|*.*");
            PathRow(t, "after_d", "After D: (optional)", PathKind.Open, "Disk images|*.img|All|*.*");
            Heading(t, "...or from a state (state2img.py materializes the state's disks first)");
            PathRow(t, "state", "x86 state", PathKind.Open, "All|*.*");
            PathRow(t, "state_out", "Disks out folder", PathKind.Folder);
            Row(t, "", Btn("Run state2img.py", () => Run(Py(), Script("pkg/state2img.py"), new[] { Need("state"), Need("state_out") }.Concat(Opt(V("drive_c"), V("drive_d"))))),
                Btn("Use its disks as After", () => { var o = V("state_out"); var imgs = Directory.Exists(o) ? Directory.GetFiles(o, "*.img").OrderBy(x => x).ToArray() : Array.Empty<string>(); if (imgs.Length > 0) Set("after_c", imgs[0]); if (imgs.Length > 1) Set("after_d", imgs[1]); Log($"state disks: {string.Join(", ", imgs)}"); }));
            Heading(t, "Options");
            MultiRow(t, "excludes", 50, "", "Exclude globs (one per line)");
            CheckRow(t, "deep", "--deep (byte-compare same size/date files)");
            CheckRow(t, "whole_ini", "--whole-ini");
            CheckRow(t, "keep_reg_noise", "--keep-reg-noise (keep Enum/Config: needed for driver installs, e.g. Quake II GeForce)");
            PathRow(t, "snapshot", "--snapshot (optional)", PathKind.Open, "All|*.*");
            PathRow(t, "out", "Output .n98", PathKind.Save, "nano98 package|*.n98");
            Row(t, "", Btn("Capture", Capture), Btn("Info on output", () => Run(Py(), Script("pkg/n98.py"), new[] { "info", Need("out") })));
        }

        private void BuildLookTab()
        {
            var t = Page("Boot look", "seat_look.py: black LOGO.SYS with \"nano98 is loading...\", black shutdown screens, blank boot pointer, black desktop. Preview writes only a .bmp; Apply writes onto the base image.");
            Row(t, "", Btn("Preview boot screen", PreviewLook), Btn("Apply to base image", () => Run(Py(), Script("seat_look.py"), new[] { Need("base") })));
            Row(t, "Preview", lookPreview);
        }

        private void BuildTestTab()
        {
            var t = Page("Test boot", "Base + package in a seat window: the app opens the package the way Emulator > Open game package does, on the base chosen here (BROKENNES_N98_BASE / _BASE_D for this app session). Runs in this app on this PC; long soak or parity runs belong on the fleet / Garage2.");
            PathRow(t, "test_pkg", "Package", PathKind.Open, "nano98 package|*.n98");
            Row(t, "", Btn("= capture output", () => Set("test_pkg", V("out"))), Btn("Info", () => Run(Py(), Script("pkg/n98.py"), new[] { "info", Need("test_pkg") })));
            TextRow(t, "preset", "Preset (n98.py cmd --preset)", "");
            TextRow(t, "exit", "Exit (poweroff|shell|restart)", "poweroff");
            Row(t, "", Btn("Write seat command (n98.py cmd)", () => { var o = Path.Combine(LocalRoot, "test_cmd.txt"); Directory.CreateDirectory(LocalRoot); Run(Py(), Script("pkg/n98.py"), new[] { "cmd", Need("test_pkg"), o, "--exit", V("exit") }.Concat(V("preset").Length > 0 ? new[] { "--preset", V("preset") } : Array.Empty<string>()), () => { if (File.Exists(o)) Log(File.ReadAllText(o)); }); }),
                Btn("Apply only (n98.py apply, check)", () => { var o = Path.Combine(LocalRoot, "test_apply"); Run(Py(), Script("pkg/n98.py"), new[] { "apply", Need("test_pkg"), "--base" }.Concat(BaseArgs()).Concat(new[] { "--out", o })); }));
            Row(t, "", Btn("Test boot in the app", TestBoot));
            Heading(t, "Headless (same path as the app, for a shot): Workshop --n98seat PKG --frames N --shot PNG (heavy: claim a slot or send it to the fleet)");
            Row(t, "", Btn("Copy the Workshop command", () => { var cmd = $"Workshop --n98seat \"{V("test_pkg")}\" --frames 3600 --shot n98seat.png"; Clipboard.SetText(cmd); Log("copied: " + cmd); }));
        }

        private void BuildWarmTab()
        {
            var t = Page("Warm state / v2", "The culled base, its pre-booted warm state and schema-2 packages (tools/nano98: build_seat.py, slim.py, pkg/n98.py v2/layer/variant, pkg/sparse.py). " +
                "The warm state is taken at the launcher's N98 BOOT with the blank standard D: and is shared by every schema-2 package on that base + emulator build. Heavy stages (settle, warm) boot Windows: run them on Garage2 / the fleet when the PC is busy.");
            Heading(t, "One command: build_seat.py (each stage is skipped when its output is already in the work folder)");
            PathRow(t, "bs_workshop", "Workshop exe", PathKind.Open, "BrokenNes.Workshop.exe|BrokenNes.Workshop.exe|All|*.*");
            PathRow(t, "bs_work", "Work folder", PathKind.Folder);
            PathRow(t, "bs_out", "Output base (.n98d)", PathKind.Save, "nano98 base|*.n98d");
            PathRow(t, "bs_installed", "--installed (an installed Win98 disk; else the Base tab's ISO)", PathKind.Open, "Disk images|*.img;*.n98d|All|*.*");
            Row(t, "", Btn("Run build_seat.py", () => Run(Py(), Script("build_seat.py"), new[] { "--workshop", Need("bs_workshop"), "--work", Need("bs_work"), "--out", Need("bs_out") }
                    .Concat(V("bs_installed").Length > 0 ? new[] { "--installed", V("bs_installed") } : new[] { "--iso", Need("bb_iso") }.Concat(Args("--keyfile", V("bb_key"))).Concat(C("bb_eula") ? new[] { "--accept-eula" } : Array.Empty<string>())))),
                Btn("Open work folder", () => Explore(V("bs_work"))), Btn("Use output as Base", () => Set("base", V("bs_out"))));
            Heading(t, "Single steps");
            PathRow(t, "slim_in", "slim.py input (raw image)", PathKind.Open, "Disk images|*.img|All|*.*");
            PathRow(t, "slim_out", "slim.py output (.img)", PathKind.Save, "Disk images|*.img|All|*.*");
            Row(t, "", Btn("Cull + zero-fill + pack (slim.py --n98d)", () => Run(Py(), Script("slim.py"), new[] { Need("slim_in"), Need("slim_out"), "--n98d", Path.ChangeExtension(Need("slim_out"), ".n98d"), "--list", Path.ChangeExtension(Need("slim_out"), ".removed.txt") })),
                Btn("Pack only (sparse.py pack)", () => Run(Py(), Script("pkg/sparse.py"), new[] { "pack", Need("slim_in"), Path.ChangeExtension(Need("slim_in"), ".n98d") })),
                Btn("Unpack .n98d", () => Run(Py(), Script("pkg/sparse.py"), new[] { "unpack", Need("base"), Path.ChangeExtension(Need("base"), ".img") })),
                Btn("Info on base .n98d", () => Run(Py(), Script("pkg/sparse.py"), new[] { "info", Need("base") })));
            Heading(t, "Schema 1 -> 2 (n98.py v2): the game moves to D:\\GAME on its own standard drive; registry/INI applied by the launcher at run time");
            PathRow(t, "v2_in", "Schema-1 package", PathKind.Open, "nano98 package|*.n98");
            PathRow(t, "v2_out", "Schema-2 output", PathKind.Save, "nano98 package|*.n98");
            TextRow(t, "v2_drop", "--drop globs (space separated)", "");
            TextRow(t, "v2_layers", "--layers ids (space separated)", "");
            TextRow(t, "v2_args", "--args (replace launch args; empty = keep)", "");
            TextRow(t, "v2_dsize", "--d-size-mb (game drive size; empty = the standard 2000; e.g. 8000 for a big game)", "");
            CheckRow(t, "v2_embed", "--embed-base (the Base tab's .n98d inside the package: self-contained, personal copy only)");
            Row(t, "", Btn("Convert (n98.py v2)", () => Run(Py(), Script("pkg/n98.py"), new[] { "v2", Need("v2_in"), Need("v2_out"), "--work", Path.GetDirectoryName(Need("v2_out"))! }
                    .Concat(V("v2_drop").Length > 0 ? new[] { "--drop" }.Concat(SplitArgs(V("v2_drop"))) : Array.Empty<string>())
                    .Concat(V("v2_layers").Length > 0 ? new[] { "--layers" }.Concat(SplitArgs(V("v2_layers"))) : Array.Empty<string>())
                    .Concat(V("v2_args").Length > 0 ? new[] { "--args", V("v2_args") } : Array.Empty<string>())
                    .Concat(V("v2_dsize").Length > 0 ? new[] { "--d-size-mb", V("v2_dsize") } : Array.Empty<string>())
                    .Concat(C("v2_embed") ? new[] { "--embed-base", Need("base") } : Array.Empty<string>()))),
                Btn("= Test boot package", () => Set("test_pkg", V("v2_out"))));
            Heading(t, "The warm state of this base for this app build: made by the first schema-2 package opened (Test boot), or headless:");
            Row(t, "", Btn("Copy the Workshop warm command", () => { var cmd = $"set BROKENNES_N98_BASE={V("base")} && Workshop --n98seat \"{V("test_pkg")}\" --until-warm --frames 60000"; Clipboard.SetText(cmd); Log("copied: " + cmd); }),
                Btn("Open warm folders", () => Explore(Path.Combine(LocalRoot, "warm"))));
        }

        // ---------- steps that chain existing tools ----------

        private void ComposeLayers()
        {
            var list = (ListBox)fields["layers"];
            var layers = list.Items.Cast<string>().ToList();
            if (layers.Count == 0) { Log("no layers"); return; }
            string outRoot = Need("layers_out"), cur = Need("base");
            var steps = new List<(string pkg, string outDir)>();
            for (int i = 0; i < layers.Count; i++) steps.Add((layers[i], Path.Combine(outRoot, $"{i + 1:00}_{Path.GetFileNameWithoutExtension(layers[i])}")));
            void Next(int i, string baseImg)
            {
                if (i == steps.Count) { Log($"composed: {baseImg}"); return; }
                Run(Py(), Script("pkg/n98.py"), new[] { "apply", steps[i].pkg, "--base", "C=" + baseImg, "--out", steps[i].outDir, "--full" }, () => Next(i + 1, Path.Combine(steps[i].outDir, "C.img")));
            }
            Next(0, cur);
        }

        private string? LastComposed()
        {
            var o = V("layers_out"); if (!Directory.Exists(o)) return null;
            return Directory.GetDirectories(o).OrderBy(x => x).Select(d => Path.Combine(d, "C.img")).LastOrDefault(File.Exists);
        }

        private void Capture()
        {
            var a = new List<string> { "capture", Need("out"), "--title", Need("m_title") };
            if (V("m_id").Length > 0) a.AddRange(new[] { "--id", V("m_id") });
            var before = Opt(V("drive_c").Length > 0 ? "C=" + V("drive_c") : "", V("drive_d").Length > 0 ? "D=" + V("drive_d") : "").ToArray();
            if (before.Length > 0) { a.Add("--before"); a.AddRange(before); }
            a.Add("--after"); a.Add("C=" + Need("after_c")); if (V("after_d").Length > 0) a.Add("D=" + V("after_d"));
            if (V("profile").Length > 0) { a.Add("--profile"); a.Add(V("profile")); }
            var ex = Lines("excludes"); if (ex.Length > 0) { a.Add("--exclude"); a.AddRange(ex); }
            if (C("deep")) a.Add("--deep"); if (C("whole_ini")) a.Add("--whole-ini"); if (C("keep_reg_noise")) a.Add("--keep-reg-noise");
            if (V("snapshot").Length > 0) { a.Add("--snapshot"); a.Add(V("snapshot")); }
            Run(Py(), Script("pkg/n98.py"), a);
        }

        private void PreviewLook()
        {
            Directory.CreateDirectory(LocalRoot);
            var bmp = Path.Combine(LocalRoot, "seat_look_preview.bmp");
            Run(Py(), Script("seat_look.py"), new[] { "--bmp", bmp }, () =>
            {
                try { using var fs = File.OpenRead(bmp); lookPreview.Image?.Dispose(); lookPreview.Image = Image.FromStream(fs); }
                catch (Exception e) { Log("preview: " + e.Message); }
            });
        }

        private void TestBoot()
        {
            var pkg = Need("test_pkg");
            var b = V("drive_c").Length > 0 ? V("drive_c") : V("base");
            if (b.Length > 0) { Environment.SetEnvironmentVariable("BROKENNES_N98_BASE", b); Log($"BROKENNES_N98_BASE={b}"); }
            if (V("drive_d").Length > 0) { Environment.SetEnvironmentVariable("BROKENNES_N98_BASE_D", V("drive_d")); Log($"BROKENNES_N98_BASE_D={V("drive_d")}"); }
            Log($"opening {pkg} in the app (status N98 BOOT -> READY -> RUN; the first run applies, later runs resume)");
            openInApp(pkg);
        }

        private IEnumerable<string> BaseArgs()
        {
            var c = V("drive_c").Length > 0 ? V("drive_c") : Need("base");
            yield return "C=" + c;
            if (V("drive_d").Length > 0) yield return "D=" + V("drive_d");
        }

        // ---------- profile <-> fields ----------

        private JsonObject ReadProfile()
        {
            var p = V("profile");
            try { if (File.Exists(p)) return JsonNode.Parse(File.ReadAllText(p)) as JsonObject ?? new JsonObject(); }
            catch (JsonException e) { Log($"profile parse error: {e.Message}"); }
            return new JsonObject();
        }

        private void WriteProfile(JsonObject p)
        {
            var path = Need("profile");
            File.WriteAllText(path, p.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log($"wrote {path}");
        }

        private void LoadProfileIntoFields()
        {
            var p = ReadProfile();
            string S(JsonNode? n) => n?.ToString() ?? "";
            var h = p["hardware"]; var l = p["launch"];
            Set("h_machine", S(h?["machine"])); Set("h_video", S(h?["video"])); Set("h_ram", S(h?["ram_mb"])); Set("h_mincpu", S(h?["min_cpu"])); Set("h_sound", S(h?["sound"])); Set("h_3d", S(h?["3d"])); Set("h_p2cpi", h?["p2cpi"]?.ToJsonString() ?? "");
            Set("l_exe", S(l?["exe"])); Set("l_args", S(l?["args"])); Set("l_cwd", S(l?["cwd"])); Set("l_display", S(l?["display"]));
            Set("saves", string.Join(Environment.NewLine, (p["saves"] as JsonArray ?? new JsonArray()).Select(S)));
            Set("presets", p["presets"]?.ToJsonString() ?? "{}");
            if (p["cd"] is JsonArray { Count: > 0 } cd) { Set("cd_label", S(cd[0]?["label"])); Set("cd_ref", S(cd[0]?["ref"])); Set("cd_note", S(cd[0]?["note"])); if (fields["cd_required"] is CheckBox cb) cb.Checked = cd[0]?["required"]?.GetValue<bool>() ?? false; }
            Log($"loaded {V("profile")}: {p.ToJsonString()}");
        }

        private JsonObject ProfileFromFields()
        {
            var p = ReadProfile();   // keeps fields this window does not show
            var h = p["hardware"] as JsonObject ?? new JsonObject();
            void Put(JsonObject o, string k, string v, bool num = false) { if (v.Length == 0) o.Remove(k); else o[k] = num && int.TryParse(v, out var n) ? n : v; }
            Put(h, "machine", V("h_machine")); Put(h, "video", V("h_video")); Put(h, "ram_mb", V("h_ram"), true); Put(h, "min_cpu", V("h_mincpu")); Put(h, "sound", V("h_sound")); Put(h, "3d", V("h_3d"));
            if (double.TryParse(V("h_p2cpi"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cpi) && cpi >= 1.0) h["p2cpi"] = cpi; else h.Remove("p2cpi");
            p["hardware"] = h.DeepClone();
            var l = p["launch"] as JsonObject ?? new JsonObject();
            l["exe"] = V("l_exe"); l["args"] = V("l_args"); l["cwd"] = V("l_cwd"); l["display"] = V("l_display");
            p["launch"] = l.DeepClone();
            p["saves"] = new JsonArray(Lines("saves").Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            try { p["presets"] = JsonNode.Parse(V("presets").Length > 0 ? V("presets") : "{}"); }
            catch (JsonException e) { Log($"presets: not JSON ({e.Message}), kept the old value"); }
            return p;
        }

        // ---------- process runner ----------

        private void Run(string[] py, string script, IEnumerable<string> args, Action? then = null)
        {
            if (running is { HasExited: false }) { Log("a step is still running (Stop running step first)"); return; }
            List<string> argv;
            try { argv = args.ToList(); } catch (MissingFieldException e) { Log(e.Message); return; }
            var psi = new ProcessStartInfo(py[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = SafeToolsDir() };
            foreach (var a in py.Skip(1)) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(script);
            foreach (var a in argv) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            Log("> " + string.Join(" ", psi.ArgumentList.Prepend(py[0]).Select(Quote)));
            var sw = Stopwatch.StartNew();
            Process p;
            try { p = Process.Start(psi)!; }
            catch (Exception e) { Log($"could not start python: {e.Message} (set BROKENNES_PYTHON)"); return; }
            running = p; busy.Text = "running: " + Path.GetFileName(script);
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            p.Exited += (_, _) => BeginInvoke(() =>
            {
                p.WaitForExit();
                Log($"[exit {p.ExitCode}, {sw.Elapsed.TotalSeconds:F1} s]");
                busy.Text = "idle"; running = null;
                if (p.ExitCode == 0) then?.Invoke();
            });
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
        }

        private static string Quote(string s) => s.Length == 0 || s.Any(char.IsWhiteSpace) ? "\"" + s + "\"" : s;

        private void Log(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
            log.AppendText(s + Environment.NewLine);
        }

        /// <summary>Python 3 the same way N98Seat finds it: BROKENNES_PYTHON, else py -3, else python.</summary>
        private static string[] Python()
        {
            if (Environment.GetEnvironmentVariable("BROKENNES_PYTHON") is { Length: > 0 } p) return new[] { p };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (dir.Length > 0 && File.Exists(Path.Combine(dir, "py.exe"))) return new[] { Path.Combine(dir, "py.exe"), "-3" }; }
                catch (ArgumentException) { }
            }
            if (File.Exists(@"C:\Windows\py.exe")) return new[] { @"C:\Windows\py.exe", "-3" };
            return new[] { "python" };
        }
        private static string[] Py() => Python();

        private static string ToolsDir()
        {
            if (Environment.GetEnvironmentVariable("BROKENNES_N98_TOOLS") is { Length: > 0 } t) return t;
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                    if (File.Exists(Path.Combine(d.FullName, "tools", "nano98", "pkg", "n98.py"))) return Path.Combine(d.FullName, "tools", "nano98");
            var bundled = Path.Combine(AppContext.BaseDirectory, "x86", "nano98", "tools");
            if (File.Exists(Path.Combine(bundled, "pkg", "n98.py"))) return bundled;
            throw new FileNotFoundException(@"tools\nano98 not found above the app's folder; set BROKENNES_N98_TOOLS");
        }
        private static string SafeToolsDir() { try { return ToolsDir(); } catch (FileNotFoundException) { return AppContext.BaseDirectory; } }
        private static string Script(string rel) => Path.Combine(SafeToolsDir(), rel.Replace('/', Path.DirectorySeparatorChar));

        private static string? AppBase()
        {
            var tried = new List<string>();
            if (Environment.GetEnvironmentVariable("BROKENNES_N98_BASE") is { Length: > 0 } b) tried.Add(b);
            tried.Add(Path.Combine(AppContext.BaseDirectory, "x86", "nano98", "seat_base.img"));
            tried.Add(Path.Combine(LocalRoot, "seat_base.img"));
            tried.Add(@"C:\BrokenNes-work\nano98_work\seat_base.img");
            return tried.FirstOrDefault(File.Exists);
        }

        // ---------- field helpers ----------

        private enum PathKind { Open, Save, Folder }

        private TableLayoutPanel Page(string title, string help)
        {
            var page = new TabPage(title) { AutoScroll = true };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var h = new Label { Text = help, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 3, 3, 8) };
            t.Controls.Add(h); t.SetColumnSpan(h, 2);
            page.Controls.Add(t); tabs.TabPages.Add(page);
            return t;
        }

        private static void Heading(TableLayoutPanel t, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(3, 10, 3, 3), MaximumSize = new Size(900, 0) };
            t.Controls.Add(l); t.SetColumnSpan(l, 2);
        }

        private static void Row(TableLayoutPanel t, string label, params Control[] cs)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            if (cs.Length == 1) { t.Controls.Add(cs[0]); return; }
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(780, 0) };
            f.Controls.AddRange(cs); t.Controls.Add(f);
        }

        private void TextRow(TableLayoutPanel t, string key, string label, string def)
        {
            var tb = new TextBox { Width = 560, Text = def, Name = key }; fields[key] = tb; Row(t, label, tb);
        }

        private void MultiRow(TableLayoutPanel t, string key, int height, string def = "", string label = "")
        {
            var tb = new TextBox { Width = 700, Height = height, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = def, Name = key }; fields[key] = tb; Row(t, label, tb);
        }

        private void CheckRow(TableLayoutPanel t, string key, string label)
        {
            var cb = new CheckBox { Text = label, AutoSize = true, Name = key }; fields[key] = cb; Row(t, "", cb);
        }

        private void PathRow(TableLayoutPanel t, string key, string label, PathKind kind, string filter = "All|*.*")
        {
            var tb = new TextBox { Width = 560, Name = key }; fields[key] = tb;
            var b = Btn("...", () =>
            {
                if (kind == PathKind.Folder) { using var d = new FolderBrowserDialog { SelectedPath = tb.Text }; if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.SelectedPath; }
                else
                {
                    using FileDialog d = kind == PathKind.Save ? new SaveFileDialog { Filter = filter, OverwritePrompt = false } : new OpenFileDialog { Filter = filter };
                    try { if (tb.Text.Length > 0) { d.InitialDirectory = Path.GetDirectoryName(tb.Text) ?? ""; d.FileName = Path.GetFileName(tb.Text); } } catch (ArgumentException) { }
                    if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.FileName;
                }
            });
            b.Width = 30;
            Row(t, label, tb, b);
        }

        private static Button Btn(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 26 };
            b.Click += (_, _) => { try { a(); } catch (MissingFieldException) { } };
            return b;
        }

        private static void MoveItem(ListBox l, int d)
        {
            int i = l.SelectedIndex, j = i + d; if (i < 0 || j < 0 || j >= l.Items.Count) return;
            var x = l.Items[i]; l.Items.RemoveAt(i); l.Items.Insert(j, x); l.SelectedIndex = j;
        }

        private string V(string key) => fields.TryGetValue(key, out var c) && c is TextBox tb ? tb.Text.Trim() : "";
        private bool C(string key) => fields.TryGetValue(key, out var c) && c is CheckBox cb && cb.Checked;
        private void Set(string key, string v) { if (fields.TryGetValue(key, out var c) && c is TextBox tb) tb.Text = v; }
        private string[] Lines(string key) => V(key).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        private string Need(string key) { var v = V(key); if (v.Length == 0) { var msg = $"missing field: {key}"; Log(msg); throw new MissingFieldException(msg); } return v; }
        private static IEnumerable<string> Args(params string[] kv) { for (int i = 0; i + 1 < kv.Length; i += 2) if (kv[i + 1].Length > 0) { yield return kv[i]; yield return kv[i + 1]; } }
        private static IEnumerable<string> Opt(params string[] xs) => xs.Where(x => x.Length > 0);
        private static string[] SplitArgs(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        private static void Explore(string dir) { if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\""); }

        // ---------- settings ----------

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) { var b = AppBase(); if (b != null) Set("base", b); Set("profile", Path.Combine(SafeToolsDir(), "pkg", "profiles", "new_game.json")); return; }
                var o = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
                if (o == null) return;
                foreach (var (k, v) in o)
                {
                    if (!fields.TryGetValue(k, out var c) || v == null) continue;
                    if (c is TextBox tb) tb.Text = v.ToString();
                    else if (c is CheckBox cb) cb.Checked = v.GetValue<bool>();
                    else if (c is ListBox lb && v is JsonArray arr) lb.Items.AddRange(arr.Select(x => (object)(x?.ToString() ?? "")).ToArray());
                }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { Log("settings: " + e.Message); }
        }

        private void SaveSettings()
        {
            var o = new JsonObject();
            foreach (var (k, c) in fields)
                o[k] = c switch { TextBox tb => tb.Text, CheckBox cb => cb.Checked, ListBox lb => new JsonArray(lb.Items.Cast<string>().Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()), _ => null };
            try { Directory.CreateDirectory(LocalRoot); File.WriteAllText(SettingsPath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException) { }
        }
    }
}
