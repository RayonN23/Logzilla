# Logzilla UI redesign — implementation brief

You are redesigning the UI of Logzilla, a desktop log viewer. The parsing, indexing,
filtering and search engine already work. **This is a presentation-layer job only.**

## Reference render

`logzilla-mockup.html` sits next to this file. It is a **pixel-exact static render of the
target design** — open it in a browser to see it, and read its source for any value this
brief doesn't spell out. Every colour in it resolves through a CSS custom property whose
name matches the token table below, so you can read the intended token for any element by
looking at its `style` attribute.

It contains two boards:

1. **Main window**, 1440 × 920 — the whole redesigned shell
2. **Row anatomy**, 1040 × 620 — the log row at 2× in four states, plus the severity ramp
   and type specimens

The mockup is a reference, not a source to port. It is one flat HTML file with inline
styles and no behaviour; do not copy its markup into the app.

## Stack

**WinForms on .NET 10. `System.*` only — no NuGet packages, no new dependencies.**
Everything below is achievable inside that constraint. Do not propose adding a package.

### Start here: free dark mode

In .NET 10, `Application.SetColorMode` is no longer experimental — the `WFO5001` compiler
error is gone and controls follow the app's colour mode. One line in `Program.cs`:

```csharp
ApplicationConfiguration.Initialize();
Application.SetColorMode(SystemColorMode.Dark);
Application.Run(new MainForm());
```

That darkens the title bar, menus, dialogs, scrollbars and common controls for free, which
is most of what the "window chrome doesn't match the app" complaint is about. Three things
to know:

- **Win32 common controls hosted inside your own custom-drawn controls** — scrollbars in
  particular — stay light unless you opt into the theme before `CreateParams` is read.
- **`FlatAppearance` on `Button` is ignored in dark mode** (known WinForms issue). So the
  severity chips and segmented control must be owner-drawn, not `FlatStyle.Flat` buttons
  with `FlatAppearance.BorderColor`. Plan for that from the start.
- **The designer does not preview dark mode.** You'll be running the app to see it.

### Token layer

`Theme.cs` sits next to this brief. Drop it in, adjust the namespace, and use it as-is for
step 1. It carries every colour, font and metric in this document. Nothing else in the UI
should construct a `Color`, a `Font`, or a layout constant.

### Type — revised for Windows

The browser mockup uses IBM Plex. **Do not try to embed it.** It isn't installed on
Windows, and `PrivateFontCollection` fonts don't resolve reliably under GDI /
`TextRenderer` without `AddFontMemResourceEx` P/Invoke. Use what ships:

- **UI:** Segoe UI Variable Text → Segoe UI → Tahoma
- **Data:** Cascadia Mono → Consolas → Courier New

`Theme.cs` picks the first installed family in each chain. The **sizes and weights** in this
brief are the real spec; the family names are not. Fonts are constructed with
`GraphicsUnit.Pixel` so every px value here transfers 1:1. Because all data is in a
monospace family, tabular numerals come for free — no OpenType feature plumbing.

### Control mapping

