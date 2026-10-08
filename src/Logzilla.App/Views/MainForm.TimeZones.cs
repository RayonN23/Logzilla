using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Dialogs;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Logs;

namespace Logzilla.App.Views;

// Which clock the times on screen read in, and what zone a file's stamps are in
// when the stamps themselves do not say.
//
// Times are stored as real instants wherever the zone is known, so switching the
// clock only changes how they are written out; nothing is re-indexed. A file whose
// stamps carry no zone is asked about, never guessed: its last-modified time can
// suggest an answer, but only the user can give one.

internal sealed partial class MainForm
{
    private static readonly string[] TimeModeLabels = { "Log time", "Local time", "UTC" };

    private TimeMode _timeMode = TimeMode.AsWritten;
    private readonly TimeZoneInfo _localZone = TimeZoneInfo.Local;
    private ToggleChip _bTime;
    private ToolStripMenuItem[] _miTimeModes;

    /// <summary>Files already asked about this session, so "keep log time" sticks.</summary>
    private readonly HashSet<LogSource> _zoneAsked = new();

    private ToggleChip BuildTimeChip()
    {
        // Sized for the widest label, so the command bar does not shift as it cycles.
        var chip = new ToggleChip { Label = "Local time", BackColor = Theme.BgChrome };
        chip.Height = Dpi.S(Theme.ControlHeight);
        chip.Fit(90);
        chip.Label = TimeModeLabels[0];
        new ToolTip().SetToolTip(chip, "Show times as the log wrote them, in your local time, or in UTC. Click to switch, right-click to choose.");
        chip.AccessibleName = "Time zone the times are shown in";
        chip.Click2 += delegate { SetTimeMode((TimeMode)(((int)_timeMode + 1) % 3)); };
        chip.RightClick += delegate
        {
            var menu = new ContextMenuStrip();
            foreach (var item in BuildTimeModeItems())
            {
                menu.Items.Add(item);
            }
            menu.Show(chip, new Point(0, chip.Height));
        };
        return chip;
    }

    private ToolStripMenuItem BuildTimeMenu()
    {
        var times = new ToolStripMenuItem("Show times");
        _miTimeModes = BuildTimeModeItems();
        foreach (var item in _miTimeModes)
        {
            times.DropDownItems.Add(item);
        }
        return times;
    }

    private ToolStripMenuItem[] BuildTimeModeItems()
    {
        var items = new ToolStripMenuItem[3];
        string[] text = { "Log time (as the file wrote it)", "Local time (" + LocalOffsetText() + ")", "UTC" };
        for (var k = 0; k < 3; k++)
        {
            var mode = (TimeMode)k;
            items[k] = new ToolStripMenuItem(text[k]) { Checked = _timeMode == mode, BackColor = Theme.BgChrome, ForeColor = Theme.Text };
            items[k].Click += delegate { SetTimeMode(mode); };
        }
        return items;
    }

    private string LocalOffsetText()
    {
        return TimeZones.FormatOffset((int)_localZone.GetUtcOffset(DateTime.UtcNow).TotalMinutes);
    }

    private void SetTimeMode(TimeMode mode)
    {
        if (mode != TimeMode.AsWritten && F != null)
        {
            // Converting means nothing for a file whose zone nobody knows; ask now,
            // while the question is obviously about what is on screen.
            AskUndeclared(F.Srcs);
        }
        _timeMode = mode;
        SyncTimeMode();
    }

    /// <summary>Pushes the clock to everything that writes a time out.</summary>
    private void SyncTimeMode()
    {
        _view.TimeMode = _timeMode;
        _tl.TimeMode = _timeMode;
        if (_bTime != null)
        {
            _bTime.Label = TimeModeLabels[(int)_timeMode];
            _bTime.On = _timeMode != TimeMode.AsWritten;
            _bTime.Invalidate();
        }
        if (_miTimeModes != null)
        {
            for (var k = 0; k < _miTimeModes.Length; k++)
            {
                _miTimeModes[k].Checked = (int)_timeMode == k;
            }
        }
        if (!double.IsNaN(_t0))
        {
            _updatingTimeRange = true;
            _tRange.Text = FormatShownRange(_t0, _t1);
            _updatingTimeRange = false;
        }
        _view.Invalidate();
        _tl.Invalidate();
        FillSide();
    }

