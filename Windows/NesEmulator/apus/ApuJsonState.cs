using System;
using System.Reflection;
using System.Text.Json;

namespace NesEmulator
{
    /// <summary>
    /// Fills an APU core's state class from the JSON a NES save holds (NES.SaveState writes every public field of it). The older cores' JSON loaders
    /// (APU, EIL, LQ, MNES, SPD, SPD2, WF) read only a hand-picked subset, so a load silently dropped about 56 fields (sequencer positions, timer counters,
    /// envelopes, the frame sequencer): the sound after a load was not the sound that was saved (bfuzz load-save-not-idempotent / undo-mismatch).
    /// Every public field whose name is in the JSON is set; a field the JSON lacks (an older state) keeps what the loader gave it.
    /// </summary>
    internal static class ApuJsonState
    {
        public static void Fill(object target, JsonElement je)
        {
            if (je.ValueKind != JsonValueKind.Object) return;
            foreach (var f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!je.TryGetProperty(f.Name, out var v)) continue;
                try
                {
                    object? x = Read(f.FieldType, v);
                    if (x != null) f.SetValue(target, x);
                }
                catch (Exception) { /* a value of another shape (an older layout): keep the loader's */ }
            }
        }

        private static object? Read(Type t, JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Null) return null;
            if (t == typeof(bool)) return v.GetBoolean();
            if (t == typeof(byte)) return v.GetByte();
            if (t == typeof(sbyte)) return v.GetSByte();
            if (t == typeof(short)) return v.GetInt16();
            if (t == typeof(ushort)) return v.GetUInt16();
            if (t == typeof(int)) return v.GetInt32();
            if (t == typeof(uint)) return v.GetUInt32();
            if (t == typeof(long)) return v.GetInt64();
            if (t == typeof(ulong)) return v.GetUInt64();
            if (t == typeof(float)) return v.GetSingle();
            if (t == typeof(double)) return v.GetDouble();
            if (t == typeof(string)) return v.GetString();
            if (t == typeof(byte[])) return v.ValueKind == JsonValueKind.String ? v.GetBytesFromBase64() : ReadArray(typeof(byte), v);
            if (t.IsArray && t.GetElementType() is Type et && (et.IsPrimitive) && v.ValueKind == JsonValueKind.Array) return ReadArray(et, v);
            return null;
        }

        private static Array ReadArray(Type et, JsonElement v)
        {
            var a = Array.CreateInstance(et, v.GetArrayLength());
            int i = 0; foreach (var e in v.EnumerateArray()) a.SetValue(Read(et, e), i++);
            return a;
        }
    }
}
