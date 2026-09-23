<div align="center">

<img src="assets/Logzilla.png" width="140" alt="Logzilla">

# Logzilla

**A log analyzer for very large files.**

[![CI](https://github.com/rayonnarain/logzilla/actions/workflows/ci.yml/badge.svg)](https://github.com/rayonnarain/logzilla/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

</div>

Opens multi-gigabyte logs instantly, merges several of them onto one timeline,
and reads structured JSON logs as readable sentences instead of raw objects.

---

## Install

Download `Logzilla-Setup.exe` from
[Releases](https://github.com/rayonnarain/logzilla/releases) and run it.

Nothing else is needed. The installer carries the application inside it, the
application bundles its own .NET runtime, and everything is written under your
own profile — so there is no prerequisite to install and no administrator prompt.

| | |
|---|---|
| `Logzilla-Setup.exe` | Install |
| `Logzilla-Setup.exe /silent` | Install with no output |
| `Logzilla-Setup.exe /uninstall` | Remove it |
| `Logzilla-Setup.exe /dir=D:\Tools\CN` | Install somewhere else |

It also appears in **Settings › Apps** like any other program.

> **Making it the default for `.log` files**
> Windows deliberately stops an installer from seizing a file association. The
> installer registers the type and adds the app to *Open with*; to finish, right-click
> a `.log` file → **Open with** → **Choose another app** → **Logzilla** →
> tick *Always use this app*. Notepad++ needs the same step.

## What it does

**Reads the whole file, not a window into it.** The file is never fully decoded.
Indexing and search run over raw bytes and only the rows on screen become text,
so file size is bounded by disk rather than memory — about 24 bytes of index per
line. A 1 GB log indexes in roughly nine seconds.

**Understands the timestamp.** ISO, `yyyy/MM/dd`, Apache CLF, US `M/d/yyyy`,
syslog, and time-only logs including midnight rollover. Severity is read from the
line and shown in its own column.

**Merges by time.** Open several logs and press <kbd>Ctrl</kbd>+<kbd>M</kbd>.
Entries interleave onto one timeline with a colour-coded source column, even when
the files use different formats. Stack traces stay attached to the entry above
them. This is what turns "the gateway logged a timeout" and "the service logged a
connection reset" into one story.

**Reads JSON logs.** Serilog CLEF, pino, bunyan, Elastic ECS and plain NDJSON are
detected from the file itself. Message templates are filled in, so
`{Name}.ProcessMaintenance: {CommandType}` displays as
`MaintenanceCommandManager.ProcessMaintenance: DeviceInformation`. Properties
become clickable fields, and because CLEF carries the template, pattern
clustering is exact rather than heuristic.

**Finds the shape of the noise.** The Patterns panel collapses variable parts —
`latency=812ms` and `latency=41ms` become one shape — so a million lines reduce to
a few dozen distinct things ranked by frequency.

**Follows a live file.** Like `tail -f`, with the index extended incrementally.

## Building from source

Requires only the [.NET SDK 10](https://dotnet.microsoft.com/download) (or
Visual Studio 2022 / Rider). **There is no other tooling to install** — the
installer is a project in this solution rather than a separate product.

```powershell
git clone https://github.com/rayonnarain/logzilla
cd logzilla

dotnet build Logzilla.sln -c Release   # build
dotnet test  Logzilla.sln              # run the unit tests

.\build\build.ps1                            # build, test, publish and package
```

`build.ps1` writes `dist\Logzilla-Setup.exe`. It publishes self-contained
by default so the target machine needs no .NET runtime; add
`-FrameworkDependent` for a far smaller build when the runtime is already there,
or `-Runtime win-arm64` to target Arm.

Pushing a tag such as `v1.0.0` makes CI build the installer and attach it to a
GitHub Release.

### How the installer works

`src/Logzilla.Setup` is an ordinary console project. At build time an
MSBuild target zips whatever is in `artifacts\publish` and embeds it as a
resource, so the output is a single self-contained executable. At run time it
extracts to `%LOCALAPPDATA%\Programs\Logzilla`, writes Start Menu and
Desktop shortcuts through the shell's own `IShellLink`, registers the log file
types under `HKEY_CURRENT_USER`, and records an entry for Add/Remove Programs.

It targets plain `net10.0` rather than `net10.0-windows` because it needs no UI
framework; the Windows-only parts are marked `[SupportedOSPlatform("windows")]`.


## How the code is laid out

Three engine assemblies plus the front end. The split follows the layering
rule: **models and interfaces live in `Core`; concrete classes live in
`Core.Implementation` and are all `internal`**. The only public type in the
implementation is `CoreServices`, the composition root that hosts call.

```
src/
  Logzilla.Core/                  interfaces, models, enums — no logic, no UI
    Common/     DisplayFormatter, CivilDate, AsciiTable, Levels, ICurrentTimeSupplier
    Formats/    TsKind, ITimestampParser, IJsonLogReader
    Logs/       LogSource, LogDocument, ILogIndexer, ILineReader
    Query/      QuerySpec, FilterResult, IFilterEngine
  Logzilla.Core.Implementation/   the engine; every type internal
    CoreServices.cs                     composition root
    Formats/    TsParser, LevelScan, JsonLog, JsonLogReader
    Logs/       LogIndexer, LineReader, SourceLabels
    Query/      FilterEngine
  Logzilla.App/                   Windows Forms front end
    Controls/   log grid, timeline, minimap, tab strip, scrollbar
    Infrastructure/ theme, DPI scaling, settings, file associations
    Views/      MainForm, split into partial classes
  Logzilla.Setup/                 the installer, built by the .NET SDK
tests/
  Logzilla.Core.Implementation.Tests/   NUnit tests
```

`MainForm` takes `ILogIndexer`, `IFilterEngine` and `IJsonLogReader` through
its constructor; `Program` is where they are created. Nothing in the UI refers
to a concrete engine class.


### Design notes worth knowing before you change things

**Warnings are errors.** `TreatWarningsAsErrors` and the .NET analyzers are on
for every project, and `.editorconfig` raises the style rules to error too. A
build that succeeds is therefore already free of the recommended analyzer set,
unused usings, missing braces and implicit accessibility. Suppressing a rule
needs a justification on the attribute.

**Indexing never decodes the file.** `Indexer` scans bytes for line boundaries,
the timestamp and the severity word, storing offset, length, level, time and a
flag per line. `LineReader` turns bytes into a string only for the rows being
drawn. If you add a feature, keep it on that side of the line — decoding
everything is what makes other log viewers fall over.

**Scans run off the UI thread** with cancellation, so typing in the Find box
never blocks the window and a scan is abandoned the moment the query changes.

**Everything on screen is sized from measured text**, not fixed pixels. That is
what keeps the toolbar readable at 200% display scaling. If you add a control,
size it with `Dpi.S()` and let the layout panels wrap.

**Merging is a k-way merge, not a sort.** Each file is already in time order, so
`Indexer.Merge` walks cursors and copies whole entry groups, which keeps stack
traces bonded to their parent line.

## Configuration

| | |
|---|---|
| Installed to | `%LOCALAPPDATA%\Programs\Logzilla` |
| Settings and recent files | `%APPDATA%\Logzilla` |
| Force a UI scale | `set LOGZILLA_SCALE=1.5` before launching |
| Colour rules and filters | **Tools → Save setup** writes a `.cnx` file to share |

## Shortcuts

| | |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>O</kbd> | Open files |
| <kbd>Ctrl</kbd>+<kbd>M</kbd> | Merge open files onto one timeline |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | Focus the Find box |
| <kbd>Esc</kbd> | Clear the Find box |
| <kbd>Enter</kbd> / <kbd>Shift</kbd>+<kbd>Enter</kbd> | Next / previous match |
| <kbd>E</kbd> / <kbd>Shift</kbd>+<kbd>E</kbd> | Next / previous error |
| <kbd>M</kbd> | Mark an entry |
| <kbd>F5</kbd> / <kbd>F6</kbd> | Re-read from disk / follow the file |
| <kbd>F1</kbd> | Full shortcut list |

**Query syntax:** `timeout retry` (both words) · `"connection reset"` (phrase) ·
`-heartbeat` (exclude) · `.*` button (regular expression)

## Accessibility

- Every control exposes a name, role and description to assistive technology,
  and the log grid announces the selected entry as you move through it.
- When Windows is in a high-contrast scheme the dark palette is replaced by the
  system colours, so the user's chosen scheme wins.
- Everything reachable with the mouse is reachable from the keyboard — see the
  shortcut table.
- The whole interface is sized from measured text, so display scaling and
  larger system fonts are respected.

## Limits

- JSON support assumes one object per line; pretty-printed JSON is read as text.
- Merging is capped at 12 files.
- Follow works on a single file, not on a merged view.
- Timestamps display exactly as written, with no timezone conversion. Syslog and
  time-only formats carry no year, so the file's last-modified date is used.

## License

MIT — see [LICENSE](LICENSE).
