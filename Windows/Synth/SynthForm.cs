using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BrokenNes.SynthHost;

namespace BrokenNes.Windows.Synth
{
    /// <summary>
    /// BrokenNes 2 as a standalone synthesizer: the FL Studio plugin hosted in a window of its own. Four instances of it (Pulse 1, Pulse 2,
    /// Triangle, Noise: one tab each, the plugin's own editor in every tab) share one emulator and are played by a MIDI keyboard and/or the
    /// computer keyboard; the sound goes to a WASAPI output. The engine is <see cref="SynthRack"/>; this is the window, the devices and the keys.
    /// </summary>
    internal sealed class SynthForm : Form, IMessageFilter
    {
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, GA_ROOT = 2;
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        /// <summary>The plugin editor's size (Bn2Editor.Width x Height); the real size is read from its window once it exists.</summary>
        private const int EditorW = 904, EditorH = 664;

        /// <summary>The computer keyboard as a piano, in the layout most DAWs use: A is C, W is C#, S is D, E is D#, D is E ... P is D# and ';' is E, an octave up from the start.</summary>
        private static readonly Keys[] PianoKeys =
            { Keys.A, Keys.W, Keys.S, Keys.E, Keys.D, Keys.F, Keys.T, Keys.G, Keys.Y, Keys.H, Keys.U, Keys.J, Keys.K, Keys.O, Keys.L, Keys.P, Keys.OemSemicolon };

        private readonly SynthSettings settings = SynthSettings.Load();
        private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
        private readonly Panel[] editorPanels = new Panel[SynthRack.ChannelCount];
        private readonly ComboBox audioCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
        private readonly ComboBox midiCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        private readonly ComboBox routingCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        private readonly CheckBox keyboardCheck = new() { Text = "Computer keyboard plays notes", AutoSize = true };
        private readonly Label octaveLabel = new() { AutoSize = true };
        private readonly ToolStripStatusLabel statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripProgressBar meter = new() { Minimum = 0, Maximum = 100, Width = 90 };
        private readonly System.Windows.Forms.Timer ticker = new() { Interval = 100 };

        private SynthRack? rack;
        private MidiRouter? router;
        private SynthAudioOutput? audio;
        private SynthMidiInput? midiIn;
        private string? audioError, midiError;
        private readonly Dictionary<Keys, (int Channel, int Note)> down = new();   // computer keys held, and the note each started
        private bool discardState;       // "reset settings": do not write the state back on the way out
        private bool engineStarted;
        private int tickCount;

        public SynthForm()
        {
            Text = "BrokenNes 2 - Standalone Synthesizer";
            AutoScaleMode = AutoScaleMode.None;   // the plugin draws its editor in raw pixels: keep the frames in raw pixels too
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont ?? Font;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon; } catch (Exception) { /* the default icon will do */ }

