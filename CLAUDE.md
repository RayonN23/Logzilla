# Logzilla — Codebase Reference

**What it is:** A Windows Forms log viewer. Opens plain-text and JSON (CLEF/Bunyan/pino/ECS) log files, indexes them in memory, and lets you filter, search, merge, and tail them. Target: .NET 10, Windows only.

---

## Solution layout

```
Logzilla.sln
src/
  Logzilla.Core                   # interfaces + data model (no implementation)
  Logzilla.Core.Implementation    # concrete engine (all internal types)
  Logzilla.AI                     # AI assistant: log tools, redaction, providers
  Logzilla.App                    # WinForms UI
  Logzilla.Setup                  # installer / uninstaller
tests/
  Logzilla.Core.Implementation.Tests   # NUnit unit tests
  Logzilla.AI.Tests                    # NUnit tests for the AI layer (no network)
  Logzilla.App.Tests                   # NUnit tests for UI logic
  Logzilla.App.E2E                     # FlaUI end-to-end tests (real exe; LOGZILLA_E2E=1 to run, see CONTRIBUTING)
```

`Logzilla.sln.DotSettings` is the team-shared Rider/ReSharper file. Naming rules live in `.editorconfig`; the DotSettings file only adds abbreviations that stay upper case in PascalCase names (currently `AI`, as in `OpenAIProvider` and `AIPolicy`).

The App and Setup projects depend only on `Core` interfaces. All concrete types in `Core.Implementation` are `internal`; `CoreServices` (the composition root) is the only public surface. `Logzilla.AI` depends only on `Core` and is the only project that references AI vendor packages.

---

## Logzilla.Core — interfaces & data model

### Data model

**`Logs/LogDocument`** — the in-memory index of one open file (or merged set).  
Stores every line as entries in seven parallel arrays:

| Array | Type | Meaning |
|-------|------|---------|
| `Off` | `long[]` | byte offset of the line in the file |
| `Len` | `int[]` | byte length of the line |
| `Sev` | `byte[]` | severity 0–6 (0=none, 1=TRACE … 6=FATAL) |
| `Ts` | `double[]` | ms since Unix epoch; `NaN` = unknown |
| `Flag` | `byte[]` | 0 = new entry, 1 = continuation (stack trace / wrapped line) |
| `Skip` | `byte[]` | chars of timestamp+level prefix to hide in the grid |
| `Src` | `byte[]` | index into `Srcs` list (which physical file) |

`N` = number of lines indexed. `Ensure(need)` grows arrays with 1.5× strategy.  
`Marks` = `HashSet<int>` of bookmarked line indices.

**`Logs/LogSource`** — one physical file inside a document.  
Key fields: `Path`, `Encoding`, `Stride` (1 for UTF-8/ANSI, 2 for UTF-16), `DataStart` (skips BOM), `Kind` (timestamp format detected), `Parser` (assigned by indexer), `IsJson`, `TintIndex` (color in merged view).  
Zone fields: `Zoned` (stamps carry `Z`/`+hh:mm`/CLF `+0000`), `Offsets` (`ZoneOffset(FromMs, Minutes)` list, one per offset change, keyed by instant), `Declared` (`TimeZoneInfo` the user set for stamps with no zone; null = unknown).

**`Query/FilterResult`** — outcome of one filter pass.  
`View` = `List<int>` of line indices that passed; `Matches` = lines that hit the search query.  
`Bucket[480]` / `BucketWarn` / `BucketErr` = timeline histogram data.  
`Density[2048]` = minimap severity density (one byte per slot, 0–3).  
`Lo` / `Hi` = ms timestamp range of visible lines.

**`Logs/TimeZones`** — converts stored times for display (`Display`) and back (`Stored`) in a `TimeMode` (`AsWritten` / `Local` / `Utc`); `Shift` re-reads one source after its declared zone changes; `Suggest` guesses a zone from the file's last-write time; `ZoneClock` caches offsets per quarter hour for bulk conversion.

