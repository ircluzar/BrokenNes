using System;
using System.Collections.Generic;
using BrokenNes.SynthHost;
using NAudio.Midi;

namespace BrokenNes.Windows.Synth
{
    /// <summary>A MIDI keyboard (or anything that speaks MIDI) feeding the synth: every message goes to the <see cref="MidiRouter"/>.</summary>
    internal sealed class SynthMidiInput : IDisposable
    {
        private readonly MidiIn input;

        public string DeviceName { get; }

        /// <summary>Opens and starts the input at <paramref name="index"/>. Throws when another program has the device open.</summary>
        public SynthMidiInput(int index, MidiRouter router)
        {
            DeviceName = MidiIn.DeviceInfo(index).ProductName;
            input = new MidiIn(index);
            input.MessageReceived += (s, e) => router.Handle(e.RawMessage);   // on the driver's thread: the router only queues
            input.Start();
        }

        public static List<string> Devices()
        {
            var list = new List<string>();
            for (int i = 0; i < MidiIn.NumberOfDevices; i++) list.Add(MidiIn.DeviceInfo(i).ProductName);
            return list;
        }

        public void Dispose()
        {
            try { input.Stop(); } catch (Exception) { }
            input.Dispose();   // never from inside the callback: it waits for it
        }
    }
}
