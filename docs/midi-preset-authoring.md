# MIDI layout preset authoring (manual JSON)

This guide explains how to create or edit a **device layout preset** by hand, without the MIDI Setup constructor UI.

A layout preset describes:

1. **Which MIDI ports** it applies to (`deviceMatch`)
2. **Blueprint geometry** — nested areas + controls on a grid
3. **Optional factory hardware map** — CC / Note / Pitch Bend so Learn is not required

It does **not** store Sonar channel assignments. Those live in `%LocalAppData%\Lixetron\SonarQuickMixer\midi-mappings.json` and are set in the app (or by editing that file separately).

Reference implementation: [`Presets/m-vave-smc-mixer.json`](../steelseries-sonar-tray/Presets/m-vave-smc-mixer.json)  
Schema types: [`Midi/MidiLayoutModels.cs`](../steelseries-sonar-tray/Midi/MidiLayoutModels.cs)

---

## Where files live

| Kind | Path | Role |
|------|------|------|
| Official (shipped with the app) | `<install>\Presets\*.json` | Built-in layouts; always listed in the preset picker |
| User / DIY | `%LocalAppData%\Lixetron\SonarQuickMixer\UserPresets\*.json` | **Several** custom layouts per device are allowed |
| Active selection | `UserPresets\midi-preset-selection.json` | Remembers which preset is active per product (do not edit layouts into this file) |

Only `*.json` layout files in the **top level** of those folders are loaded (no subfolders). The selection file is reserved and ignored as a layout.

Import rejects invalid JSON and shows the syntax error with **line and position**; semantic problems (duplicate ids, missing `regionId`, …) are reported as plain messages.

In **MIDI Setup**, after selecting a device, the **right** workspace header has:

- a **Layout preset** combo (official + all matching user files — pick official from the list);
- **Save as…** (new named user preset from the current layout);
- **Rename…** (change the display `name` of the selected user preset in place; official is read-only);
- **Export…** / **Import…** (import always creates a **new** user preset and selects it);
- **Delete** (removes the selected user JSON).

The left column is only the MIDI device list.

Saving from the layout constructor **overwrites** the active user preset, or **creates a new** user file when you were editing the official/generic layout. **Save as…** always creates a separately named user preset. **Rename…** only updates `name` in the existing user file (filename stays the same).

In normal (non-constructor) mode, channel / mode / action / LED feedback changes are **staged** on the blueprint until you click **Save changes** (or **Discard**).

---

## JSON conventions

- **camelCase** property names (`deviceMatch`, `parentRegionId`, `isPitchBend`, …)
- Enums as **camelCase strings** (`"fader"`, `"relative"`, `"spaceBetween"`, …)
- Encoding: UTF-8
- Trailing commas are **not** allowed (strict JSON)

Invalid or unreadable files are skipped silently.

---

## Minimal working example

A 1×2 strip with one fader (absolute CC 7) and one mute button (Note 16):

```json
{
  "name": "My Pad",
  "deviceMatch": [ "My Controller" ],
  "hint": "Absolute fader on CC7, mute on Note 16.",
  "columns": 2,
  "rows": 1,
  "regions": [],
  "controls": [
    {
      "id": "f1",
      "row": 0,
      "col": 0,
      "type": "fader",
      "label": "Vol",
      "defaultMode": "absolute",
      "controller": 7
    },
    {
      "id": "m1",
      "row": 0,
      "col": 1,
      "type": "button",
      "label": "M",
      "controller": 16,
      "isNote": true,
      "defaultAction": "muteToggle"
    }
  ]
}
```

Save as e.g. `%LocalAppData%\Lixetron\SonarQuickMixer\UserPresets\my-pad.json`.  
Enable a MIDI port whose name contains `My Controller`, open MIDI Setup, assign Sonar channels.

---

## Root object (`MidiDeviceLayout`)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | recommended | Display name; also used as fallback match if `deviceMatch` is empty |
| `deviceMatch` | string[] | recommended | Substrings matched against the Windows MIDI port name (case-insensitive, either side may contain the other) |
| `hint` | string | no | Tip text in MIDI Setup |
| `columns` | int | no | Canvas width metadata (auto-synced from root children; keep ≥1) |
| `rows` | int | no | Canvas height metadata (same) |
| `regions` | object[] | no | Nested areas; omit or `[]` for a flat grid of controls |
| `controls` | object[] | yes | Blueprint controls (faders / encoders / buttons) |

