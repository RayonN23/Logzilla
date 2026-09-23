# Contributing

## Getting set up

```powershell
git clone https://github.com/rayonnarain/logzilla
cd logzilla
dotnet build Logzilla.sln
dotnet test  Logzilla.sln
```

Open `Logzilla.sln` in Visual Studio 2022 or Rider and it will just work.

## Where to put things

| Change | Goes in |
|---|---|
| A new interface, model or enum | `src/Logzilla.Core/` |
| A new timestamp or log format | `src/Logzilla.Core.Implementation/Formats/` |
| Indexing, merging, tailing | `src/Logzilla.Core.Implementation/Logs/` |
| Query syntax or filtering | `src/Logzilla.Core.Implementation/Query/` |
| A new control or painting change | `src/Logzilla.App/Controls/` |
| A new side panel or menu action | `src/Logzilla.App/Views/MainForm.*.cs` |
| Installation, shortcuts, file types | `src/Logzilla.Setup/` |

Two rules hold the layering together:

- `Core` holds interfaces, models and enums only. It has no reference to
  `System.Windows.Forms`, `System.Drawing` or the implementation.
- Every type in `Core.Implementation` is `internal`. Hosts reach it only through
  `CoreServices`. If the UI needs something new from the engine, add it to an
  interface in `Core` first.

Validate the inputs of every public method at the very top, before any other
work: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`,
and the `ArgumentOutOfRangeException.ThrowIf…` family.

## House rules

- **Keep the byte-level path intact.** Indexing scans bytes; only visible rows
  become strings. A change that decodes whole files will look fine on a 10 MB
  sample and fall over on a 4 GB one.
- **Long work goes on a background thread** with cancellation, so the UI keeps
  responding and stale scans are abandoned.
- **Size UI from measured text**, never fixed pixels — use `Dpi.S()`. Fixed
  widths break at 150% and 200% display scaling.
- **Comment the why, not the what.** Explain a non-obvious constraint; skip
  narrating the code.
- Match the surrounding style. `.editorconfig` covers the mechanical parts.

## Adding a log format

1. Add the enum value to `Core/Formats/TsKind.cs`.
2. Add a branch to `TsParser.Parse` and teach `TsParser.Detect` to recognise it.
3. Add tests to `tests/Logzilla.Core.Implementation.Tests/TsParserTests.cs` — one for
   detection, one for a parsed value, plus any awkward edge case.

Every format in the tool already has that trio of tests, and writing them is how
the single-digit-hour bug in the US format was found.

## Tests

Tests use **NUnit 4** with the constraint model:

```csharp
[Test]
public void SyslogTakesTheYearFromTheFile()
{
    var ms = Parse(TsKind.Syslog, "Mar  1 08:14:02 web01 sshd[1]: x", year: 2026);

    Assert.That(Fmt.Time(ms), Is.EqualTo("2026-03-01 08:14:02.000"));
}
```

The engine and the installer are covered; the UI is not. If you change parsing,
indexing, querying, merging or installation, add a test. Run `dotnet test`
before opening a pull request.

## Code style

`.editorconfig` carries the standard and the build enforces it —
`TreatWarningsAsErrors` is on, so a style violation fails the build rather than
lingering as a warning. In particular:

| | |
|---|---|
| Namespaces | file-scoped |
| Braces | always, even for a single statement |
| `var` | wherever the type is apparent from the right-hand side |
| `new` | target-typed (`private readonly List<int> _x = new();`) |
| Accessibility | explicit on every type and member |
| Usings | outside the namespace, System first, none unused |
| Private fields | `_camelCase` |
| Namespaces | match the folder: `Core/Logs/` is `Logzilla.Core.Logs` |

`dotnet format` applies the whole lot; run it before committing if your editor
does not do so on save.

## Reading the clock

Never call `DateTime.Now` or `DateTime.UtcNow` directly — take an
`ICurrentTimeSupplier` instead. Anything that reads the clock directly cannot be
tested without waiting for real time to pass.

## Releasing

1. Bump `<Version>` in `Directory.Build.props`.
2. Note the change in `CHANGELOG.md`.
3. Tag and push:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

CI builds the installer and attaches it to a GitHub Release.

Keep `AppMetadata.Version` in the Setup project in step with
`Directory.Build.props`; it is what Add/Remove Programs displays.