    /// <summary>A stored time as the document's reference file reads it now.</summary>
    private double ShownTime(double ms)
    {
        return TimeZones.Display(ms, TimeZones.Reference(F), _timeMode, _localZone);
    }

    /// <summary>A time read in the current clock, back as stored.</summary>
    private double StoredTime(double shown)
    {
        return TimeZones.Stored(shown, TimeZones.Reference(F), _timeMode, _localZone);
    }

    private string ShowLineTime(LogDocument f, int i)
    {
        return DisplayFormatter.Time(TimeZones.Display(f.Ts[i], f.SrcOf(i), _timeMode, _localZone));
    }

    private string FormatShownRange(double t0, double t1)
    {
        return TimeRangeParser.FormatTimeRange(ShownTime(t0), ShownTime(t1));
    }

    /// <summary>View > Time zone of this file: asks outright for a single file; a
    /// merged view has several, so it opens the Sources panel that lists them.</summary>
    private void ZoneOfCurrentFile()
    {
        var f = F;
        if (f == null)
        {
            return;
        }
        if (f.Merged)
        {
            _sideTab = "sources";
            SyncSideTabs();
            FillSide();
            return;
        }
        if (f.Srcs[0].Kind == TsKind.None)
        {
            Info(f.Srcs[0].Name + " has no timestamps Logzilla recognises, so there is no zone to set.");
            return;
        }
        AskZone(f.Srcs[0]);
    }

    /// <summary>One file's zone in the Sources panel; clickable when it is the user's to set.</summary>
    private void AddZoneRow(List<PanelRow> rows, LogSource s, int index)
    {
        var settable = !s.Zoned && s.Kind != TsKind.None;
        PanelRow row;
        if (settable && s.Declared == null)
        {
            row = new PanelRow(s.Label + ": set time zone...", "not stated");
            row.Accent = Theme.SevColor[4];
        }
        else
        {
            row = new PanelRow(s.Label, TimeZones.Describe(s, _localZone));
            row.Accent = Theme.Text2;
        }
        if (settable)
        {
            row.Tag = "zone:" + index;
            row.Chevron = true;
        }
        rows.Add(row);
    }

    /// <summary>In the Detail panel: says so when the time above could not be converted.</summary>
    private string ZoneShownNote(LogSource s)
    {
        if (_timeMode == TimeMode.AsWritten || TimeZones.IsKnown(s))
        {
            return "";
        }
        return "  (shown in log time)";
    }

    /// <summary>Asks about each timed file in <paramref name="sources"/> whose zone is unknown.</summary>
    private void AskUndeclared(IEnumerable<LogSource> sources)
    {
        foreach (var s in new List<LogSource>(sources))
        {
            if (TimeZones.NeedsDeclaring(s) && !_zoneAsked.Contains(s))
            {
                AskZone(s);
            }
        }
    }