| This brief | WinForms |
|---|---|
| Token layer | `Theme.cs`, provided. |
| Log list | A custom `Control` with `OnPaint`, if it isn't one already. The dimmed prefix, the per-run search highlight and the inline expanded row all need manual run painting, which `ListView` owner-draw makes miserable. If you must stay on `ListView`, it needs `VirtualMode = true` **and** `OwnerDraw = true`. |
| Row text | `TextRenderer.DrawText`, **not** `Graphics.DrawString`. Use `Theme.TextFlags`. GDI+ ClearType on a dark ground reads muddy and is much slower across thousands of short runs. |
| Per-run colour (prefix / tail / match) | Measure each run with `TextRenderer.MeasureText` using the *same* flags, advance an x offset, draw each run separately. Cache the measured prefix width per row — re-measuring on every paint will show up in live tail. |
| Ellipsis | `TextFormatFlags.EndEllipsis` (`Theme.TextFlagsEllipsis`). |
| **Flicker** | `SetStyle(ControlStyles.AllPaintingInWmPaint \| UserPaint \| OptimizedDoubleBuffer, true)` on every custom control, and subclass `Panel` to set `DoubleBuffered = true`. This is the single biggest cause of a dense dark WinForms UI looking cheap. Do it before anything else. |
| Severity chips | Owner-drawn. A `Control` subclass holding an `IsOn` flag, painting fill + border + dot in `OnPaint`, raising `Click`. Not `FlatAppearance`. |
| Segmented control | **One** control drawing both segments, not two adjacent buttons. |
| Timeline histogram | Custom `Control`. 88 buckets is nothing — fill `Rectangle`s. Render the bars into a cached `Bitmap`, invalidate it only when the bucket data changes, and draw the crosshair, tooltip and viewport band live on top. |
| Minimap | Same pattern: cached bitmap of ticks + live viewport overlay. |
| Inspector rows | Custom-painted rows in a double-buffered `Panel` is simplest and gives you the dot leader for free. If you use `Label`s, set `AutoSize = false`, `TextAlign = ContentAlignment.MiddleRight`, and anchor value labels to `AnchorStyles.Right` — this is what fixes the clipping. |
| File tabs | A `Panel` with custom-drawn tab rects. Do **not** use `TabControl`; owner-drawing its strip leaves system-coloured gaps. |
| Title bar | `SetColorMode` first. Only go to `FormBorderStyle.None` + `WM_NCHITTEST` if you decide the tab strip must sit inline with the caption — that's the last and riskiest step, and the design still works without it. |
| DPI | `PerMonitorV2`. Every constant in `Theme.cs` is at 96 DPI; call `Theme.Scale(value, DeviceDpi)` at paint time. Never store a scaled value. |
| Accessible names | `AccessibleName` on every control. Custom-painted hit regions need `AccessibleObject` subclasses if screen-reader support matters. |

Everything from here down is framework-neutral unless marked.

---

## The problem

The current window is dense and technically complete, but the visual layer fights the
data. Eight specific failures, in the order they hurt:

1. **Text is clipped everywhere.** Toolbar buttons show "Highligh" and "Follo". Panel tabs
   show "Summar" and "Detai". Severity pills show "FATA", "ERRO", "WAR". Worst of all, the
   *values* in the summary panel run off the right edge — entry counts, file size, rate,
   severity counts. A summary panel whose numbers can't be read is worse than no panel.
2. **INFO is rendered at full brightness** and it is ~97% of every file. The single ERROR
   line has to compete with 8,000 identically-weighted neighbours.
3. **Timestamps repeat at full brightness** and consume ~25% of the row width while only
   the milliseconds vary between adjacent lines.
4. **Severity colour encodes category, not filter state.** All pills are fully saturated
   and lit at once, so colour tells you nothing about what's actually being shown.
5. **Long lines vanish off the right edge** with no ellipsis, no wrap, no expand.
6. **The timeline histogram is monochrome**, so it cannot show *where* errors are — which
   is the only reason to have a timeline. (This is why a "busiest error windows" text list
   had to exist as a workaround. Keep the list; it's good. But the timeline should carry
   the same information visually.)
7. **The toolbar is one undifferentiated row** of same-size controls mixing search,
   highlight, context, time filter, severity filter and view mode, with two unlabelled
   buttons (`.` and `A`).
8. **Window chrome doesn't match the app.** A bright OS-blue title bar sits above a
   near-black body; the dark theme stops at the client area.

---

## Design tokens

Introduce these as a single token layer (CSS custom properties, a resource dictionary, a
theme struct — whatever matches the stack). **Nothing below this line should hardcode a
colour.**

### Surfaces

| Token | Value | Used for |
|---|---|---|
| `bg-data` | `#0E1114` | The log list. Deliberately the *darkest* plane — data recedes, chrome lifts. |
| `bg-chrome` | `#12161B` | Title bar, command bar, inspector panel |
| `bg-chrome-sunk` | `#10141A` | Filter bar, timeline, status bar, minimap |
| `bg-raised` | `#1A212B` | Selected row, tooltips, popovers |
| `bg-control` | `#232B36` | Active segment in a segmented control |
| `bg-chip-on` | `#1D2530` | Enabled filter chip |
| `bg-row-hover` | `#161B22` | Row hover |

### Lines