### Device matching

A layout matches a port when **any** `deviceMatch` entry satisfies:

```text
portName.Contains(match)  OR  match.Contains(portName)
```

(case-insensitive), or when the port name contains `name`.

Tips:

- Prefer short unique fragments (`"SMC-Mixer"`, `"nanoKONTROL"`) over full path-like names.
- List several aliases if Windows reports different names for the same box / ports.
- Several user presets may match the same device; pick one in MIDI Setup. They do not merge with the official layout.

---

## Areas (`regions` → `MidiLayoutRegion`)

Areas are nestable grid cells. Controls (and child areas) sit in their parent’s local grid.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `id` | string | — | **Unique** id; referenced by `parentRegionId` / `regionId` |
| `parentRegionId` | string \| null | `null` | Parent area; `null` = root canvas |
| `label` | string | `""` | Optional chrome label |
| `row`, `col` | int | `0` | Cell in the **parent** grid (0-based) |
| `rowSpan`, `colSpan` | int | `1` | How many cells the area occupies |
| `hideBorder` | bool | `false` | No solid border in normal view (constructor still shows a dashed outline) |
| `keepSpacing` | bool | `false` | With `hideBorder`, keep a modest gap so strips/transport stay separated |
| `contentJustify` | enum | `"pack"` | Horizontal free-space distribution of children |
| `contentAlign` | enum | `"pack"` | Vertical free-space distribution of children |

### `contentJustify` / `contentAlign`

| Value | Meaning |
|-------|---------|
| `pack` | Children packed at start (left / top) |
| `spaceBetween` | Equal gaps **between** children; no extra gap at edges |
| `spaceEvenly` | Equal gaps between children **and** at both edges |

### Nesting rules

- Do not create cycles (`A` parent of `B` parent of `A`).
- Every `parentRegionId` / `regionId` must point to an existing `regions[].id` (or be omitted for root).
- Sibling `row`/`col` placements should not intentionally stack two items on the exact same cell unless you mean overlapping chrome (usually avoid).

Example skeleton (chassis → row of strips):

```json
"regions": [
  { "id": "chassis", "row": 0, "col": 0, "colSpan": 8, "rowSpan": 2, "hideBorder": true },
  { "id": "strips", "parentRegionId": "chassis", "row": 0, "col": 0, "colSpan": 8, "hideBorder": true },
  { "id": "strip1", "parentRegionId": "strips", "row": 0, "col": 0, "hideBorder": true, "keepSpacing": true },
  { "id": "strip2", "parentRegionId": "strips", "row": 0, "col": 1, "hideBorder": true, "keepSpacing": true }
]
```

---

