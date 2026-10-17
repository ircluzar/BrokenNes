using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BrokenNes.CorruptorModels;
using NesEmulator.Corruption;

namespace BrokenNes
{
    /// <summary>
    /// The Glitch Harvester's entries (<see cref="HarvestEntry"/>, what the web module, the HTTP API and Lite's list show) and the stockpile file's entries (<see cref="StockpileEntry"/>, NesEmulator.Corruption), both ways.
    /// A NES entry carries its machine as the NES's savestate text; a console entry carries an <see cref="Anchor"/>; either becomes the file entry's anchor.
    /// </summary>
    public static class GhStockpileMap
    {
        public static StockpileEntry ToFile(HarvestEntry e, IEnumerable<HarvesterBaseState>? bases = null)
        {
            var f = new StockpileEntry
            {
                Id = e.Id, Name = e.Name, Description = e.Description, Created = e.Created, Tags = e.Tags.ToList(), Legacy = e.Legacy,
                Game = e.Game ?? new GameRef(), Machine = e.Machine ?? new MachineSpec(), Source = e.Source,
            };
            foreach (var w in e.Writes) f.Writes.Add(new CorruptionWrite(w.Domain, w.Address, w.Value, w.Was, w.At));
            if (e.Anchor != null) f.Anchor = e.Anchor;
            else
            {
                string state = e.State;
                if (state.Length == 0 && bases != null) state = bases.FirstOrDefault(b => b.Id == e.BaseStateId)?.State ?? "";
                if (state.Length > 0)
                {
                    f.Anchor = new Anchor { Kind = AnchorKind.State, Console = "nes", StateFormat = "nes-json", StateData = Encoding.UTF8.GetBytes(state) };
                    if (f.Game.Console.Length == 0) f.Game.Console = "nes";
                    if (!f.Game.HasIdentity) NesStateIdentity.Fill(f.Game, state);
                }
                else f.Anchor = new Anchor { Kind = AnchorKind.None };
            }
            if (f.Game.Console.Length == 0 && f.Anchor.Kind == AnchorKind.State) f.Game.Console = f.Anchor.Console;
            return f;
        }

        public static HarvestEntry FromFile(StockpileEntry f, Stockpile? file = null)
        {
            var e = new HarvestEntry
            {
                Id = f.Id, Name = f.Name, Description = f.Description, Created = f.Created, Tags = f.Tags.ToList(), Legacy = f.Legacy,
                Game = f.Game, Machine = f.Machine, Source = f.Source,
            };
            foreach (var w in f.Writes) e.Writes.Add(new BlastInstruction { Domain = w.Domain, Address = w.Address, Value = w.Value, Was = w.Was, At = w.At });
            var anchor = file?.Resolve(f.Anchor) ?? (f.Anchor.Kind == AnchorKind.Ref ? null : f.Anchor);
            if (anchor != null && anchor.Kind == AnchorKind.State && anchor.StateFormat == "nes-json" && anchor.Alt == null)
                e.State = Encoding.UTF8.GetString(anchor.StateData);      // the NES path replays from this text, as it always has
            else e.Anchor = anchor;
            return e;
        }
    }
}