| Token | Value | Used for |
|---|---|---|
| `line` | `#1C222B` | Divider between major regions |
| `line-soft` | `#1A1F27` | Row separators, dividers inside a pane |
| `line-control` | `#222932` | Default control border |
| `line-control-strong` | `#2E3744` | Active/raised control border |

### Text

| Token | Value | Contrast on `bg-data` | Used for |
|---|---|---|---|
| `text` | `#E4E8EE` | 14.3:1 | Primary values, active labels |
| `text-2` | `#A3ABB7` | 6.4:1 | Control labels |
| `text-3` | `#8D96A3` | 6.4:1 | Timestamps, secondary values |
| `text-dim` | `#7A8390` | 4.9:1 | Line numbers, INFO tag, section heads, disabled labels |
| `text-prefix` | `#79828F` | 4.7:1 | Dimmed logger/namespace prefix inside a message |

**`#7A8390` is the floor.** Anything dimmer than that on `bg-data` fails WCAG AA for small
text. If you find greys below it in the existing code (or in my mockup — a couple of axis
labels and hints use `#646C79`), raise them to `text-dim`.

### Severity

| Level | Swatch / tag | Message text | Notes |
|---|---|---|---|
| FATAL | `#FF5C8A` | `#FFB3C6` | |
| ERROR | `#FF6F6B` | `#FFB3AF` | |
| WARN | `#F0B341` | `#F5C778` | |
| INFO | `#8B95A3` | `#C8CFD9` | **Neutral on purpose.** INFO is the absence of colour. |
| DEBUG | `#A99BF5` | `#CFC7FA` | |
| TRACE | `#7E8899` | `#B2BAC6` | |