**`Query/QuerySpec`** — all filter parameters in one object.  
Built by `QuerySpec.Build(...)`. Contains:
- `Inc` / `Exc` — include/exclude byte arrays (for fast `MatchBytes`)
- `Rx` — compiled `Regex` (when regex mode is on)
- `LevelMask` — 7-bit mask, bit N = show severity N
- `SrcMask` — 64-bit mask, bit N = show source N
- `T0` / `T1` — time range (ms, `NaN` = no filter)
- `Ctx` — context lines around each match
- `FilterMode` — true = hide non-matching lines; false = highlight only

`ScanBytes` / `ScanText` fold one physical line into an `EntryMatch`; `MatchBytes` is
the single-line form, kept as the reference the fast path is tested against.

**`Query/FieldFilter`** — one field condition: `Key`, `Op` (`Is` / `Contains` / `IsNot`),
`Values`. Values of one filter are alternatives; separate filters (`QuerySpec.Fields`) must
all hold. Keys and values compare case-insensitively. Field filters narrow the view in
either search mode; only the text query decides `Matches`.

**`Query/FieldMatch`** / **`FieldScan`** — answers the field filters for one entry, line by
line: `"key": value` anywhere in a line (nested keys included), plus `key=value` in plain
text. `FieldMatch.Needles` gives byte needles so `FilterEngine` can rule most lines out
before decoding them.

**`Query/EntryMatch`** — which include terms have turned up so far in one entry, and
whether an exclude term has. An entry is a header line plus its continuations, so a
term on line 1 and a term on line 4 satisfy the same query.

### Interfaces

| Interface | Where used |
|-----------|-----------|
| `ILogIndexer` | Prepare file metadata; Run indexing; Merge docs |
| `IFilterEngine` | Run a QuerySpec against a LogDocument |
| `IJsonLogReader` | Parse, render, describe a JSON log line |
| `ITimestampParser` | Parse a timestamp from a byte span |
| `ICurrentTimeSupplier` | Inject wall clock (testable) |
| `ILineReader` | Random-access raw bytes for a line index |

### Helpers

- **`Common/Levels`** — severity names: `""`, TRACE, DEBUG, INFO, WARN, ERROR, FATAL (indices 0–6)
- **`Common/DisplayFormatter`** — `Time(ms)`, `Bytes(n)`, `Duration(ms)`, `Number(n)` for status bar
- **`Common/CivilDate`** — pure arithmetic `DaysSinceEpoch(y,m,d)` used by TsParser
- **`Common/AsciiTable`** — `ToLowerAscii(byte)`, `IsWord(byte)` for hot-path byte matching
- **`Common/XmlText`** — `LooksLikeXml`, `Pretty`, `PrettyWithin` indent an XML payload
  embedded in a log message. A tolerant tokenizer, not a parser: fragments, quoted
  payloads and truncated tags all lay out, and anything unrecognised is returned as-is
- **`Formats/TsKind`** — enum: `None`, `Iso`, `Slash`, `Clf`, `Us`, `Syslog`, `TimeOnly`

---

## Logzilla.Core.Implementation — engine internals

### `CoreServices` (public composition root)

```csharp
CoreServices.CreateClock()         // → SystemTimeSupplier
CoreServices.CreateIndexer(clock)  // → LogIndexer
CoreServices.CreateFilterEngine()  // → FilterEngine
CoreServices.CreateJsonLogReader() // → JsonLogReader
```

### `Formats/TsParser`

Parses six timestamp formats from raw bytes without allocating strings.  
`Detect(sample)` votes across up to 300 sample lines and returns the winning `TsKind`.  
`Parse(byte[], off, len, out ms)` returns `true` + sets `ms` (ms since epoch); also sets `LastEnd` (byte index after the stamp, so `Skip` can be computed).  
`TimeOnly` logs track midnight rollovers via `_dayShift` + `_lastTod`.

### `Formats/JsonLog`

