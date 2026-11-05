# Controller standard (plan, 2026-11-04)

One set of controller components for every BrokenNes panel, laid out like a VST plugin's faceplate. Corruption Slop gets the
first controller built from it; Butchering mode's effects are packed into controllers next; then a second Butchering mode
lists every effect and lets the player place them on a grid to build their own panel. (The current Butchering strip stays.)

## 1. The pieces

Everything sits on a **grid**: one cell is 56 x 56 px (the "U"), with gutters of 12 px between columns and 14 px between rows; components are whole cells, sections group them under a title. A section's frame sits in the gutter around its cells (4 px out, 9 px at the top for its title), so neighbouring frames never touch and every control keeps air on all sides; the plate pads 14 / 18 px. Small knobs are 34 px, big (2x2) 76 px. (Revised 2026-11-05 after the first look: 48 px cells and no gutter were too tight.)

| Component | Size (cells) | What it does | Interaction |
|---|---|---|---|
| **Knob** | 1x1 (small), 2x2 (big) | A continuous value | Drag up/down (Shift fine), wheel, arrow keys, double-click resets to default |
| **Stepped knob** | 1x1, 2x2 | A choice among a few values, with detents and their names | As the knob, snapping |
| **Slider** | 1x3 vertical / 3x1 horizontal | A continuous value where position matters to read (levels, mixes) | Drag along, click to jump, wheel, arrows, double-click resets |
| **Button** | 1x1 | A momentary action (Stop, Blast now, Reload) | Click, Enter / Space |
| **Toggle** | 1x1 | On / off, with its LED | Click, Enter / Space |
| **Display** | 2x1, 3x1, 4x1 | A read-out: a value, a status, a counter, in a small LCD | Read only |
| **Label** | any width x 1 | Section titles and captions | - |
| **LED** | part of a toggle, or 1x1 alone | A state light (running, intermission, fault) | Read only |

Every control shows its **label** under it and its **value** on hover / while it moves (in the control's own display line), with its unit.

## 2. The parameter behind each control

A control never talks to the engine directly: it is bound to a **parameter**, the same record for every panel.

```
{ id, label, short (max 8 chars, for small controls), min, max, default, step,
  unit ("%", "ms", "x", "Hz", ""), taper ("linear" | "log" | "exp"), steps (names, for stepped knobs),
  format(value) -> text, get() -> value, set(value) }
```

The taper keeps a knob's travel useful (a 0.4 s - 4.2 s pause on a log taper). `set` is debounced by the panel (one engine
call per frame of movement at most). Saving and loading a panel's values is the parameter's job, not the control's.

## 3. The look

- Dark faceplate, one accent colour per panel (Corruption Slop: its current orange-red), tokens on `:root` (`--plate`,
  `--plate-edge`, `--ink`, `--ink-dim`, `--accent`, `--led-on`, `--lcd-bg`, `--lcd-ink`).
- Knobs: a dark cap with a pointer line, the value as an arc around it in the accent colour (0 to the value; bipolar knobs from the centre).
- Displays: a recessed LCD strip, monospace digits.
- States: normal, hover (the arc brightens), active (dragging: the value display lights), disabled (dimmed, no arc), and later "automated" (a dot, when a value is being driven).
- Accessible: every control is an ARIA slider / button / switch with its label and value text; keyboard works everywhere.

## 4. A layout is data

A panel is a description, not hand-written markup, so the later Butchering grid mode can save and load panels:

```json
{ "title": "Corruption Slop", "cols": 8, "rows": 4, "accent": "#e0523b",
  "sections": [ { "title": "Slop", "x": 0, "y": 0, "w": 4, "h": 4 } ],
  "controls": [ { "type": "knob", "param": "intensity", "x": 0, "y": 1, "w": 2, "h": 2, "label": "Adjust until funny" } ] }
```

The library (`Webmodules/shared/controls/controls.js` + `controls.css`) builds a panel from a layout and a list of parameters.
Any webmodule can use it.

## 5. The first controller: Corruption Slop

What it does today: every 0.4 - 4.2 s it blasts random bit flips (the intensity knob scales the blast), and now and then it
takes an "intermission" (a new background and null provider, the base state reloaded). These are fixed in the script today;
the controller exposes them:

| Control | Parameter | Range | Default | Today |
|---|---|---|---|---|
| Big knob "Adjust until funny" | intensity | 0 - 100 % | 50 | the existing knob |
| Knob "Base" | base intensity (what 100 % means) | 50 - 2000, log | 420 | `BASE_INTENSITY` |
| Knob "Pace" | pause between blasts | 0.1 - 10 s, log, a min-max pair shown as one range | 0.42 - 4.2 s | `randomInt(420, 4200)` |
| Knob "Intermission" | chance of an intermission per cycle | 0 - 100 % | 15 | `INTERMISSION_CHANCE` |
| Stepped knob "Guarantee" | an intermission is certain after N cycles | 5 - 100, off | 25 | `INTERMISSION_GUARANTEE_AFTER` |
| Display | status: running / intermission / stopped | - | - | - |
| Display | cycle count, blasts so far | - | - | - |
| LED | blast (flashes on each blast) | - | - | - |
| Button "Stop" | stop and leave | - | - | the stop square |

Values are kept per player (browser storage) and shown with their units; double-click any knob to return to today's value.

## 6. Order of work

| Step | What |
|---|---|
| C1 | The library: grid, knob, stepped knob, slider, button, toggle, display, LED, sections; parameters; layout from data; a test page with every component in every state | **DONE** 9d1158f8: `Webmodules/shared/controls/` (controls.js, controls.css, demo.html) |
| C2 | Corruption Slop on it (the table above), the hidden settings made parameters | **DONE** 26fba9b3: checked in the real app (loop runs, a knob reaches the engine, Stop returns) |
| C3 | Butchering effects as controllers: each effect's settings become parameters (on, rate, duration, ...), with a default small layout per effect | **DONE**: `ButcheringMode/effect-controllers.js`; all 68 SNES effects build |
| C4 | The Butchering grid mode: the effect list (classified with the purity scores), drag onto a grid, save / load panels | **DONE**: the Build view (Strip / Build switch): list by group, sorted by purity with tier dots, drag or + onto a board of 48 px cells sized to the window, modules move by their title bar, one board kept per console; board and strip edit the same settings |