    /// <summary>Asks what zone one file's stamps are in, and applies the answer.</summary>
    private void AskZone(LogSource s)
    {
        if (s.Zoned)
        {
            Info(s.Name + " writes its zone into every timestamp (" + TimeZones.Describe(s, _localZone) +
                 "), so there is nothing to set.");
            return;
        }
        _zoneAsked.Add(s);

        var hint = TimeZones.Suggest(LastWall(s), LastWriteUtc(s.Path), _localZone);
        var local = new TaskDialogCommandLinkButton("Local time" + (hint == ZoneHint.Local ? "  (suggested)" : ""),
            "Written on this machine's clock, " + LocalOffsetText() + " today.");
        var utc = new TaskDialogCommandLinkButton("UTC" + (hint == ZoneHint.Utc ? "  (suggested)" : ""),
            "Written in Coordinated Universal Time, as servers often are.");
        var other = new TaskDialogCommandLinkButton("Another offset...", "A fixed offset from UTC, such as +05:30.");
        var leave = new TaskDialogCommandLinkButton("Keep log time", "Show the times exactly as the file wrote them.");
        var page = new TaskDialogPage
        {
            Caption = AppInfo.Name,
            Heading = "What time zone is " + s.Name + " in?",
            Text = "Its timestamps don't say, so they can't be converted until you choose. " +
                   "The answer is remembered for this file.",
            Buttons = { local, utc, other, leave },
            AllowCancel = true,
            DefaultButton = hint == ZoneHint.Utc ? utc : hint == ZoneHint.Local ? local : leave,
        };
        if (hint != ZoneHint.None)
        {
            page.Footnote = new TaskDialogFootnote(
                "Suggested because the last entry matches when the file was last saved, read in " +
                (hint == ZoneHint.Utc ? "UTC." : "local time."));
        }

        var picked = TaskDialog.ShowDialog(this, page);
        if (picked == local)
        {
            DeclareZone(s, _localZone);
        }
        else if (picked == utc)
        {
            DeclareZone(s, TimeZoneInfo.Utc);
        }
        else if (picked == other)
        {
            var text = Prompt.Ask(this, "Offset from UTC, such as +05:30");
            if (text == null)
            {
                return;
            }
            if (!TimeZones.TryParseOffset(text, out var minutes))
            {
                Info("\"" + text + "\" is not an offset from UTC. Try one like +02:00 or -05:30.");
                return;
            }
            DeclareZone(s, TimeZones.Fixed(minutes));
        }
        else if (picked == leave)
        {
            DeclareZone(s, null);
        }
    }

    /// <summary>
    /// Says what zone a file's stamps were written in (null: nobody knows) and re-reads
    /// its times in every document that holds it. A merged document is re-sorted, since
    /// the file's entries may now belong somewhere else on the shared timeline.
    /// </summary>
    private void DeclareZone(LogSource s, TimeZoneInfo zone)
    {
        if (s.Zoned)
        {
            return;
        }
        if (_indexing.Count > 0)
        {
            Info("Wait for indexing to finish, then set the time zone.");
            return;
        }
        var old = s.Declared;
        _cfg.Set(ZoneKey(s.Path), zone == null ? "" : TimeZones.Name(zone, _localZone));
        if (ReferenceEquals(old, zone) || (old != null && zone != null && old.Id == zone.Id))
        {
            return;
        }
        foreach (var d in _docs)
        {
            if (!d.Srcs.Contains(s))
            {
                continue;
            }
            TimeZones.Shift(d, s, old, zone);
            if (d.Merged)
            {
                _indexer.Reorder(d);
                if (d == F)
                {
                    _view.ClearSelection();
                }
            }
        }
        s.Declared = zone;
        _st3.Text = s.Name + ": " + TimeZones.Describe(s, _localZone);
        StartFilter();
    }

    /// <summary>Applies what the user said about this file last time, before it is indexed.</summary>
    private void ApplySavedZone(LogSource s)
    {
        if (!s.Zoned && s.Kind != TsKind.None)
        {
            s.Declared = TimeZones.FromName(_cfg.Get(ZoneKey(s.Path), ""), _localZone);
            if (_cfg.Get(ZoneKey(s.Path), null) != null)
            {
                _zoneAsked.Add(s);
            }
        }
    }

    /// <summary>
    /// Settings are "key=value" lines split on the first '=', and a Windows path may
    /// hold one, so it is escaped out of the key.
    /// </summary>
    private static string ZoneKey(string path)
    {
        return "zone." + (path ?? "").ToUpperInvariant().Replace("%", "%25").Replace("=", "%3D");
    }

    /// <summary>The last stamped time of an undeclared file, which is its wall clock.</summary>
    private double LastWall(LogSource s)
    {
        foreach (var d in _docs)
        {
            var si = d.Srcs.IndexOf(s);
            if (si < 0)
            {
                continue;
            }
            for (var i = d.N - 1; i >= 0; i--)
            {
                if (d.Src[i] == si && !double.IsNaN(d.Ts[i]))
                {
                    return d.Ts[i];
                }
            }
        }
        return double.NaN;
    }

    private static DateTime LastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }
}
