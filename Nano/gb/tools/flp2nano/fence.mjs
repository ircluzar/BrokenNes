// fence.mjs: flp2nano's result as the music pipeline's fence report (format brokennes-fence v1, the same model as the plugin's Music/Flp/FlpFence.cs:
// docs/projects/flp-music-pipeline.md section 8). Every warning convert() wrote becomes a line: "over" when something in FL is not in the song (skipped,
// no channel left), "note" when it plays differently; plus "ok" lines that show the budget. convert() itself is unchanged (its outputs are byte for byte as before).

export const FENCE_FORMAT = 'brokennes-fence';

const OVER = /skipped|no MIDI channel left|NOT converted|not converted|NOTHING/;

// proj (optional, flp.mjs's model): a skipped channel with no notes in any pattern is a note, not over (nothing of it is lost).
export function fenceOf(r, engine = 'Nano GB', proj = null) {
  const count = new Map();
  if (proj) for (const p of proj.patterns.values()) for (const n of p.notes) count.set(n.channel, (count.get(n.channel) ?? 0) + 1);
  const silent = w => { const m = /^channel (\d+) /.exec(w); return proj && m && !count.get(+m[1]); };
  const lines = [];
  const rule = w => /pan/.test(w) ? 'pan' : /slide/.test(w) ? 'notes' : /noise/.test(w) ? 'noise' : /automation|controller/.test(w) ? 'automation' : /PLAYLIST|arrangement|clip/.test(w) ? 'reader' : /chip|plugin|ROM mode|BrokenNes2/.test(w) ? 'instrument' : 'other';
  for (const w of r.warnings) lines.push({ rule: rule(w), level: OVER.test(w) && !silent(w) ? 'over' : 'note', text: silent(w) ? w + ' (it has no notes)' : w });
  const n = r.summary.length;
  if (n === 0) lines.push({ rule: 'song', level: 'over', text: `NOTHING for ${engine}: no BrokenNes2 Nano GB / NES channel` });
  else lines.push({ rule: 'instruments', level: 'ok', text: `${n} part${n === 1 ? '' : 's'} on MIDI channels (15 fit)` });
  for (const s of r.summary) if (s.bendRange > 24) lines.push({ rule: 'bend', level: 'note', text: `MIDI ch ${s.midiChannel} "${s.name}": bend range +-${s.bendRange} semitones` });
  return { format: FENCE_FORMAT, version: 1, engine, lines };
}

export function fenceText(f) {
  const c = l => f.lines.filter(x => x.level === l).length;
  const out = [`${f.engine}: ${c('over')} over the limit, ${c('note')} played differently, ${c('ok')} inside`];
  for (const lv of ['over', 'note', 'ok']) for (const l of f.lines.filter(x => x.level === lv)) out.push(`${lv === 'over' ? 'OVER ' : lv === 'note' ? 'note ' : 'ok   '}[${l.rule}] ${l.text}`);
  return out.join('\n');
}