**Indexing path (byte-level, no allocations):**  
`Find(byte[], off, len, key)` — scans for `"key":` and returns the index just past `:`.  
`Scalar(byte[], p, end, max)` — reads the raw scalar value at position p (handles quoted strings without full unescaping).  
`Level(string)` — maps a string or numeric level to 0–6.

**Display path (full parse, only for visible rows):**  
`Parse(string)` — flat `List<KVP<string,string>>` from one JSON line.  
`Render(kv, template)` — fills `{Placeholder}` tokens from properties (Serilog CLEF style).  
`Line(raw)` — one-line summary for the grid (resolves message / template, appends first line of exception).  
`Pretty(raw)` — multi-line detail for the Entry panel (message, properties, exception with restored newlines).

**Key arrays** (searched in order):  
`TimeKeys`, `LevelKeys`, `MsgKeys`, `ExKeys` — recognized field names across CLEF, Bunyan, pino, ECS, etc.

### `Formats/LevelScan`

Scans a plain-text line for level tokens (DEBUG, INFO, WARN, ERROR, FATAL, TRACE).  
Returns severity byte + positions (`lvA`, `lvB`) so `Skip` can absorb the level token too.

### `Logs/LogIndexer`

**`Prepare(path)`** — creates a `LogSource`: detects BOM/encoding (UTF-8, UTF-16 LE/BE, system fallback), reads 300 sample lines, runs `TsParser.Detect` and `DetectJson`.

**`Run(doc, si, progress, cancelled)`** — hot path. Reads the file in 8 MB chunks, scans for `\n` (stride-aware for UTF-16), calls `Emit()` for each line. Cancellable every 64 K lines.

**`Emit()`** — decides per line:
- For JSON: byte-scan for time key → parse ts; byte-scan for level key → `JsonLog.Level`.
- For plain text: `TsParser.Parse` → ts + `Skip`; `LevelScan.Scan` → severity.
- `Flag = 1` if no own timestamp (continuation) or leading whitespace (plain text).
- Writes into `doc.Off/Len/Sev/Ts/Flag/Skip/Src[doc.N]` then increments `doc.N`.

**`Sample(source, max)`** — reads first 1 MB, returns up to `max` lines as `byte[]` (first 96 bytes each).

**`Merge(docs)`** — k-way timestamp merge across multiple single-file documents. Continuations (`Flag == 1`) always travel with their parent entry. Calls `SourceLabels.Apply()` to strip shared filename prefixes.

### `Logs/LineReader`

Buffered random-access reader. `Locate(lineIndex, out buf)` seeks to `Off[i]` and returns the buffer slice. Used by `FilterEngine` and `LogView` to get raw bytes for a line.

### `Query/FilterEngine`

`Run(doc, spec, cancelled, progress)`:
1. Walks `doc.N` lines one **entry** at a time. With `spec.Group`, an entry is a
   header line plus every `Flag == 1` line under it; without it, one line is one entry.
2. Per entry: level mask → source mask → time range, all taken from the header line;
   then the field filters (`FieldScanState`) over the whole entry.
3. Query match over the *whole* entry (`Hits`): literal terms accumulate across its
   lines via `EntryMatch`, an exclude term anywhere drops it, a regex sees the lines
   joined. `r.Matches` records the header line, so it stays sorted for jump-to-hit.
4. A passing entry keeps all of its lines; a failing one drops all of them.
5. After main pass: if `spec.Ctx > 0`, expands keep-set by ±Ctx lines around each match.
6. Builds `FilterResult.View`, per-level `Counts`, per-source `SrcCounts`.
7. Calls `BuildViz()` to fill `Bucket` histogram and `Density` minimap arrays.

Cancellation and progress poll on a line-count threshold (`PollStride`), not a mask on
the loop index — entries are variable length and would step over a fixed multiple.

---

## Logzilla.AI — the AI assistant (3.0.0)

A model reaches the log only through this project. Nothing in it touches WinForms.