## Controls (`controls` → `MidiLayoutControl`)

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `id` | string | — | **Unique** stable id (used as `controlId` in mappings) |
| `regionId` | string \| null | `null` | Parent area; `null` = root canvas |
| `row`, `col` | int | `0` | Cell in parent grid |
| `rowSpan`, `colSpan` | int | `1` | Span in parent grid |
| `type` | enum | `"fader"` | Visual + default behaviour: `fader` \| `encoder` \| `button` |
| `label` | string | `""` | Caption on the blueprint |
| `defaultMode` | enum \| null | inferred | `absolute` \| `relative` — factory / Learn default |
| `controller` | int \| null | `null` | Factory hardware number (see below). Omit = Learn / discover required |
| `isNote` | bool | `false` | Hardware is a MIDI Note (not CC) |
| `isPitchBend` | bool | `false` | Hardware is Pitch Bend; `controller` is MIDI channel index **0–15** (status `E0`–`EF`) |
| `relativeEncoding` | enum \| null | `offsetBinary` when relative | `offsetBinary` \| `twosComplement` |
| `defaultAction` | enum \| null | `none` | `none` \| `volume` \| `muteToggle`. Official presets omit this. Assign in MIDI Setup. |
| `feedback` | object \| null | none | Host→device feedback (mute / channel select / blink); see [Hardware feedback](#hardware-feedback-mute-leds-channel-select-blink) |

### Type defaults (when optional fields are omitted)

| `type` | Default mode | Default action |
|--------|--------------|----------------|
| `fader` | `absolute` | `none` |
| `encoder` | `relative` | `none` |
| `button` | `absolute` | `none` |

Pitch Bend always forces mode `absolute` when building factory bindings.

### Factory hardware (`controller` + flags)

If `controller` is set (0–127), the app can **seed** a binding automatically when the device is enabled / reset:

| Message | Flags | `controller` meaning |
|---------|-------|----------------------|
| Control Change | (neither flag) | CC number 0–127 |
| Note | `"isNote": true` | Note number 0–127 |
| Pitch Bend | `"isPitchBend": true` | Channel index 0–15 → status `E0`+n |

Seeded bindings start with **no Sonar channel** (`channelId` empty). The user assigns Game / Chat / … in MIDI Setup.

Controls **without** `controller` still appear on the blueprint; the user must Learn or move the hardware for auto-discover.

Do **not** put Sonar channel ids in the layout JSON — they are not part of this schema.

---

## Value modes (runtime meaning)

These affect how MIDI is turned into Sonar volume after the control is bound:

| Mode | Typical hardware | Behaviour |
|------|------------------|-----------|
| `absolute` | Fader, absolute knob, Pitch Bend | Position maps to 0…1 (`raw/127`, or 14-bit for Pitch Bend) |
| `relative` | Endless encoder | Ticks × step (app setting / binding) change current volume |

### Relative encodings

| Encoding | Tick rule (common) |
|----------|--------------------|
| `offsetBinary` | 1…63 = +, 65…127 = − (64 / 0 idle) |
| `twosComplement` | 1…64 = +, 65…127 as signed − |

---

## Nested strip example (one channel)

```json
{
  "id": "f1",
  "regionId": "strip1",
  "row": 0,
  "col": 0,
  "rowSpan": 4,
  "type": "fader",
  "label": "CH1",
  "defaultMode": "absolute",
  "controller": 0,
  "isPitchBend": true
},
{
  "id": "m1",
  "regionId": "strip1",
  "row": 0,
  "col": 1,
  "type": "button",
  "label": "M",
  "controller": 16,
  "isNote": true,
  "defaultAction": "muteToggle"
}
```

Pitch Bend channel `0` → wire status `E0` (CH1 on SMC-Mixer **DAW Mode / Mode A**, Mackie-style). Note `16` → mute pad for that strip.

### Hardware feedback (mute LEDs, channel select, blink)

Optional per-control `feedback` tells the app to **send MIDI out** when Sonar / assignment state changes (config-driven — no device brand hardcoding).

```json
"feedback": { "source": "mute" }
"feedback": { "source": "mute", "style": "blink" }
"feedback": { "source": "channelAssigned" }
```

| `source` | Lamp when… |
|----------|------------|
| `mute` | Assigned Sonar channel is muted |
| `channelAssigned` | Pads: lamp on when a Sonar channel is assigned. **Pitch Bend faders:** soft-takeover — host sends Sonar volume as Pitch Bend; the strip LED blinks only while that differs from the physical fader (move fader to match / turn Off to clear). Never a steady “always on” lamp — the hardware has no solid mode for these LEDs. |
| omit / none | No host→device lamp |

| `style` | While condition is active |
|---------|---------------------------|
| `solid` (default) | Steady on (pads). Fader match LEDs on many MCU-like surfaces only blink today. |
| `blink` | Toggle on/off ~400 ms (useful for mute on pads; on faders toggles match/mismatch) |

With only `source`, on/off messages default as follows (values **127 / 0**, MIDI channel **1** for notes):

- **Note / CC buttons:** same controller as the input identity
- **Pitch Bend faders:** host→device **Pitch Bend** on the fader’s MIDI channel (strip 0 → ch 1). Runtime soft-takeover sends the **Sonar** level (LED blinks only while ≠ physical). Off / idle echoes the last physical position to extinguish. Do not use intentional “opposite extreme” mismatch — that makes LEDs blink forever.

Explicit messages (e.g. CC mode button that lights via a different Note):

```json
"feedback": {
  "source": "mute",
  "style": "solid",
  "on":  { "kind": "note", "controller": 16, "value": 127, "channel": 1 },
  "off": { "kind": "note", "controller": 16, "value": 0,   "channel": 1 }
}
```

In MIDI Setup (normal mode), select any control and use **LED feedback** as two dropdowns: **source** (Off / On mute / On channel selected) and **style** (Solid / Blink). Faders only offer Off / Channel assigned (soft takeover); style is not used — the surface blinks while Sonar ≠ physical fader. Changes are **staged** like channel assignments — use **Save changes** / **Discard**. Hardware lamps update only after Save. In the layout constructor the same options appear under Label (saved with **Save layout**).

Official shipping presets (e.g. SMC-Mixer) intentionally omit `feedback` — they only bake hardware identity so Learn is unnecessary. Enable mute/channel LEDs in MIDI Setup on a user preset copy if you want them.

### M-VAVE SMC-Mixer modes (official preset)

Hold **Shift** and use the bottom arrow buttons:

| Gesture | Honest name | Role |
|---------|-------------|------|
| **Shift + ←** | **DAW Mode** (also “Mode A”) | Fixed **Mackie Control** map — what [`m-vave-smc-mixer.json`](../steelseries-sonar-tray/Presets/m-vave-smc-mixer.json) bakes |
| **Shift + →** | **CC / User Mode** (also “Mode B”) | Editable in MidiSuite; not the official factory map |

Do not call the Pitch Bend + MCU Notes map “Mode B”: on the hardware that label belongs to the right-arrow CC mode.

---

## Workflow checklist

1. Note the exact MIDI port name in Windows / MIDI Setup.
2. Create `UserPresets\<something>.json` with `deviceMatch` covering that name.
3. Sketch areas (`regions`) if you need strips / transport / nested chrome; otherwise leave `regions` empty and place controls on the root grid.
4. Give every control a unique `id`.
5. Optionally bake factory hardware (`controller`, `isNote`, `isPitchBend`, `defaultMode`, …).
6. Restart app → enable device → open MIDI Setup → assign Sonar channels.
7. Verify: move hardware; absolute faders should jump Sonar; relative encoders should nudge; buttons toggle mute when routed.

### Reset behaviour

- **Restore factory layout** — deletes the **user** override file and shows the official (or generic) layout again.
- **Reset all bindings** — clears mappings for that device, then re-seeds factory hardware from the **resolved** layout (user override if present).

---

## What this file is not

| Concern | Where it lives |
|---------|----------------|
| Sonar channel / mute routes | `midi-mappings.json` |
| Last absolute fader positions | `midi-control-state.json` |
| App toggles (MIDI enabled, overlay, relative step) | `settings.json` |
| Official shipping preset | repo `Presets\` (rebuild/install to update for all users) |

Editing `midi-mappings.json` by hand is possible but easy to break; prefer MIDI Setup for channel assignment.

---

## Troubleshooting

| Symptom | Likely cause |
|---------|----------------|
| Generic empty grid instead of your layout | `deviceMatch` does not hit the port name; or JSON failed to parse |
| Official SMC layout still shows | User file does not match, or official match wins only when no user file matches — check UserPresets |
| Controls visible but no volume | No factory `controller` and not Learned; or no Sonar channel assigned |
| Two ports / MIDIIN2 oddness | Bindings are stored under a port name; enable the primary product port; secondary ports may be auto-hidden |
| Relative encoder jumps oddly | Wrong `relativeEncoding`, or mode left as `absolute` |

Validate JSON with any strict JSON parser before testing in the app.

---

## Full field quick reference

```text
MidiDeviceLayout
  name, deviceMatch[], hint?, columns?, rows?, regions[], controls[]

MidiLayoutRegion
  id, parentRegionId?, label?, row, col, rowSpan?, colSpan?,
  hideBorder?, keepSpacing?, contentJustify?, contentAlign?

MidiLayoutControl
  id, regionId?, row, col, rowSpan?, colSpan?,
  type, label?,
  defaultMode?, controller?, isNote?, isPitchBend?,
  relativeEncoding?, defaultAction?,
  feedback? { source, style?, on?, off? }
```

Enums (camelCase in JSON):

- `type`: `fader` | `encoder` | `button`
- `defaultMode` / mode: `absolute` | `relative`
- `relativeEncoding`: `offsetBinary` | `twosComplement`
- `defaultAction`: `none` | `volume` | `muteToggle` (omit / none until assigned in MIDI Setup; Action combo: faders/encoders = None|Volume, buttons = None|Mute)
- `feedback.source`: `none` | `mute` | `channelAssigned`
- `feedback.style`: `solid` | `blink`
- `feedback.on/off.kind`: `note` | `cc` | `pitchBend`
- `contentJustify` / `contentAlign`: `pack` | `spaceBetween` | `spaceEvenly`