            var menu = new MenuStrip();
            var synth = new ToolStripMenuItem("&Synth");
            synth.DropDownItems.Add(new ToolStripMenuItem("Restart as &Emulator", null, (s, e) => SynthMode.RestartAsEmulator(this)));
            synth.DropDownItems.Add(new ToolStripMenuItem("&Install to FL Studio...", null, (s, e) => InstallVstFlow.RunInteractive(this)));
            synth.DropDownItems.Add(new ToolStripSeparator());
            synth.DropDownItems.Add(new ToolStripMenuItem("&Reset synth settings and restart", null, (s, e) => ResetSettings()));
            synth.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => Close()) { ShortcutKeys = Keys.Alt | Keys.F4 });
            menu.Items.Add(synth);
            MainMenuStrip = menu;

            // the bar: devices, routing, the computer keyboard
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 6, 8, 4) };
            Label L(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 6, 4, 0) };
            bar.Controls.AddRange(new Control[] { L("Audio output"), audioCombo, L("MIDI input"), midiCombo, L("MIDI routing"), routingCombo });
            keyboardCheck.Margin = new Padding(8, 6, 4, 0); octaveLabel.Margin = new Padding(4, 6, 4, 0);
            bar.Controls.AddRange(new Control[] { keyboardCheck, octaveLabel });

            for (int i = 0; i < SynthRack.ChannelCount; i++)
            {
                var page = new TabPage(SynthRack.ChannelNames[i]) { AutoScroll = true, BackColor = Color.Black, Name = "tab" + i };
                editorPanels[i] = new Panel { Location = new Point(0, 0), Size = new Size(EditorW, EditorH), BackColor = Color.Black, Name = "editor" + i };
                page.Controls.Add(editorPanels[i]);
                tabs.TabPages.Add(page);
            }

            var status = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = false };
            status.Items.AddRange(new ToolStripItem[] { statusLabel, new ToolStripStatusLabel("Level"), meter });

            Controls.Add(tabs);
            Controls.Add(status);
            Controls.Add(bar);
            Controls.Add(menu);   // WinForms docks the last control added first: menu on top, then the bar, the status strip, and the tabs fill the rest

            // wide enough for the editor and tall enough for the bar, the tab headers and the status strip; the screen may be smaller (tabs scroll)
            var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 1024);
            ClientSize = new Size(Math.Min(EditorW + 24, work.Width - 40), Math.Min(EditorH + 150, work.Height - 80));

            audioCombo.SelectedIndexChanged += (s, e) => { if (engineStarted) ChangeAudioDevice(); };
            midiCombo.SelectedIndexChanged += (s, e) => { if (engineStarted) ChangeMidiDevice(); };
            routingCombo.Items.AddRange(new object[] { "MIDI channel 1-4 plays Pulse 1, Pulse 2, Triangle, Noise", "All MIDI plays the selected tab" });
            routingCombo.SelectedIndexChanged += (s, e) => { settings.Routing = routingCombo.SelectedIndex == 1 ? MidiRouting.AllToSelected : MidiRouting.ByMidiChannel; if (router != null) router.Routing = settings.Routing; };
            keyboardCheck.CheckedChanged += (s, e) => { settings.ComputerKeyboard = keyboardCheck.Checked; if (!keyboardCheck.Checked) ReleaseComputerKeys(); };
            tabs.SelectedIndexChanged += (s, e) => { settings.Tab = tabs.SelectedIndex; if (router != null) router.Selected = tabs.SelectedIndex; };
            ticker.Tick += (s, e) => Tick();

            Application.AddMessageFilter(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { StartEngine(); }
            catch (Exception ex) { FailAndReturnToEmulator(ex); }
        }

        // ---------------------------------------------------------------------------------------------------- engine

        private void StartEngine()
        {
            string dll = PluginInstaller.FindPluginDll()
                ?? throw new FileNotFoundException("The FL Studio plugin (Plugin\\" + PluginInstaller.PluginFile + ") is not next to the program, and the standalone synth is that plugin. " +
                                                   "Build it with Plugin\\build-dist.ps1 and rebuild the desktop app.");
            int rate = 48000;
            try { rate = SynthAudioOutput.RateOf(settings.AudioDevice); } catch (Exception ex) { audioError = ex.Message; }

            rack = new SynthRack(dll, rate);
            if (SynthSettings.LoadStates() is { } saved) rack.LoadStates(saved);
            router = new MidiRouter(rack) { Routing = settings.Routing, Selected = Math.Clamp(settings.Tab, 0, SynthRack.ChannelCount - 1) };

            FillDevices();
            routingCombo.SelectedIndex = settings.Routing == MidiRouting.AllToSelected ? 1 : 0;
            keyboardCheck.Checked = settings.ComputerKeyboard;
            tabs.SelectedIndex = router.Selected;
            UpdateOctaveLabel();

            // the plugin's editors: one per tab, each in its own panel (the first one may open the plugin's About window, once per machine)
            for (int i = 0; i < SynthRack.ChannelCount; i++)
            {
                rack.ShowEditor(i, editorPanels[i].Handle);
                if (GetClientRect(rack.EditorWindow(i), out var r) && r.Right > 0 && r.Bottom > 0) editorPanels[i].Size = new Size(r.Right, r.Bottom);
            }

            engineStarted = true;
            StartAudio();
            StartMidi();
            ticker.Start();
        }

        /// <summary>Something the synth cannot do without went wrong at start-up. Say what, then put the emulator back so the user is not left with nothing.</summary>
        private void FailAndReturnToEmulator(Exception ex)
        {
            ticker.Stop();
            MessageBox.Show(this, "The standalone synthesizer could not start:\n\n" + ex.Message + "\n\nThe emulator is being started instead.",
                "BrokenNes 2 - Standalone Synthesizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            discardState = true;   // nothing was loaded, so there is nothing worth saving
            try { SelfLaunch.Start(""); } catch (Exception) { }
            Close();
        }

        private void FillDevices()
        {
            audioCombo.Items.Clear(); audioCombo.Items.Add("(system default)");
            try { foreach (var d in SynthAudioOutput.Devices()) audioCombo.Items.Add(d); } catch (Exception ex) { audioError ??= ex.Message; }
            audioCombo.SelectedIndex = Math.Max(0, audioCombo.Items.IndexOf(settings.AudioDevice.Length == 0 ? "(system default)" : settings.AudioDevice));

            midiCombo.Items.Clear(); midiCombo.Items.Add("(none)");
            List<string> midi = new();
            try { midi = SynthMidiInput.Devices(); } catch (Exception ex) { midiError = ex.Message; }
            foreach (var d in midi) midiCombo.Items.Add(d);
            // remembered device; nothing remembered means the first one found (someone with a keyboard plugged in expects it to work)
            int pick = 0;
            if (settings.MidiDevice != SynthSettings.NoMidi)
            {
                int remembered = settings.MidiDevice.Length > 0 ? midiCombo.Items.IndexOf(settings.MidiDevice) : -1;
                pick = remembered > 0 ? remembered : midi.Count > 0 ? 1 : 0;
            }
            midiCombo.SelectedIndex = pick;
        }

        private void StartAudio()
        {
            audio?.Dispose(); audio = null; audioError = null;
            if (rack == null) return;
            rack.Accepting = false;   // no device yet: nothing would play, nothing would drain the queue
            try { audio = new SynthAudioOutput(rack, settings.AudioDevice); rack.Accepting = true; }
            catch (Exception ex) { audioError = ex.Message; }   // shown on the status line; pick another output and it starts
        }

        private void ChangeAudioDevice()
        {
            settings.AudioDevice = audioCombo.SelectedIndex <= 0 ? "" : (string)audioCombo.SelectedItem!;
            StartAudio();
        }

        private void StartMidi()
        {
            midiIn?.Dispose(); midiIn = null; midiError = null;
            if (router == null || midiCombo.SelectedIndex <= 0) return;
            try
            {
                // the combo lists devices in driver order after "(none)", so its index is the device number
                midiIn = new SynthMidiInput(midiCombo.SelectedIndex - 1, router);
            }
            catch (Exception ex) { midiError = ex.Message; }
        }

        private void ChangeMidiDevice()
        {
            settings.MidiDevice = midiCombo.SelectedIndex <= 0 ? SynthSettings.NoMidi : (string)midiCombo.SelectedItem!;
            StartMidi();
        }

        private void Tick()
        {
            if (rack == null || router == null) return;
            float peak = rack.ReadPeak();
            meter.Value = Math.Clamp((int)(peak * 100), 0, 100);
            string a = audio != null ? $"{audio.DeviceName}, {audio.SampleRate} Hz" : $"none ({audioError ?? "starting"})";
            if (audio?.Error is { } engineError) a = engineError;
            string m = midiIn != null ? midiIn.DeviceName : midiError != null ? $"none ({midiError})" : "none";
            string last = router.LastEvent.Length > 0 ? router.LastEvent : "no notes yet";
            statusLabel.Text = $"Audio: {a}   |   MIDI: {m}   |   {last}   |   level {peak:0.00}";
            if (++tickCount % 300 == 0) SaveState();   // every 30 s: a crash (or a killed process) loses little
        }

        private void SaveState()
        {
            if (rack == null || discardState) return;
            try { SynthSettings.SaveStates(rack.SaveStates()); settings.Save(); } catch (Exception) { /* settings are a convenience: never worth an error box */ }
        }

        private void ResetSettings()
        {
            if (MessageBox.Show(this, "Forget the synth's saved settings (sound chips, volumes, games, devices) and start fresh?", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            discardState = true;
            SynthSettings.ForgetStates();
            try { File.Delete(SynthSettings.SettingsPath); } catch (Exception) { }
            SelfLaunch.Start(SynthMode.Arg);
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            Application.RemoveMessageFilter(this);
            ticker.Stop();
            ReleaseComputerKeys();
            midiIn?.Dispose(); midiIn = null;      // sound sources first: nothing may render, or arrive, while the rack is taken apart
            audio?.Dispose(); audio = null;
            if (rack != null)
            {
                SaveState();
                for (int i = 0; i < SynthRack.ChannelCount; i++) { try { rack.HideEditor(i); } catch (Exception) { } }
                rack.Dispose();
                rack = null;
            }
            if (!discardState) settings.Save();
        }

        // ---------------------------------------------------------------------------------------------------- the computer keyboard

        private void UpdateOctaveLabel() => octaveLabel.Text = $"Keys A..; = C{4 + settings.Octave}  (Z / X: octave)";

        private static int PianoIndex(Keys key) => Array.IndexOf(PianoKeys, key);

        private void ReleaseComputerKeys()
        {
            foreach (var (channel, note) in down.Values) rack?.NoteOff(channel, note);
            down.Clear();
        }

        /// <summary>
        /// Sees every key press of this thread before the control that has the focus does, so the piano works whichever of the window's controls is
        /// focused, the plugin's editors included (they are native windows WinForms knows nothing about). Only keys that mean something here are
        /// taken; Ctrl / Alt combinations, other windows (the plugin's own dialogs) and everything else pass through untouched.
        /// </summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN && m.Msg != WM_KEYUP) return false;
            if (!IsHandleCreated || rack == null || GetAncestor(m.HWnd, GA_ROOT) != Handle) return false;
            var key = (Keys)(m.WParam.ToInt64() & 0xFFFF);

            if (m.Msg == WM_KEYUP)
            {
                if (!down.Remove(key, out var started)) return false;
                rack.NoteOff(started.Channel, started.Note);   // the note that key started, even if the octave or the tab changed since
                return true;
            }

            if (!settings.ComputerKeyboard || (ModifierKeys & (Keys.Control | Keys.Alt)) != 0) return false;
            bool repeat = ((m.LParam.ToInt64() >> 30) & 1) != 0;
            if (key is Keys.Z or Keys.X)
            {
                if (!repeat) { settings.Octave = Math.Clamp(settings.Octave + (key == Keys.X ? 1 : -1), -3, 3); UpdateOctaveLabel(); }
                return true;
            }
            int index = PianoIndex(key);
            if (index < 0) return false;
            if (repeat || down.ContainsKey(key)) return true;   // auto-repeat: the note is already sounding
            int note = Math.Clamp(60 + 12 * settings.Octave + index, 0, 127);
            int channel = tabs.SelectedIndex;
            down[key] = (channel, note);
            rack.NoteOn(channel, note, 0.8f);
            router?.Report($"{SynthRack.ChannelNames[channel]}: key {key} plays note {note}");
            return true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            ReleaseComputerKeys();   // the key-up goes to another window: do not leave notes hanging
        }
    }
}