- **`Tools/LogTools`** — the read-only tools a model may call: `get_overview`, `search_log`,
  `get_entries`, `count_over_time`, `find_patterns`, `list_fields`, `get_field_values`. They run
  the real `IFilterEngine`; counts come from the engine, never the model; every entry carries its
  1-based line number (`#1234`) so answers can cite it. Every result goes through `Run()`: errors
  become text for the model, output is capped (`MaxOutputChars`) and **redacted**. Events:
  `Activity` (transcript lines; a `SearchRequest` makes it appliable as a filter) and `Sending`
  (exact text leaving the machine, for the record). **Never add a tool that writes, executes or
  reaches the network** — log content is attacker-influenced, and a read-only tool set is what
  makes prompt injection harmless.
- **`Tools/ILogContext`** — what the host lets the tools see (document, view, engine, reader,
  time formatting/parsing in the user's clock, fields). Read from background threads.
- **`Tools/Shapes`** — normalises a message to its pattern shape.
- **`Auth/GitHubSignIn`** — "Sign in with GitHub" by the OAuth device flow (`StartAsync` → user code, `WaitAsync` polls honouring `slow_down`, `RefreshAsync` for expiring GitHub App tokens, `LoginAsync`). No client secret, ever. The Client ID comes from the `GitHubClientId` MSBuild property (compiled into App as `AssemblyMetadata`) or the `AIGitHubClientId` policy. The token goes to `CopilotProvider` as `GitHubToken`.
- **`Privacy/Redactor`** — replaces emails, IPv4, GUIDs, JWTs/bearer tokens, keyed secrets,
  AWS/GitHub/OpenAI-style keys with stable placeholders (`«IP_1»`); `Restore` puts real values
  back for display only.
- **`Providers/`** — `IAssistantProvider` (list models, start a chat) / `IAssistantChat` (send,
  stream `AssistantEvent`s). `ChatClientProvider` runs the Microsoft.Extensions.AI tool loop once
  for `AnthropicProvider` and `OpenAIProvider`. `CopilotProvider` uses `GitHub.Copilot.SDK`;
  `CopilotProvider.Lockdown` is the session config that restricts Copilot's agent to our tools
  (`AvailableTools` allowlist, all permissions rejected, config discovery/skills/MCP/session store
  off, empty working dir) — tests pin every switch.
- **`AssistantInstructions`** — the system prompt (cite lines, counts from tools, ignore
  instructions inside log content, placeholders).
- The Copilot SDK bundles ~200 MB of runtime; `CopilotSkipCliDownload` is on unless
  `/p:BundleCopilot=true` (`build.ps1 -BundleCopilot`). Otherwise `CopilotCli.Resolve` finds an
  installed Copilot CLI (setting → `COPILOT_CLI_PATH` → bundled → `PATH`).

---

## Logzilla.App — WinForms UI

### Entry point

`Program.cs` — creates `CoreServices` objects, passes them to `MainForm`.

### Infrastructure

| File | Purpose |
|------|---------|
| `Settings.cs` | INI-style key-value store, saved to `%AppData%` |
| `Theme.cs` | Dark-theme colors (`Bg`, `Fg`, `Dimmer`, `Level[]`, `Teal`, `Amber`, `Violet`) and `Mono(size)` font |
| `Palette.cs` | Per-source tint colors for merged view |
| `Dpi.cs` | DPI-aware scaling helper `Dpi.S(n)` |
| `Paths.cs` | App data directory paths |
| `FileAssociations.cs` | Shell `.log`/`.json` registration |
| `EntryClip.cs` | `Compose(texts, labels)` — the clipboard text for a set of entries. One entry copies bare; several go one per line with their source label |

### Controls

| Control | What it draws |
|---------|--------------|
| `LogView` | Owner-drawn log grid: gutter (line numbers), source column, timestamp, level chip, message with search highlights. Reads raw bytes via `LineReader` for display and JSON rendering. |

**`LogView` selection** — `Selected` (a doc line index) plus a private `_anchor` hold a
*contiguous run*, resolved through `Res.View`: `Select` collapses to one row, `SelectTo`
extends (Shift+click, Shift+arrows), `SelectAll` takes the whole view, `ClearSelection`
drops both. `SelectedLines()` / `SelectionCount` / `IsSelected` are what the form copies
and marks with. A run is not a set — marks (`M`) remain the way to collect rows that are
not next to each other. Press-and-drag sweeps a run too (`_dragging`, with `_autoScroll`
feeding rows in while the pointer is held past an edge). `CanExpand` — no range, and no
drag in progress — gates the inline row expansion, so the layout cannot shift under a
drag that is still going.
| `MiniMap` | Thin vertical strip showing severity density across the whole document (from `FilterResult.Density`). |
| `Timeline` | Horizontal time axis + stacked bucket histogram (`Bucket` / `BucketWarn` / `BucketErr`). Supports click-drag time range selection. |
| `TabStrip` | Custom tab bar for multiple open documents. |
| `ThinScrollBar` | Slim custom scrollbar (replaces WinForms default). |
| `PanelList` / `PanelRow` | Side panel list used by summary/sources/marks/colors panels. |
| `FieldChipBar` | Strip of field-filter chips under the filter bar; visible only while a filter is set. Click edits, × removes, right-click menu. |
| `FieldFilterEditor` / `SuggestionList` | The field filter editor, in `Dialogs/FieldFilterDialog` (a borderless modal dialog centred on the main window's monitor, dragged by its header, reopening where it was left): field box, is/contains/is not, value box, and a pick list of the log's fields or a field's values with counts. |

Infrastructure for field filters: `FieldFilterList` (how filters merge — same key and op
join, is/is not cancel; filters are replaced, never mutated, because the scan thread may
hold them) and `FieldCatalog` (fields and values sampled from up to 20,000 entries,
cached per document and line count).

`HighlightRule` — a regex or literal pattern + color, used by `LogView` for custom coloring.

### MainForm (split across partial files)

| File | Responsibility |
|------|---------------|
| `MainForm.cs` | Fields, constructor, icon loading, `HighlightRule` persistence |
| `MainForm.Layout.cs` | `BuildUi()` — creates all controls; `ApplySettings()` |
| `MainForm.Commands.cs` | Keyboard shortcuts, menu actions (open, merge, copy, jump-to-next-hit …) |
| `MainForm.Filtering.cs` | `StartFilter()` — builds `QuerySpec`, runs `FilterEngine` on a background thread with generation-counter cancellation; `UpdateStatus()` |
| `MainForm.Files.cs` | File open/reload, tail-poll timer (`TailPollMs = 1000 ms`) |
| `MainForm.Input.cs` | Keyboard + drag-and-drop input handling |
| `MainForm.Panels.cs` | `FillSide()` — populates the side panels (summary, sources, fields, patterns, entry, marks, colors; Ask refreshes itself) |
| `MainForm.FieldFilters.cs` | `_fieldFilters`, the chip strip, opening the editor (`OpenFieldEditor`, Ctrl+Shift+F), the chip menu, and the grid's "Show only where… / Hide where…" menus |
| `MainForm.Highlighting.cs` | The Highlighting tab (key `colors`): the add bar (`HighlightInput`, `.*`, `HighlightAdd`), a right-click menu per rule, `RemoveOldDefaultRules` (one-off removal of the three rules versions up to 3.0 added). Tab labels come from `SideLabel` — keys `marks`/`colors`/`ask` read Bookmarks/Highlighting/AI Chat |
| `MainForm.Tabs.cs` | Per-tab search: `SwitchTab(index)` saves the current tab's query, severity chips, sources, time range and field filters and restores the new tab's (`TabSearch`). Every change of active tab goes through it. Search options (regex, case, highlight/filter, context) stay window-wide |
| `MainForm.TimeZones.cs` | The Log time / Local time / UTC chip and menu, asking a file's zone (`AskZone`, a `TaskDialog`), `DeclareZone`, per-path zone memory in settings (`zone.<PATH>`) |
| `MainForm.Assistant.cs` | The Ask tab (8th side tab, Ctrl+8): provider/model picker, `Ask()` (redact → record → `IAssistantChat.SendAsync`, events marshalled with `BeginInvokeSafe`), citations → `_view.Select`, Apply → user filters, AI settings dialog, `AIPolicy`. Each question swaps an immutable `AskSnapshot` into `SwitchableContext`, so tools on background threads never read UI state |

AI infrastructure in `Infrastructure/`: `AIPolicy` (registry policy under `SOFTWARE\Policies\Logzilla`; HKLM wins), `SecretStore` (DPAPI, `dpapi:` prefix, settings keys `ai.key.<provider>`), `AIRecord` (in-memory + optional `%APPDATA%\Logzilla\ai-record\*.jsonl`), `CopilotCli`, `AskMarkup` (answer Markdown + `[#n]` citations). Controls: `AskPanel`, `AskTranscript` (owner-drawn). Settings keys: `ai.copilot.login`, `ai.copilot.expires`, `ai.key.copilot-refresh` (sign-in), `ai.enabled`, `ai.redact`, `ai.record`, `ai.scope`, `ai.provider`, `ai.model.<provider>`, `ai.endpoint.<provider>`, `ai.copilot.cli`.

**Generation counter pattern:** `_liveGen` (volatile int) is incremented each time a new filter starts. The background thread checks `_liveGen != gen` to self-cancel without locks.

**Tail mode:** `_tailTimer` fires every second; when the file has changed, `TailPoll` runs the indexer on a background thread (`_tailing` holds the document meanwhile, and merge / zone changes wait for it), then re-runs the filter. A truncated file is re-read from the top and `LogView.ForgetLines()` drops per-line caches.

---

## Logzilla.Setup

Standalone installer / uninstaller console app. Key types:
- `ApplicationInstaller` — orchestrates install/uninstall steps
- `RegistryRegistrar` — writes `HKCU` App Paths and uninstall entries
- `ShortcutWriter` — creates `.lnk` files via COM `IShellLink`
- `DirectorySize` — computes installed size for Add/Remove Programs
- `PendingDelete` — schedules file deletion on next reboot via `MoveFileEx`
- `ShellNotifier` — `SHChangeNotify` to refresh Explorer after association changes

---

## Design decisions worth knowing before touching the code

1. **Parallel arrays, not objects** — `LogDocument` uses seven parallel arrays for all line metadata. This is intentional for cache efficiency in the hot scan loop. Do not refactor into a `LogLine` struct without profiling.

2. **No JSON parsing at index time** — `JsonLog.Find()` is a byte-level key scanner. Full `Parse()` runs only for rows visible on screen. This keeps a million-line CLEF file as cheap to open as a plain-text file.

3. **`double` timestamps (ms since epoch)** — `NaN` means "unknown, inherit from previous line of same source." The timeline and filter treat `NaN` as outside any explicit time range. **What `Ts` holds depends on the source:** a real UTC instant when the zone is known (`Zoned`, or `Declared` set), the wall clock as written otherwise. Everything that shows a time goes through `TimeZones.Display`; everything typed goes through `TimeZones.Stored`. The parser (`TsParser`) always returns the wall clock and reports the offset in `LastOffset`; `LogIndexer.Emit` applies it. Changing a declared zone calls `TimeZones.Shift` on every document holding the source, then `ILogIndexer.Reorder` on merged ones.

4. **`Flag == 1` = continuation** — stack traces and wrapped lines are attached to the entry above. `FilterEngine` and `LogView` both respect this grouping.

5. **`Skip` byte** — chars of the timestamp+level prefix suppressed in the message column. Lets the grid show clean message text without re-parsing every rendered row.

6. **`_liveGen` cancellation** — the filter runs on a `Thread` (not `Task`), checked every 65536 lines (every 4096 when a regex or field filter decodes each line). A new query increments `_liveGen`, causing the old thread to return early.

7. **`LogDocument.Ensure(n)`** — grows with `cap = cap + cap/2 + 1024` (1.5× + slack). Never shrinks. Keep in mind when handling large files that arrays are pre-allocated at 64 K entries and may be several MB.