Histogram INFO fill: `#333C49` (a fill, not text — contrast rules don't apply).

### Accent

`accent = #56C8DE`. Derive alpha variants rather than hardcoding more hexes:

- `accent-glow` = accent @ 10% — focus ring
- `accent-faint` = accent @ 12% — viewport band fill, brand mark fill, badge background
- `accent-dim` = accent @ 42% — focused field border
- `accent-edge` = accent @ 65% — selected row top/bottom edge, crosshair, viewport band edges

Accent means exactly four things: **selection, focus, viewport position, brand.** It must
never be used to encode severity or any other data dimension.

### Search match

- Non-current match: background `rgba(240,179,65,0.22)`, text `#F6D48F`, radius 2px
- Current match: background `rgba(240,179,65,0.85)`, text `#11151A`, radius 2px

### Type

Two families, no more:

- **UI: IBM Plex Sans** (or the closest available grotesque; do *not* fall back to Inter,
  Roboto or Arial)
  - Controls and buttons: 11.5px / 400
  - Section heads: 10px / 600 / `letter-spacing: 0.12em` / uppercase
  - Column heads: 9.5px / 600 / `letter-spacing: 0.1em` / uppercase
  - Severity tag in a row: 9.5px / 600 / `letter-spacing: 0.06em`
  - Wordmark: 12.5px / 600
- **Data: IBM Plex Mono**
  - Log body: 11.5px, line-height 20px
  - Panel values: 11.5px
  - Line numbers, status bar, axis labels: 10.5px

`font-variant-numeric: tabular-nums` (or the toolkit equivalent) on **every numeral that
sits in a column**: line numbers, timestamps, panel values, severity counts, error-window
counts, the status bar. Without it the columns wobble.

### Geometry

Radii: `4` inner controls · `5–6` chips and buttons · `7` input fields · `10` window.

---

## Layout

Fixed vertical stack, top to bottom. Heights are exact; the main region absorbs slack.

```
┌──────────────────────────────────────────────────────────┐
│ Title bar                                          40px  │
├──────────────────────────────────────────────────────────┤
│ Command bar (search, mode, context, tail)          52px  │
├──────────────────────────────────────────────────────────┤
│ Filter bar (severity chips, reset)                 44px  │
├──────────────────────────────────────────────────────────┤
│ Timeline (stacked histogram)                       96px  │
├───────────────────────────────────┬──────────────────────┤
│ Log list                   fluid  │ Inspector      380px │
│                                   │                      │
├───────────────────────────────────┴──────────────────────┤
│ Status bar                                         28px  │
└──────────────────────────────────────────────────────────┘
```

### Log row columns

| Column | Width | Alignment |
|---|---|---|
| Line number | 46px | right, 10px right padding |
| Severity spine | 3px | full row height, `transparent` for INFO |
| Time | 100px | left, 10px left padding |
| Level tag | 46px | left |
| Message | fluid | left, 16px right padding, ellipsis on overflow |
| Minimap | 11px | pinned to the right edge of the pane |

Row height: **20px**. The gutter/spine/time/level widths are fixed so that INFO rendering a
blank level tag does not shift the message column.

---

## Component specs

### Title bar

Dark (`bg-chrome`), not OS-blue. Left to right: brand mark (20px rounded square,
`accent-faint` fill, `accent-dim` border, containing a 3-line glyph in `accent` with a
single `#FF6F6B` dot), the wordmark, a hairline divider, then the file tabs. Window
controls (minimize / maximize / close) on the right as 1.1px-stroke SVG in `text-2`, in
34×28 hit areas.

Each tab shows a severity dot (the worst level present in that file), the filename, and
the entry count in mono `text-dim`. Kill the loose "LOGZILLA" label that currently sits in
the menu bar.

### Command bar

One group per job, separated by 1px × 20px dividers:

1. **Search field**, 560×34, `bg-data` fill, focused border `accent-dim` with a 3px
   `accent-glow` ring. Contains, inline: search icon → text input (mono 12.5px) → match
   counter (`1 of 61`, mono, `text-3`) → divider → `.*` and `Aa` toggle buttons. The two
   unlabelled buttons get `aria-label`/tooltips: "Use regular expression", "Match case".
2. **Prev / next match** — two 30×30 chevron buttons.
3. **Mode segmented control**, 30px tall: `Highlight` | `Filter to matches`. This replaces
   the ambiguous "Highligh" button — the two behaviours were never distinguishable before.
4. **Context stepper**: a `Context` label plus `− ±2 +`. It was `CTX 0` with a spinner.
5. Spacer, then on the right: `Wrap` toggle, `Live tail` (green dot + `rgba(143,214,148,…)`
   tint when on), and a `⋯` overflow button.

### Filter bar

Six severity chips, ordered FATAL → TRACE, each 26px tall:

- **On:** `bg-chip-on` fill, `#2E3846` border, filled 7px square dot in the severity colour,
  label in `text`, count in mono `text-2`.
- **Off:** transparent fill, `line-control` border, **hollow** dot (1.5px border, no fill),
  label and count in `text-dim`.
- **Zero count:** renders like off and is non-interactive.

State is carried by fill + border + dot fill. Severity colour is reduced to a 7px dot, so
it identifies the category without shouting about it.

Right side: a dashed `+ Field filter` button and a `Reset` button carrying a count badge of
how many filters are active.

**The chip counts and the status bar must agree.** If DEBUG (62) and TRACE (12) are off in
a file of 8,497, the status bar reads `8,423 of 8,497 shown · 2 severities hidden` and the
inspector's "Shown after filters" reads `8,423 / 99.1%`. This is the whole point of the
change: filter state must be legible without opening a menu.

### Timeline

96px total: a 16px label strip, a 48px chart, a 16px axis.

- **Bars are stacked by severity**, bottom to top: INFO (`#333C49`) → WARN → ERROR. Flex
  row, `gap: 1px`, `align-items: flex-end`, each column `flex-grow: 1`. This is the single
  most important change in this section — a monochrome log histogram is decorative.
- **Label strip:** `VOLUME OVER TIME` (section-head style) on the left with the bucket size
  and peak in mono next to it; the visible time range and a `Reset zoom` button on the
  right. The current "peak 862 / bucket" grey-on-black in the middle is unreadable.
- **Viewport band:** an overlay rectangle showing which slice of the file the list is
  currently displaying — `accent-faint` fill with 1px `accent-edge` left and right borders.
  There is currently no way to tell where you are.
- **Hover readout:** a 1px `accent-edge` crosshair plus a `bg-raised` card with a
  `line-control-strong` border and a soft drop shadow, showing the bucket range, the entry
  count, and the error count with its swatch.
- **Axis:** ~8 time labels in mono 9.5px `text-dim`.

### Log list

- **Date band:** a single 20px `bg-chrome` strip reading e.g. `THU 18 JUN 2026`, emitted
  whenever the date changes rather than repeating the date on every row. This is what
  reclaims the horizontal space.
- **Column header row**, 26px, column-head type, with a `columns` affordance on the right.
- **INFO rows:** spine transparent, level tag in `text-dim`, timestamp in `text-3`, message
  in `#C8CFD9`. The logger/namespace prefix (`MaintenanceCommandManager.BeforeSessionEnd:`,
  `Tcp.645 [...]:`) is split out and rendered in `text-prefix`, so the varying tail of the
  message is the brightest thing in the row.
- **WARN / ERROR rows:** coloured 3px spine, coloured level tag, brighter timestamp
  (`#B6BFCB`), tinted message.
- **Overflow:** `text-overflow: ellipsis` — an explicit, honest truncation marker. Never a
  hard clip.
- **Hover:** `bg-row-hover`.
- **Selected row expands in place:** `bg-raised` with 1px `accent-edge` top and bottom, the
  full untruncated message, a two-column field grid (`logger`, `deviceId`, `thread`,
  `commandType` — whatever the parser extracted), and an action row: `Copy as JSON`,
  `Only this <field>`, `Only this logger`, and a red-bordered `Next ERROR` carrying its
  shortcut hint. Row-level copy/bookmark icons sit at the right end of the selected row.
- **Minimap**, 11px, pinned right: 2px ticks at proportional positions, severity-coloured,
  INFO ticks in `#333C49`; plus a viewport box in `accent-faint` with `accent-edge` edges.

### Inspector

Tabs at 40px: `Summary` · `Fields` · `Patterns` · `Marks` · `⋯`. **Full words.** If they
don't fit, move the tail into the overflow menu — never truncate a tab label.

Three sections, each with a section head followed by a hairline rule.

- **VOLUME** — 8 rows at 22px: label in `text-2` on the left, a dot-leader hairline
  (`#1B212A`) filling the middle, the value right-aligned in mono `text`, and an optional
  unit/percentage suffix in mono 10px `text-dim`. Rows: Entries indexed, Shown after
  filters, Errors or worse, File size, Time span, First entry, Last entry, Average rate.
  **The value column is anchored to the panel's right padding.** This is the fix for the
  clipping.
- **SEVERITY** — six 26px rows: dot (filled when included, hollow when excluded), name,
  track + bar, count right-aligned in mono, percentage right-aligned in mono `text-dim`.
  Bars use a **log scale** (`log(count) / log(max)`), because linear makes ERROR a 3px nub
  next to INFO's full bar. Say so: a small `log scale` badge next to the section head.
  Excluded severities render the bar as a 1px outline instead of a fill. Rows are buttons
  ("click to isolate" — keep this, it's a good feature that was previously invisible).
- **BUSIEST ERROR WINDOWS** — five 26px button rows: timestamp in mono, a mini bar scaled
  to the top count, count in `#FF9B96`, the word `errors`, and a chevron. Clicking scrubs
  the timeline and the list to that window.

### Status bar

28px, `bg-chrome-sunk`. Left: filename · file size · timestamp format spelled out
(`ISO-8601 · UTC−04:00`, not `iso`). Right: an accent dot + `8,423` + `of 8,497 shown ·
2 severities hidden`, then the caret line, then a `Ctrl K` key cap + `commands`.

---

## Work plan

Ship these in order. Each is independently mergeable; don't start the next until the
previous one builds and renders.

**0 — Dark mode + double buffering.** Add `Application.SetColorMode(SystemColorMode.Dark)`
to `Program.cs`. Then add `SetStyle(ControlStyles.AllPaintingInWmPaint | UserPaint |
OptimizedDoubleBuffer, true)` to every custom-painted control, and introduce a
`BufferedPanel : Panel` that sets `DoubleBuffered = true` for use everywhere a plain
`Panel` is used today.
*Done when:* the app runs dark end to end, and resizing or fast-scrolling shows no flicker
or tearing anywhere.

**1 — Token layer.** Drop in the provided `Theme.cs`, fix the namespace, and replace every
hardcoded colour, font and layout constant in the UI with a reference to it. Do not change
any visual values yet — this step is a mechanical substitution.
*Done when:* a grep for `Color.FromArgb`, `new Font(` and `Color.` in the UI code returns
only `Theme.cs`, and the app looks exactly as it did before the step.

**2 — Un-clip everything.** Every button, tab and chip renders its full label. Every value
in the inspector is right-aligned against the panel's right padding with `min-width: 0` and
`white-space: nowrap` on the label side. Tabs that don't fit go into an overflow menu.
*Done when:* at the narrowest supported window width, no label and no numeric value in the
inspector is visually cut. Add a test that asserts each inspector value element's
`scrollWidth <= clientWidth`.

**3 — Row hierarchy.** Neutral INFO, dimmed prefix, coloured spine, date band replacing
per-row dates, ellipsis on overflow, hover state, tabular numerals.
*Done when:* on a file that is 97% INFO, a single ERROR row is findable in under a second
without scrolling assistance.

**4 — Severity chips + status bar agreement.** Rebuild the pills as stateful chips with
counts. Wire the counts to the same source of truth as the status bar and the inspector.
*Done when:* toggling any chip updates the chip's own visual state, the status bar's
"X of Y shown", and the inspector's "Shown after filters" — and all three agree.

**5 — Stacked timeline.** Severity-stacked bars, label strip, viewport band, hover readout,
axis labels.
*Done when:* the timeline visually indicates the same error clusters that "Busiest error
windows" lists.

**6 — Row expansion.** Selected-row detail with the field grid and the action row.
*Done when:* every truncated line's full content is reachable in one click, and the
`Only this <field>` actions produce the same result as typing the equivalent filter.

**7 — Command bar regrouping.** Search field with inline counter and toggles, mode
segmented control, labelled context stepper, right-hand tail/wrap group, dividers.
*Done when:* every control has a visible label or an `aria-label` + tooltip, and no two
adjacent controls belong to different jobs without a divider between them.

**8 — Window chrome.** Brand mark, custom-drawn file tabs in a `Panel`, spelled-out status
bar. Step 0 already darkened the caption, so a fully custom title bar (`FormBorderStyle.None`
+ `WM_NCHITTEST`) is **optional** — take it only if you want the tab strip inline with the
caption, and take it last. Skipping it costs the 10px window radius and nothing else.
*Done when:* no system-coloured surface remains visible against the dark body, including
scrollbars inside custom-drawn controls.

---

## Do not

- Change the parser, the indexer, the search engine, or any filtering logic. If a visual
  change appears to need a data change, stop and say so rather than editing the engine.
- Change or remove any existing keyboard shortcut. Add the `Ctrl K` command palette entry
  point only if one already exists behind the scenes; otherwise leave the key cap out.
- Add animations, transitions, gradients or shadows beyond the single tooltip shadow.
- Introduce a UI framework or component library that isn't already in the project.
- Use emoji as icons. Icons are inline stroke SVG at 1.1–1.3px stroke width.
- Reformat, reorder or reflow files you aren't otherwise changing.
- Use `#646C79` or anything dimmer for text a user needs to read.
- Add a NuGet package, a JS toolchain, or any dependency outside `System.*`. If something
  looks impossible without one, stop and say so rather than adding it.
- Use `Graphics.DrawString` for log body text, or `FlatAppearance` on a `Button`.
- Bake DPI-scaled values into constants.

## Verification checklist

Before calling it done, open a real log of at least 5,000 lines and check:

- [ ] No label, tab, chip or panel value is visually truncated at any supported width
- [ ] A lone ERROR in a wall of INFO is findable at a glance
- [ ] The date appears once per day, not once per row
- [ ] Which severities are filtered out is readable without opening anything
- [ ] Chip counts, status bar, and inspector totals all agree
- [ ] Error clusters are visible in the timeline, not only in the text list
- [ ] Every truncated line is fully readable in one click
- [ ] Every numeric column stays aligned as values change (tabular numerals)
- [ ] Every icon-only button has an accessible name
- [ ] Every control is reachable by Tab and shows a visible focus ring
