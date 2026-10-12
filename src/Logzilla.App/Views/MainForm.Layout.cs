using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Logs;

namespace Logzilla.App.Views;

// Title bar, command bar, filter bar, timeline, panels — a fixed vertical stack
// per the redesign brief, each strip a fixed height except the log/inspector
// region which absorbs slack.

internal sealed partial class MainForm
{
    private const int Gap = 6;          // space between controls inside a group
    private const int GroupGap = 8;     // space either side of a group divider
    private const int BarPad = 12;      // left/right inset of every bar

    /// <summary>Inset either side of an inspector tab label. Tighter than the command
    /// bar's chips so all seven panels sit on one row at the inspector's usual width.</summary>
    private const int SideTabPadX = 4;

    private static FlatButton Btn(string text, string tip, int minWidth, EventHandler click)
    {
        var b = new FlatButton { Text = text };
        b.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
        Fit(b, minWidth);
        if (click != null)
        {
            b.Click += click;
        }
        if (tip != null)
        {
            new ToolTip().SetToolTip(b, tip);
            b.AccessibleName = tip;
        }
        return b;
    }

    /// <summary>
    /// Vertically centres every child of a single-row flow panel inside a bar of
    /// the given (unscaled) height. Per-control top margins guessed by hand were
    /// what left labels, buttons and chips sitting at different baselines.
    /// </summary>
    private static void CenterRow(FlowLayoutPanel row, int barHeight)
    {
        var h = Dpi.S(barHeight);
        foreach (Control c in row.Controls)
        {
            var m = c.Margin;
            var top = Math.Max(0, (h - c.Height) / 2);
            c.Margin = new Padding(m.Left, top, m.Right, 0);
        }
    }

    private static string Cap1(string s2)
    {
        return s2.Length == 0 ? s2 : char.ToUpperInvariant(s2[0]) + s2.Substring(1);
    }

    private static Label GroupLabel(string text)
    {
        var l = new Label();
        l.Text = text; l.AutoSize = true;
        l.ForeColor = Theme.Text2; l.Font = Theme.UiBody;
        l.BackColor = Color.Transparent;
        l.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
        return l;
    }

    /// <summary>1px full-width divider between two stacked bars.</summary>
    private static Panel HRule() => new() { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };

    private static Panel Divider()
    {
        var p = new Panel();
        p.Width = 1;
        p.Height = Dpi.S(20);
        p.BackColor = Theme.Line;
        p.Margin = new Padding(Dpi.S(GroupGap - Gap), 0, Dpi.S(GroupGap), 0);
        return p;
    }

    // Every button in a bar shares one height so their borders line up.
    private static void Fit(FlatButton b, int minWidth, int height = Theme.ControlHeight)
    {
        b.FitTo(minWidth, height, b.Text.Length <= 2 ? 0 : 12);
    }

    private static ToolStripMenuItem Mi(string text, Keys keys, EventHandler h)
    {
        var m = new ToolStripMenuItem(text);
        if (keys != Keys.None)
        {
            m.ShortcutKeys = keys;
        }
        if (h != null)
        {
            m.Click += h;
        }
        m.BackColor = Theme.BgChrome; m.ForeColor = Theme.Text;
        return m;
    }

    private BufferedPanel _brand;

    /// <summary>The title bar is the wordmark alone. The icon that used to sit beside
    /// it repeated what the window icon and the taskbar already show, and at this size
    /// it read as clutter against the type.</summary>
    private void PaintBrand(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgChrome);
        TextRenderer.DrawText(g, AppInfo.Name.ToUpperInvariant(), Theme.Wordmark,
            new Rectangle(Dpi.S(BarPad), 0, _brand.Width, _brand.Height), Theme.Text, Theme.TextFlags);
    }

    private static int BrandWidth()
    {
        var t = Dpi.Text(AppInfo.Name.ToUpperInvariant(), Theme.Wordmark);
        return Dpi.S(BarPad) + t.Width + Dpi.S(16);
    }

    private static void PaintVerticalHairline(object sender, PaintEventArgs e)
    {
        var c = (Control)sender;
        e.Graphics.Clear(c.BackColor);
        var h = Dpi.S(20);
        using var p = new Pen(Theme.Line);
        var x = c.Width / 2;
        e.Graphics.DrawLine(p, x, (c.Height - h) / 2, x, (c.Height + h) / 2);
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "UI construction is intentionally centralized for layout coherence.")]
    private void BuildUi()
    {
        // ---------- menu (kept for shortcuts; visually folded under the brand) ----------
        _menu = new MenuStrip();
        _menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        _menu.BackColor = Theme.BgChrome; _menu.ForeColor = Theme.Text;
        _menu.Font = Theme.UiBody;
        _menu.Padding = new Padding(6, 2, 0, 2);
        _menu.Dock = DockStyle.Fill;

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Mi("Open files...", Keys.Control | Keys.O, delegate { DoOpen(); }));
        file.DropDownItems.Add(Mi("Open folder...", Keys.Control | Keys.Shift | Keys.O, delegate { DoOpenFolder(); }));
        _miRecent = new ToolStripMenuItem("Recent files");
        // Filled when it opens, not at startup or on every file load: each entry asks
        // the file system whether its file still exists, and a path on a share that has
        // gone away can hold the UI thread for seconds. The placeholder only gives the
        // item its submenu arrow; it is replaced before the submenu is ever shown.
        _miRecent.DropDownItems.Add(new ToolStripMenuItem("(nothing yet)") { Enabled = false });
        _miRecent.DropDownOpening += delegate { RebuildRecentMenu(); };
        file.DropDownItems.Add(_miRecent);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Mi("Merge open files into one timeline", Keys.Control | Keys.M, delegate { DoMerge(); }));
        file.DropDownItems.Add(Mi("Export shown entries...", Keys.Control | Keys.E, delegate { DoExport(); }));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Mi("Close tab", Keys.Control | Keys.W, delegate { DoClose(_active); }));
        file.DropDownItems.Add(Mi("Close all tabs", Keys.None, delegate { while (_docs.Count > 0)
        {
            DoClose(0);
        } }));
        file.DropDownItems.Add(Mi("Exit", Keys.Alt | Keys.F4, delegate { Close(); }));

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(Mi("Find", Keys.Control | Keys.F, delegate { _q.Focus(); _q.SelectAll(); }));
        edit.DropDownItems.Add(Mi("Find next", Keys.F3, delegate { JumpMatch(1); }));
        edit.DropDownItems.Add(Mi("Find previous", Keys.Shift | Keys.F3, delegate { JumpMatch(-1); }));
        edit.DropDownItems.Add(Mi("Filter by field...", Keys.Control | Keys.Shift | Keys.F, delegate { OpenFieldEditorFromKeyboard(); }));
        edit.DropDownItems.Add(Mi("Clear field filters", Keys.None, delegate { ClearFieldFilters(); }));
        edit.DropDownItems.Add(Mi("Go to line...", Keys.Control | Keys.G, delegate { GoToLine(); }));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Mi("Select all shown entries", Keys.Control | Keys.A, delegate { SelectAllEntries(); }));
        _miCopy = Mi("Copy entry", Keys.Control | Keys.C, delegate { CopySelected(); });
        edit.DropDownItems.Add(_miCopy);
        edit.DropDownItems.Add(Mi("Copy all shown entries", Keys.Control | Keys.Shift | Keys.C, delegate { CopyShown(); }));
        edit.DropDownItems.Add(Mi("Copy bookmarked entries", Keys.None, delegate { CopyMarks(); }));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Mi("Bookmark entry", Keys.None, delegate { ToggleMark(); }));
        edit.DropDownItems.Add(Mi("Clear all bookmarks", Keys.None, delegate
        { if (F != null) { F.Marks.Clear(); _view.Invalidate(); FillSide(); } }));
        edit.DropDownItems.Add(Mi("Reset filters", Keys.Control | Keys.R, delegate { DoReset(); }));

        var view = new ToolStripMenuItem("&View");
        _miFollow = Mi("Follow file as it grows", Keys.F6, delegate { ToggleTail(); });
        _miRaw = Mi("Show raw lines", Keys.None, delegate
        { _view.Raw = !_view.Raw; _miRaw.Checked = _view.Raw; SetToggleStates(); _view.Invalidate(); });
        _miMulti = Mi("Keep stack traces with their entry", Keys.None, delegate
        { _group = !_group; _miMulti.Checked = _group; StartFilter(); });
        _miSrcCol = Mi("Show source column when merged", Keys.None, delegate
        { _view.ShowSource = !_view.ShowSource; _miSrcCol.Checked = _view.ShowSource; _view.Attach(F, _res); });
        _miMulti.Checked = true; _miSrcCol.Checked = true;
        view.DropDownItems.Add(_miFollow);
        view.DropDownItems.Add(_miRaw);
        view.DropDownItems.Add(_miMulti);
        view.DropDownItems.Add(_miSrcCol);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(BuildTimeMenu());
        var ask = Mi("AI Chat", Keys.None, delegate { _sideTab = "ask"; SyncSideTabs(); FillSide(); });
        ask.ShortcutKeyDisplayString = "Ctrl+8";   // the side-tab shortcut already handles the key
        view.DropDownItems.Add(ask);
        view.DropDownItems.Add(Mi("Time zone of this file...", Keys.None, delegate { ZoneOfCurrentFile(); }));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Mi("Larger text", Keys.Control | Keys.Oemplus, delegate { Zoom(1); }));
        view.DropDownItems.Add(Mi("Smaller text", Keys.Control | Keys.OemMinus, delegate { Zoom(-1); }));
        view.DropDownItems.Add(Mi("Reset text size", Keys.Control | Keys.D0, delegate { Zoom(0); }));

        var tools = new ToolStripMenuItem("&Tools");
        _miAssoc = Mi("Set up as the log file handler...", Keys.None, delegate { DoRegister(); });
        tools.DropDownItems.Add(_miAssoc);
        tools.DropDownItems.Add(Mi("Remove file associations", Keys.None, delegate { DoUnregister(); }));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Mi("AI settings...", Keys.None, delegate { ShowAISettings(); }));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Mi("Save setup...", Keys.None, delegate { SaveSetup(); }));
        tools.DropDownItems.Add(Mi("Load setup...", Keys.None, delegate { LoadSetup(); }));
        tools.DropDownItems.Add(Mi("Open settings folder", Keys.None, delegate
        {
            // Through Explorer: Process.Start on a bare folder path does not use the shell
            // on .NET, so it tried to run the folder as a program and silently failed.
            try
            {
                var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                System.Diagnostics.Process.Start(explorer, "\"" + Paths.Dir + "\"")?.Dispose();
            }
            catch (Exception ex) { IgnoreNonCritical(ex, "Open settings folder"); }
        }));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Mi("Shortcuts and query syntax", Keys.F1, delegate { ShowHelp(); }));
        help.DropDownItems.Add(Mi("About " + AppInfo.Name, Keys.None, delegate { ShowAbout(); }));

        foreach (var m in new[] { file, edit, view, tools, help })
        {
            m.ForeColor = Theme.Text;
            m.DropDown.BackColor = Theme.BgChrome;
            foreach (ToolStripItem it in m.DropDownItems) { it.BackColor = Theme.BgChrome; it.ForeColor = Theme.Text; }
            _menu.Items.Add(m);
        }

        // ---------- title bar: brand mark + wordmark, menu, file tabs ----------
        // Brand | menu | hairline | file tabs — one 40px row, so an empty tab
        // strip no longer costs a whole blank band under the menu.
        var titleRow = new BufferedPanel { Dock = DockStyle.Top, Height = Dpi.S(Theme.TitleBarHeight), BackColor = Theme.BgChrome };
        _brand = new BufferedPanel { Dock = DockStyle.Left, Width = BrandWidth(), BackColor = Theme.BgChrome };
        _brand.Paint += PaintBrand;
        _menu.Padding = new Padding(0);
        _menu.Dock = DockStyle.Top;
        var menuHost = new BufferedPanel
        {
            Dock = DockStyle.Left, BackColor = Theme.BgChrome,
            Width = _menu.PreferredSize.Width + Dpi.S(4),
            Padding = new Padding(0, Math.Max(0, (Dpi.S(Theme.TitleBarHeight) - _menu.PreferredSize.Height) / 2), 0, 0),
        };
        menuHost.Controls.Add(_menu);
        var titleDivider = new BufferedPanel { Dock = DockStyle.Left, Width = Dpi.S(17), BackColor = Theme.BgChrome };
        titleDivider.Paint += PaintVerticalHairline;

        _tabs = new TabStrip();
        _tabs.Dock = DockStyle.Fill;
        _tabs.Picked += delegate (object _, TabStrip.IdxArgs a)
        { if (a.I != _active) { SwitchTab(a.I); SyncTabs(); StartFilter(); } };
        _tabs.Closed += delegate (object _, TabStrip.IdxArgs a) { DoClose(a.I); };

        // Dock order: the last control added is docked first (outermost).
        titleRow.Controls.Add(_tabs);
        titleRow.Controls.Add(titleDivider);
        titleRow.Controls.Add(menuHost);
        titleRow.Controls.Add(_brand);
        var titleRule = HRule();

        // ---------- command bar ----------
        var cmdBar = new BufferedPanel { Dock = DockStyle.Top, Height = Dpi.S(Theme.CommandBarHeight), BackColor = Theme.BgChrome };
        var cmdLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false,
            BackColor = Theme.BgChrome, Padding = new Padding(Dpi.S(BarPad), 0, 0, 0),
        };

        BuildSearchHost(cmdLeft);

        _bPrev = Btn("‹", "Previous match  (Shift+Enter)", 30, delegate { JumpMatch(-1); });
        _bPrev.Margin = new Padding(Dpi.S(Gap), 0, Dpi.S(4), 0);
        cmdLeft.Controls.Add(_bPrev);
        _bNext = Btn("›", "Next match  (Enter)", 30, delegate { JumpMatch(1); });
        _bNext.Margin = new Padding(0);
        cmdLeft.Controls.Add(_bNext);

        cmdLeft.Controls.Add(Divider());

        _mode = new SegmentedControl { LeftLabel = "Highlight", RightLabel = "Filter to matches", Name = "SearchMode" };
        _mode.Fit();
        _mode.Margin = new Padding(0);
        _mode.AccessibleName = "Search mode: highlight matches or filter to matches";
        _mode.Changed += delegate { _filterMode = _mode.RightActive; StartFilter(); };
        cmdLeft.Controls.Add(_mode);

        cmdLeft.Controls.Add(Divider());
        BuildContextStepper(cmdLeft);
        cmdLeft.Controls.Add(Divider());
        BuildTimeGroup(cmdLeft);

        var cmdRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = Theme.BgChrome, Padding = new Padding(0, 0, Dpi.S(BarPad), 0),
        };

        _bMerge = Btn("Merge", "Merge open files into one timeline  (Ctrl+M)", 64, delegate { DoMerge(); });
        _bMerge.Name = "MergeButton";
        _bMerge.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
        _bMerge.Enabled = false; // nothing is open yet; SyncTabs turns it on
        cmdRight.Controls.Add(_bMerge);

        _mergeDivider = Divider();
        cmdRight.Controls.Add(_mergeDivider);

        _bWrap = new ToggleChip { Label = "Wrap", BackColor = Theme.BgChrome, Name = "WrapChip" };
        _bWrap.Height = Dpi.S(Theme.ControlHeight);
        _bWrap.Fit(60);
        _bWrap.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
        new ToolTip().SetToolTip(_bWrap, "Wrap long lines instead of scrolling sideways");
        _bWrap.AccessibleName = "Wrap long lines onto multiple rows";
        _bWrap.Click2 += delegate { _view.Wrap = !_view.Wrap; _bWrap.On = _view.Wrap; _bWrap.Invalidate(); _view.Invalidate(); };
        cmdRight.Controls.Add(_bWrap);

        _bTime = BuildTimeChip();
        _bTime.Name = "TimeChip";
        _bTime.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
        cmdRight.Controls.Add(_bTime);

        _bDate = new ToggleChip { Label = "Date", BackColor = Theme.BgChrome };
        _bDate.Height = Dpi.S(Theme.ControlHeight);
        _bDate.Fit(60);
        _bDate.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
        new ToolTip().SetToolTip(_bDate, "Collapse or expand the date and time column");
        _bDate.AccessibleName = "Show the date and time column";
        _bDate.Click2 += delegate { _view.ShowTime = !_view.ShowTime; _bDate.On = _view.ShowTime; _bDate.Invalidate(); _view.Invalidate(); };
        cmdRight.Controls.Add(_bDate);

        _bTail = new ToggleChip { Label = "Live tail", Dot = Theme.Ok, BackColor = Theme.BgChrome, Name = "TailChip" };
        _bTail.Height = Dpi.S(Theme.ControlHeight);
        _bTail.Fit(90);
        _bTail.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
        new ToolTip().SetToolTip(_bTail, "Keep reading the file as it grows  (F6)");
        _bTail.AccessibleName = "Follow the file as it grows";
        _bTail.Click2 += delegate { ToggleTail(); };
        cmdRight.Controls.Add(_bTail);

        var overflow = Btn("⋯", "More view options", 30, null);
        overflow.Margin = new Padding(0);
        overflow.Click += delegate { ShowCommandOverflow(overflow); };
        cmdRight.Controls.Add(overflow);

        CenterRow(cmdLeft, Theme.CommandBarHeight);
        CenterRow(cmdRight, Theme.CommandBarHeight);
        cmdBar.Controls.Add(cmdLeft);
        cmdBar.Controls.Add(cmdRight);
        cmdBar.Resize += delegate { FitSearchToBar(cmdBar, cmdLeft, cmdRight); };
        var cmdRule = HRule();

        // ---------- filter bar ----------
        var filterBar = new BufferedPanel { Dock = DockStyle.Top, Height = Dpi.S(Theme.FilterBarHeight), BackColor = Theme.BgChromeLow };
        var filterLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false,
            BackColor = Theme.BgChromeLow, Padding = new Padding(Dpi.S(BarPad), 0, 0, 0),
        };
        for (var l = 6; l >= 1; l--)
        {
            var lv = l;
            var chip = new ToggleChip { Label = Levels.Abbreviation[l], Dot = Theme.SevColor[l] };
            chip.Fit(70);
            chip.Margin = new Padding(0, 0, Dpi.S(Gap), 0);
            chip.AccessibleName = "Toggle " + Levels.Abbreviation[l] + " severity, right-click to isolate";
            chip.Click2 += delegate { _levelMask ^= (1 << lv); SyncLevels(); StartFilter(); };
            chip.RightClick += delegate { _levelMask = 1 | (1 << lv); SyncLevels(); StartFilter(); };
            _bLv[l] = chip;
            filterLeft.Controls.Add(chip);
        }

        var filterRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = Theme.BgChromeLow, Padding = new Padding(0, 0, Dpi.S(BarPad), 0),
        };
        _bFieldFilter = Btn("+ Field filter", "Filter on a field  (Ctrl+Shift+F)", 100, delegate
        {
            OpenFieldEditor(-1);
        });
        _bFieldFilter.Style = FlatButton.Look.Dashed;
        Fit(_bFieldFilter, 100, Theme.ChipHeight);
        filterRight.Controls.Add(_bFieldFilter);
        _bReset = Btn("Reset", "Clear query, levels and time range  (Ctrl+R)", 54, delegate { DoReset(); });
        Fit(_bReset, 54, Theme.ChipHeight);
        _bReset.Margin = new Padding(0);
        filterRight.Controls.Add(_bReset);
        CenterRow(filterLeft, Theme.FilterBarHeight);
        CenterRow(filterRight, Theme.FilterBarHeight);
        filterBar.Controls.Add(filterLeft);
        filterBar.Controls.Add(filterRight);
        var filterRule = HRule();

        // ---------- timeline ----------
        _tl = new Timeline { Name = "Timeline" };
        _tl.Dock = DockStyle.Top;
        _tl.RangeSelected += delegate (object _, Timeline.RangeArgs a)
        {
            _t0 = a.A; _t1 = a.B; _tl.T0 = a.A; _tl.T1 = a.B;
            _updatingTimeRange = true;
            _tRange.Text = double.IsNaN(a.A) ? "" : FormatShownRange(a.A, a.B);
            _tRange.ForeColor = Theme.Text;
            _bTRangeClear.ForeColor = double.IsNaN(a.A) ? Theme.TextDim : Theme.SevColor[4];
            _updatingTimeRange = false;
            UpdateTimeTrigger();
            StartFilter();
        };
        _tl.Seek += delegate (object _, Timeline.SeekArgs a) { SeekTime(a.T); };

        // ---------- centre ----------
        _split = new SplitContainer { Name = "MainSplit" };
        _split.Dock = DockStyle.Fill;
        _split.BackColor = Theme.BgChrome;
        _split.SplitterWidth = Dpi.S(3);
        _split.Panel1.BackColor = Theme.BgData;
        _split.Panel2.BackColor = Theme.BgChrome;

        _view = new LogView { Indexer = _indexer, JsonReader = _jsonReader, Name = "LogGrid" };
        _view.Dock = DockStyle.Fill;
        _view.Rules = _rules;
        _view.SelectionChanged += delegate
        {
            UpdateStatus();
            // The row expands inline, so the inspector keeps its current tab; the raw
            // detail dump only refreshes when the user has chosen to show it.
            if (_sideTab == "detail")
            {
                FillSide();
            }
            _mini.Invalidate();
            _tl.Invalidate();
        };
        _view.CopyEntryRequested += delegate { CopySelected(); };
        _view.CopyFieldRequested += delegate (object _, LogView.FieldCopyEventArgs a) { CopyText(a.Value, FieldNames.Display(a.Key)); };
        _view.CopyAsJsonRequested += delegate { CopySelectedAsJson(); };
        _view.NextErrorRequested += delegate { JumpSeverity(1, 5); };
        _view.BookmarkToggleRequested += delegate { ToggleMark(); };
        _view.MouseUp += ViewMouseUp;
        _view.MouseWheel += ViewWheel;

        _mini = new MiniMap { Name = "Minimap" };
        _mini.Dock = DockStyle.Right;
        _mini.View = _view;
        _mini.Jump += delegate (object _, MiniMap.JumpFractionEventArgs a) { _view.ScrollFraction(a.F); _mini.Invalidate(); };

        _split.Panel1.Controls.Add(_view);
        _split.Panel1.Controls.Add(_mini);

        // Inspector tabs: a strip of full-word text tabs, active one underlined. All
        // eight fit one 40px row at the panel's natural width; the strip wraps to a
        // second row rather than hiding any of them when the splitter is dragged in,
        // or larger text makes the labels wider.
        //
        // Sized by hand, not AutoSize: an auto-sized FlowLayoutPanel docked to the top
        // measures itself as one unbroken row, so it never wrapped — the last tabs
        // (Ask among them) were drawn past the panel's edge until the window grew.
        var tabs2 = new FlowLayoutPanel { Name = "SideTabs" };
        tabs2.Dock = DockStyle.Top;
        tabs2.BackColor = Theme.BgChrome;
        tabs2.WrapContents = true;
        tabs2.Height = Dpi.S(Theme.TitleBarHeight);
        tabs2.Padding = new Padding(Dpi.S(BarPad - 8), 0, 0, 0);
        _sideTabStrip = tabs2;
        tabs2.SizeChanged += delegate { FitSideTabs(); };
        foreach (var nm in SideNames)
        {
            var key = nm;
            var b = Btn(SideLabel(nm), null, 40, null);   // full word, never truncated
            b.Style = FlatButton.Look.Tab;
            b.Name = "SideTab." + nm;
            b.FitTo(40, Theme.TitleBarHeight, SideTabPadX);
            b.Margin = new Padding(0);
            b.Click += delegate { _sideTab = key; SyncSideTabs(); FillSide(); };
            tabs2.Controls.Add(b);
            _sideTabs.Add(b);
            if (nm == "marks")
            {
                _bMarksSideTab = b;
            }
        }
        var tabsRule = HRule();

        _sidePanel = new BufferedPanel { Name = "SidePanel" };
        _sidePanel.Dock = DockStyle.Fill;
        _sidePanel.BackColor = Theme.BgChrome;

        _side = new PanelList();
        _side.Dock = DockStyle.Fill;
        _side.Activate += SideActivate;

        _entry = new TextBox { Name = "EntryText" };
        _entry.Multiline = true; _entry.ReadOnly = true; _entry.ScrollBars = ScrollBars.Both;
        _entry.WordWrap = false;
        // Lift the edit control's 32K default so a long payload is never cut short.
        _entry.MaxLength = int.MaxValue;
        _entry.Dock = DockStyle.Fill; _entry.BackColor = Theme.BgChrome; _entry.ForeColor = Theme.Text2;
        _entry.Margin = new Padding(0);
        _entry.BorderStyle = BorderStyle.None; _entry.Font = Theme.MonoSmall;

        _sidePanel.Padding = new Padding(0, Dpi.S(8), 0, 0);
        _entryHost = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Theme.BgChrome, Padding = new Padding(Dpi.S(12), Dpi.S(6), 0, 0), Visible = false };
        _entry.Visible = true;
        _entryHost.Controls.Add(_entry);
        _sidePanel.Controls.Add(_side);
        _sidePanel.Controls.Add(_entryHost);
        _sidePanel.Controls.Add(BuildAskPanel());
        _sidePanel.Controls.Add(BuildHighlightBar());   // added last, so it docks above the list
        _split.Panel2.Controls.Add(_sidePanel);
        _split.Panel2.Controls.Add(tabsRule);
        _split.Panel2.Controls.Add(tabs2);

        BuildStatusBar();
        BuildFieldBar();

        // Top-docked strips: the last one added sits highest.
        Controls.Add(_split);
        Controls.Add(HRule());
        Controls.Add(_tl);
        Controls.Add(_fieldBar);   // under the filter bar, shown only while a field filter is set
        Controls.Add(filterRule);
        Controls.Add(filterBar);
        Controls.Add(cmdRule);
        Controls.Add(cmdBar);
        Controls.Add(titleRule);
        Controls.Add(titleRow);
        Controls.Add(_statusHost);
        MainMenuStrip = _menu;

        SyncLevels(); SyncSideTabs(); SetToggleStates(); FillSide(); UpdateStatus(); UpdateTimeTrigger();



        DragEnter += delegate (object _, DragEventArgs a)
        { if (a.Data != null && a.Data.GetDataPresent(DataFormats.FileDrop))
        {
            a.Effect = DragDropEffects.Copy;
        } };
        DragDrop += delegate (object _, DragEventArgs a)
        {
            if (a.Data == null)
            {
                return;
            }

            var paths = (string[])a.Data.GetData(DataFormats.FileDrop);
            OpenMany(paths);
        };
        KeyDown += FormKeyDown;
        FormClosing += delegate { _tailTimer.Stop(); SaveState(); CloseAssistant(); };
    }

    private BufferedPanel _searchHost;
    private BufferedPanel _entryHost;
    private bool _searchFocused;

    private void BuildSearchHost(FlowLayoutPanel cmdLeft)
    {
        _searchHost = new BufferedPanel
        {
            Width = Dpi.S(SearchMinWidth),
            Height = Dpi.S(Theme.FieldHeight),
            BackColor = Theme.BgChrome,
            Margin = new Padding(0, 0, Dpi.S(Gap), 0),
        };
        _searchHost.Paint += PaintSearchHost;

        _q = new TextBox { Name = "SearchBox" };
        _q.BorderStyle = BorderStyle.None;
        _q.BackColor = Theme.BgData; _q.ForeColor = Theme.Text;
        _q.Font = Theme.MonoBody;
        _q.AccessibleName = "Search log entries";
        _q.TextChanged += delegate
        {
            _debounce.Stop(); _debounce.Start();
            UpdateSearchAffordances();
        };
        _q.KeyDown += QueryKeyDown;
        _q.Enter += delegate { _searchFocused = true; _searchHost.Invalidate(); };
        _q.Leave += delegate { _searchFocused = false; _searchHost.Invalidate(); };
        _searchHost.Controls.Add(_q);

        _hits = new Label { Name = "HitCount" };
        _hits.AutoSize = false;
        _hits.TextAlign = ContentAlignment.MiddleRight;
        _hits.ForeColor = Theme.Text3; _hits.Font = Theme.MonoSmall;
        _hits.BackColor = Theme.BgData;
        _searchHost.Controls.Add(_hits);

        _bRegex = new ToggleChip { Label = ".*", BorderlessOff = true, Font = Theme.MonoBody, Name = "RegexChip" };
        _bRegex.BackColor = Theme.BgData;
        new ToolTip().SetToolTip(_bRegex, "Use regular expression");
        _bRegex.AccessibleName = "Use regular expression";
        _bRegex.Click2 += delegate { _regex = !_regex; SetToggleStates(); StartFilter(); };
        _searchHost.Controls.Add(_bRegex);

        _bCase = new ToggleChip { Label = "Aa", BorderlessOff = true };
        _bCase.BackColor = Theme.BgData;
        new ToolTip().SetToolTip(_bCase, "Match case");
        _bCase.AccessibleName = "Match case";
        _bCase.Click2 += delegate { _case = !_case; SetToggleStates(); StartFilter(); };
        _searchHost.Controls.Add(_bCase);

        _bClear = Btn("×", "Clear the search box  (Esc)", 22, delegate { ClearFind(); });
        _bClear.Style = FlatButton.Look.Ghost;
        _bClear.BackColor = Theme.BgData;
        _bClear.ForeColor = Theme.TextDim;
        _bClear.Visible = false;
        _searchHost.Controls.Add(_bClear);

        cmdLeft.Controls.Add(_searchHost);
        PositionSearchHost();
    }

    private const int SearchMinWidth = 220;
    private const int SearchMaxWidth = 560;

    // Layout inside the field, right to left:  [Aa][.*] | [×][1 of 61]  text  [icon]
    private int _searchDividerX;

    private void PositionSearchHost()
    {
        var h = _searchHost.Height;
        var inner = h - Dpi.S(10);
        var top = (h - inner) / 2;
        var sq = inner;

        _bCase.SetBounds(_searchHost.Width - Dpi.S(5) - sq, top, sq, inner);
        _bRegex.SetBounds(_bCase.Left - Dpi.S(2) - sq, top, sq, inner);
        _searchDividerX = _bRegex.Left - Dpi.S(6);

        // Plus the label's own padding: measured to the pixel, a count as long as the
        // sample ("1,234,567 hits") wrapped its last word onto a line nobody could see.
        var hitsW = Dpi.Text("9,999 of 9,999", Theme.MonoSmall).Width + Dpi.S(8);
        _hits.SetBounds(_searchDividerX - Dpi.S(8) - hitsW, top, hitsW, inner);
        _bClear.SetBounds(_hits.Left - Dpi.S(2) - Dpi.S(22), (h - Dpi.S(22)) / 2, Dpi.S(22), Dpi.S(22));

        var textLeft = Dpi.S(30);
        _q.Left = textLeft;
        _q.Top = (h - _q.Height) / 2;
        _q.Width = Math.Max(20, _bClear.Left - Dpi.S(4) - textLeft);
        _searchHost.Invalidate();
    }

    private Panel _mergeDivider;
    private bool _mergeFolded;

    /// <summary>Gives the search field whatever width the command bar has spare,
    /// between the brief's 560px and a usable minimum.</summary>
    private void FitSearchToBar(Control cmdBar, FlowLayoutPanel cmdLeft, Control cmdRight)
    {
        var used = cmdLeft.Padding.Horizontal;
        foreach (Control c in cmdLeft.Controls)
        {
            if (c != _searchHost)
            {
                used += c.Width + c.Margin.Horizontal;
            }
        }
        used += _searchHost.Margin.Horizontal;

        // Too narrow for both groups (a window under ~1000px wide), the right-hand group
        // was drawn over the search mode switch. Merge steps aside first: it is also
        // File › Merge and Ctrl+M.
        // Our own flag, not Visible: that reads false for every control until the window
        // itself is shown.
        var mergeW = _bMerge.Width + _bMerge.Margin.Horizontal + _mergeDivider.Width + _mergeDivider.Margin.Horizontal;
        var rightFull = cmdRight.Width + (_mergeFolded ? mergeW : 0);
        var fold = cmdBar.ClientSize.Width - rightFull - used - Dpi.S(GroupGap) < Dpi.S(SearchMinWidth);
        if (fold != _mergeFolded)
        {
            _mergeFolded = fold;
            _bMerge.Visible = !fold;
            _mergeDivider.Visible = !fold;
        }
        var avail = cmdBar.ClientSize.Width - (fold ? rightFull - mergeW : rightFull) - used - Dpi.S(GroupGap);
        var w = Math.Clamp(avail, Dpi.S(SearchMinWidth), Dpi.S(SearchMaxWidth));
        if (w != _searchHost.Width)
        {
            _searchHost.Width = w;
            PositionSearchHost();
        }

        // Whatever still does not fit would sit under the right-hand group, a sliver of
        // it showing at the edge. It is left out instead, and back once there is room.
        var limit = cmdBar.ClientSize.Width - (fold ? rightFull - mergeW : rightFull);
        // Worked out from sizes alone: Visible reads false for everything until the
        // window is shown, so it cannot say what was showing before.
        var x = cmdLeft.Padding.Left;
        var fits = 0;
        while (fits < cmdLeft.Controls.Count)
        {
            var c = cmdLeft.Controls[fits];
            x += c.Margin.Left + c.Width;
            if (x > limit && c != _searchHost)
            {
                break;
            }
            x += c.Margin.Right;
            fits++;
        }
        // Whole groups only — not "Context −" without its "0 +" — and no divider left
        // with nothing after it: the cut goes back to the divider before the group.
        if (fits < cmdLeft.Controls.Count)
        {
            var d = fits - 1;
            while (d > 0 && cmdLeft.Controls[d].GetType() != typeof(Panel))
            {
                d--;
            }
            if (d > 0)
            {
                fits = d;
            }
        }
        for (var k = 0; k < cmdLeft.Controls.Count; k++)
        {
            cmdLeft.Controls[k].Visible = k < fits;
        }
    }

    private void PaintSearchHost(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgChrome);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, _searchHost.Width - 1, _searchHost.Height - 1);
        using (var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusField)))
        {
            using (var b = new SolidBrush(Theme.BgData))
            {
                g.FillPath(b, path);
            }
            using (var p = new Pen(_searchFocused ? Theme.AccentDim : Theme.LineControl))
            {
                g.DrawPath(p, path);
            }
        }
        using (var p = new Pen(Theme.Text3, Dpi.Factor * 1.3f))
        {
            var cy = _searchHost.Height / 2f;
            var rad = Dpi.S(4.5f);
            var cx = Dpi.S(14f);
            g.DrawEllipse(p, cx - rad, cy - rad - Dpi.S(1f), rad * 2, rad * 2);
            var d = rad * 0.72f;
            g.DrawLine(p, cx + d, cy + d - Dpi.S(1f), cx + d + Dpi.S(3.5f), cy + d + Dpi.S(2.5f));
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        using (var p = new Pen(Theme.LineControl))
        {
            var ih = Dpi.S(16);
            var y0 = (_searchHost.Height - ih) / 2;
            g.DrawLine(p, _searchDividerX, y0, _searchDividerX, y0 + ih);
        }
    }

    private void UpdateSearchAffordances()
    {
        if (_bClear != null)
        {
            _bClear.Visible = _q.Text.Length > 0;
        }
    }

    private void BuildContextStepper(FlowLayoutPanel cmdLeft)
    {
        var cap = GroupLabel("Context");
        cmdLeft.Controls.Add(cap);

        _ctx = new NumericUpDown();
        _ctx.Minimum = 0; _ctx.Maximum = 50;
        _ctx.ValueChanged += delegate { StartFilter(); UpdateCtxLabel(); };

        var minus = Btn("−", "Fewer context lines", 30, delegate { if (_ctx.Value > _ctx.Minimum)
        {
            _ctx.Value--;
        } });
        minus.Margin = new Padding(0);
        cmdLeft.Controls.Add(minus);

        _ctxLabel = new Label
        {
            AutoSize = false, Width = Dpi.S(34), Height = Dpi.S(Theme.ControlHeight),
            TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
            Font = Theme.MonoBody, ForeColor = Theme.Text, Margin = new Padding(0),
            AccessibleName = "Context lines",
        };
        cmdLeft.Controls.Add(_ctxLabel);

        var plus = Btn("+", "More context lines", 30, delegate { if (_ctx.Value < _ctx.Maximum)
        {
            _ctx.Value++;
        } });
        plus.Margin = new Padding(0);
        cmdLeft.Controls.Add(plus);
        UpdateCtxLabel();
    }

    private Label _ctxLabel;

    private void UpdateCtxLabel()
    {
        if (_ctxLabel != null)
        {
            var v = (int)_ctx.Value;
            _ctxLabel.Text = v == 0 ? "0" : "±" + v.ToString(CultureInfo.InvariantCulture);
        }
    }

    private ToolStripDropDown _timeDropDown;
    private FlatButton _bTimeTrigger;

    // The time-range field is a power-user convenience layered on top of the
    // Timeline's drag-to-select — not part of the brief's own 1440px command-bar
    // budget, so it lives behind a small trigger button instead of its own
    // always-visible group (which would starve the spec'd search field of room).
    private void BuildTimeGroup(FlowLayoutPanel cmdLeft)
    {
        _tRange = new TextBox { Name = "TimeRange" };
        _tRange.Width = Dpi.S(170);
        _tRange.BackColor = Theme.BgData; _tRange.ForeColor = Theme.Text; _tRange.BorderStyle = BorderStyle.FixedSingle;
        _tRange.Font = Theme.MonoSmall;
        _tRange.Location = new Point(Dpi.S(8), Dpi.S(7));
        _tRange.AccessibleName = "Time range filter";
        var tip = new ToolTip();
        tip.SetToolTip(_tRange,
            "Filter by time range\r\n" +
            "Examples:\r\n" +
            "  20:00 - 21:00\r\n" +
            "  8pm - 9pm\r\n" +
            "  8:30am - 5:30pm\r\n" +
            "  2026-01-15 08:00 - 2026-01-15 09:30\r\n" +
            "  2026-01-15 08:00 - 09:30");
        _tRange.TextChanged += delegate
        {
            if (_updatingTimeRange) { return; }
            _debounce.Stop(); _debounce.Start();
            _bTRangeClear.ForeColor = _tRange.Text.Length > 0 ? Theme.SevColor[4] : Theme.TextDim;
            UpdateTimeTrigger();
        };

        _bTRangeClear = Btn("×", "Clear the time filter", 22, delegate
        {
            _updatingTimeRange = true;
            _tRange.Text = "";
            _tRange.ForeColor = Theme.Text;
            _updatingTimeRange = false;
            _bTRangeClear.ForeColor = Theme.TextDim;
            _t0 = _t1 = double.NaN;
            _tl.T0 = _tl.T1 = double.NaN;
            _tl.Invalidate();
            StartFilter();
            UpdateTimeTrigger();
        });
        _bTRangeClear.Style = FlatButton.Look.Ghost;
        _bTRangeClear.SetBounds(_tRange.Right + Dpi.S(4), _tRange.Top + (_tRange.Height - Dpi.S(22)) / 2, Dpi.S(22), Dpi.S(22));
        _bTRangeClear.ForeColor = Theme.TextDim;

        var host = new BufferedPanel
        {
            Size = new Size(_bTRangeClear.Right + Dpi.S(8), _tRange.Bottom + Dpi.S(7)),
            BackColor = Theme.BgChrome,
        };
        host.Controls.Add(_tRange);
        host.Controls.Add(_bTRangeClear);

        _timeDropDown = new ToolStripDropDown { Padding = new Padding(4), BackColor = Theme.BgChrome };
        _timeDropDown.Items.Add(new ToolStripControlHost(host) { Margin = new Padding(0), AutoSize = false, Size = host.Size });

        _bTimeTrigger = Btn("Time", "Filter by time range", 54, null);
        _bTimeTrigger.Name = "TimeButton";
        _bTimeTrigger.Margin = new Padding(0);
        _bTimeTrigger.Click += delegate
        {
            _timeDropDown.Show(_bTimeTrigger, new Point(0, _bTimeTrigger.Height));
            _tRange.Focus();
        };
        cmdLeft.Controls.Add(_bTimeTrigger);
    }

    private void UpdateTimeTrigger()
    {
        var active = _tRange.Text.Length > 0;
        _bTimeTrigger.BorderColor = active ? Theme.SevColor[4] : Theme.LineControl;
        _bTimeTrigger.ForeColor = active ? Theme.Text : Theme.Text2;
    }

    private void ShowCommandOverflow(Control anchor)
    {
        var cm = new ContextMenuStrip();
        cm.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        cm.BackColor = Theme.BgChrome; cm.ForeColor = Theme.Text; cm.Font = Theme.UiBody;
        var raw = Mi("Show raw lines", Keys.None, delegate { _miRaw.PerformClick(); });
        raw.Checked = _view.Raw;
        var multi = Mi("Keep stack traces with their entry", Keys.None, delegate { _miMulti.PerformClick(); });
        multi.Checked = _group;
        var src = Mi("Show source column when merged", Keys.None, delegate { _miSrcCol.PerformClick(); });
        src.Checked = _view.ShowSource;
        cm.Items.Add(raw); cm.Items.Add(multi); cm.Items.Add(src);
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add(Mi("Larger text  (Ctrl+=)", Keys.None, delegate { Zoom(1); }));
        cm.Items.Add(Mi("Smaller text  (Ctrl+-)", Keys.None, delegate { Zoom(-1); }));
        cm.Items.Add(Mi("Reset text size  (Ctrl+0)", Keys.None, delegate { Zoom(0); }));
        foreach (ToolStripItem it in cm.Items) { it.BackColor = Theme.BgChrome; it.ForeColor = Theme.Text; }
        cm.Show(anchor, new Point(0, anchor.Height));
    }

    private BufferedPanel _statusHost;

    private void BuildStatusBar()
    {
        _statusHost = new BufferedPanel { Dock = DockStyle.Bottom, Height = Dpi.S(Theme.StatusBarHeight), BackColor = Theme.BgChromeLow };
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false,
            BackColor = Theme.BgChromeLow, Padding = new Padding(Dpi.S(BarPad), 0, 0, 0),
        };
        _st1 = new Label { Name = "StatusText", AutoSize = true, ForeColor = Theme.Text3, Font = Theme.MonoSmall, Margin = new Padding(0) };
        left.Controls.Add(_st1);

        // Only painted while indexing or scanning; an idle empty track reads as a stray box.
        _progHost = new Panel { Size = new Size(Dpi.S(120), Dpi.S(4)), BackColor = Theme.BgChromeLow, Margin = new Padding(Dpi.S(12), 0, 0, 0) };
        _progHost.Paint += delegate (object _, PaintEventArgs pe)
        {
            if (_progPct < 0)
            {
                return;
            }
            using (var t = new SolidBrush(Theme.BgControl))
            {
                pe.Graphics.FillRectangle(t, _progHost.ClientRectangle);
            }
            using var b = new SolidBrush(Theme.Accent);
            pe.Graphics.FillRectangle(b, 0, 0, _progHost.Width * _progPct / 100, _progHost.Height);
        };
        left.Controls.Add(_progHost);

        _st3 = new Label { AutoSize = true, ForeColor = Theme.SevColor[4], Font = Theme.MonoSmall, Margin = new Padding(Dpi.S(12), 0, 0, 0) };
        left.Controls.Add(_st3);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = Theme.BgChromeLow, Padding = new Padding(0, 0, Dpi.S(BarPad), 0),
        };
        _statusDot = new Panel { Width = Dpi.S(6), Height = Dpi.S(6), BackColor = Theme.TextDim, Margin = new Padding(0, 0, Dpi.S(8), 0) };
        right.Controls.Add(_statusDot);
        _stCount = new Label { Name = "StatusCount", AutoSize = true, ForeColor = Theme.Text, Font = Theme.MonoSmall, Margin = new Padding(0) };
        right.Controls.Add(_stCount);
        _stHint = new Label { AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.MonoSmall, Margin = new Padding(0) };
        right.Controls.Add(_stHint);
        var keyCap = new Label
        {
            AutoSize = false, Text = "Ctrl K", ForeColor = Theme.Text2, Font = Theme.MonoSmall, BackColor = Theme.BgControl,
            TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(Dpi.S(4), 0, Dpi.S(6), 0),
        };
        keyCap.Size = new Size(Dpi.Text("Ctrl K", Theme.MonoSmall).Width + Dpi.S(12), Dpi.S(18));
        right.Controls.Add(keyCap);
        var cmdHint = new Label { AutoSize = true, Text = "commands", ForeColor = Theme.TextDim, Font = Theme.MonoSmall, Margin = new Padding(0) };
        right.Controls.Add(cmdHint);

        CenterRow(left, Theme.StatusBarHeight);
        CenterRow(right, Theme.StatusBarHeight);
        _statusHost.Controls.Add(left);
        _statusHost.Controls.Add(right);
        _statusHost.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line });

        // The left run spans the whole bar, under the right-hand one, so a long message
        // ("Search stopped: the regular expression took too long…") ran on beneath the
        // counts and lost its end. It now stops short of them, with "…" and the whole
        // text as a tooltip.
        _st3.AutoEllipsis = true;
        void FitMessage()
        {
            var room = right.Left - _st3.Left - _st3.Margin.Right - Dpi.S(8);
            _st3.MaximumSize = new Size(Math.Max(Dpi.S(40), room), _st3.Font.Height + Dpi.S(2));
        }
        right.LocationChanged += delegate { FitMessage(); };
        right.SizeChanged += delegate { FitMessage(); };
        _st3.LocationChanged += delegate { FitMessage(); };
    }

    private void ApplySettings()
    {
        int w = _cfg.GetInt("win.w", 0), h = _cfg.GetInt("win.h", 0);
        if (w > 700 && h > 450 && w < 20000 && h < 20000)
        {
            ClientSize = new Size(w, h);
        }
        if (_cfg.GetBool("win.max", false))
        {
            WindowState = FormWindowState.Maximized;
        }
        _fontSize = Math.Max(6.5f, Math.Min(18f, _cfg.GetInt("font", 115) / 10f));
        ApplyFontSize();
        _levelMask = _cfg.GetInt("levels", 0x7F) | 1;
        _view.ShowSource = _cfg.GetBool("srccol", true);
        _miSrcCol.Checked = _view.ShowSource;
        SyncLevels();
        _timeMode = (TimeMode)Math.Clamp(_cfg.GetInt("timemode", 0), 0, 2);
        SyncTimeMode();
        var sd = _cfg.GetInt("split", 0);
        Shown += delegate
        {
            try
            {
                // A window restored from a bigger monitor must not hang off this one,
                // or the side panel ends up half off-screen.
                var wa = Screen.FromControl(this).WorkingArea;
                if (WindowState == FormWindowState.Normal)
                {
                    int bw = Math.Min(Width, wa.Width), bh = Math.Min(Height, wa.Height);
                    var nx = Math.Max(wa.Left, Math.Min(Left, wa.Right - bw));
                    var ny = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - bh));
                    Bounds = new Rectangle(nx, ny, bw, bh);
                }
                var side = Math.Min(Dpi.S(Theme.InspectorWidth + 20), (int)(ClientSize.Width * 0.34));
                var want = sd > 0 ? sd : Math.Max(Dpi.S(300), ClientSize.Width - side);
                var lo = Math.Min(Dpi.S(260), Math.Max(0, _split.Width - Dpi.S(60)));
                var hi = Math.Max(lo, _split.Width - Dpi.S(240));
                _split.SplitterDistance = Math.Max(lo, Math.Min(want, hi));
            }
            catch (Exception ex) { IgnoreNonCritical(ex, "Apply window geometry"); }
        };
    }

    private void ApplyFontSize()
    {
        // Font size only scales the log body; chrome stays at the design's fixed
        // pixel sizes so the redesign's proportions hold at any zoom level.
        var px = _fontSize * Math.Max(0.5f, Dpi.Factor);
        _view.Font = new Font(Theme.MonoBody.FontFamily, px, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void SaveState()
    {
        try
        {
            _cfg.Set("win.max", WindowState == FormWindowState.Maximized);
            if (WindowState == FormWindowState.Normal)
            { _cfg.Set("win.w", ClientSize.Width); _cfg.Set("win.h", ClientSize.Height); }
            _cfg.Set("font", (int)Math.Round(_fontSize * 10));
            _cfg.Set("levels", _levelMask);
            _cfg.Set("srccol", _view.ShowSource);
            _cfg.Set("timemode", (int)_timeMode);
            _cfg.Set("split", _split.SplitterDistance);
            var sb = new StringBuilder();
            for (var i = 0; i < _rules.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('');
                }
                sb.Append(_rules[i].On ? "1" : "0").Append('')
                  .Append(_rules[i].IsRegex ? "1" : "0").Append('')
                  .Append(_rules[i].C.ToArgb().ToString(CultureInfo.InvariantCulture)).Append('')
                  .Append(_rules[i].Pattern);
            }
            _cfg.Set("rules", sb.ToString());
            _cfg.Save();
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Persist UI settings"); }
    }

    private void SetToggleStates()
    {
        _bRegex.On = _regex; _bRegex.Invalidate();
        _bCase.On = _case; _bCase.Invalidate();
        _mode.RightActive = _filterMode; _mode.Invalidate();
        _bTail.On = _tail; _bTail.Invalidate();
        _bWrap.On = _view.Wrap; _bWrap.Invalidate();
        _bDate.On = _view.ShowTime; _bDate.Invalidate();
    }

    private void SyncLevels()
    {
        _levelMask |= 1; // severity 0 ("no level detected") is always shown — there is no chip for it
        // Chip counts come from the whole file, not the filtered result: FilterResult.Counts
        // only tallies lines that passed, so a switched-off level would read 0 and the
        // chip would lock itself as "zero count" with no way to switch it back on.
        var totals = SeverityTotals(F);
        for (var l = 1; l <= 6; l++)
        {
            var on = ((_levelMask >> l) & 1) == 1;
            var chip = _bLv[l];
            chip.On = on;
            var count = totals != null ? totals[l] : -1;
            chip.Count = count >= 0 ? DisplayFormatter.Number(count) : "";
            chip.Enabled2 = count != 0;
            chip.Fit(70);
            chip.Invalidate();
        }
        UpdateResetBadge();
    }

    private LogDocument _totalsDoc;
    private int _totalsN = -1;
    private readonly int[] _totals = new int[7];

    /// <summary>Per-severity line counts for the whole document, cached until it grows.</summary>
    private int[] SeverityTotals(LogDocument f)
    {
        if (f == null)
        {
            return null;
        }
        if (f != _totalsDoc || f.N != _totalsN)
        {
            Array.Clear(_totals);
            var sev = f.Sev;
            for (var i = 0; i < f.N; i++)
            {
                _totals[sev[i]]++;
            }
            _totalsDoc = f;
            _totalsN = f.N;
        }
        return _totals;
    }

    private void UpdateResetBadge()
    {
        var active = 0;
        if (_q.Text.Length > 0)
        {
            active++;
        }
        if ((_levelMask | 1) != 0x7F)
        {
            active++;
        }
        if (!double.IsNaN(_t0))
        {
            active++;
        }
        if (_srcMask != -1)
        {
            active++;
        }
        active += _fieldFilters.Count;
        var text = active > 0 ? "Reset (" + active + ")" : "Reset";
        if (_bReset.Text != text)
        {
            _bReset.Text = text;
            Fit(_bReset, 54, Theme.ChipHeight);
            _bReset.ForeColor = active > 0 ? Theme.Text : Theme.Text2;
        }
    }

    /// <summary>Gives the inspector's tab strip as many rows as its width needs.</summary>
    private void FitSideTabs()
    {
        var strip = _sideTabStrip;
        if (strip == null || strip.Width <= 0)
        {
            return;
        }
        var h = Math.Max(Dpi.S(Theme.TitleBarHeight), strip.GetPreferredSize(new Size(strip.Width, 0)).Height);
        if (strip.Height != h)
        {
            strip.Height = h;
        }
    }

    private void SyncSideTabs()
    {
        for (var i = 0; i < _sideTabs.Count; i++)
        {
            _sideTabs[i].Selected = SideNames[i] == _sideTab;
        }
        _entryHost.Visible = _sideTab == "detail";
        if (_hlBar != null)
        {
            _hlBar.Visible = _sideTab == "colors";
        }
        _side.Visible = _sideTab != "detail" && _sideTab != "ask";
        if (_ask != null)
        {
            var wasHidden = !_ask.Visible;
            _ask.Visible = _sideTab == "ask";
            if (_ask.Visible && wasHidden)
            {
                RefreshAsk();
                _ask.FocusInput();
            }
        }
    }

    private void SyncTabs()
    {
        _tabs.Titles.Clear(); _tabs.Subs.Clear(); _tabs.Merged.Clear(); _tabs.WorstSev.Clear();
        foreach (var d in _docs)
        {
            _tabs.Titles.Add(d.Name);
            _tabs.Subs.Add(DisplayFormatter.Number(d.N));
            _tabs.Merged.Add(d.Merged);
            _tabs.WorstSev.Add(WorstSeverity(d));
        }
        _tabs.Active = _active;
        _tabs.Invalidate();
        SyncMergeButton();
    }

    /// <summary>Worst severity so far per document, and how many lines it covers.</summary>
    private readonly Dictionary<LogDocument, (int Lines, int Worst)> _worstSev = new();

    /// <summary>
    /// The worst severity in a document, extended over only the lines added since the
    /// last call. SyncTabs runs on every indexing tick and every filter, and rescanning
    /// every open file each time was a full pass per tab on the UI thread. Lines that
    /// are already indexed only ever change when following a file rewrites them, and
    /// TailPoll forgets the document's entry before it does.
    /// </summary>
    private int WorstSeverity(LogDocument d)
    {
        var n = d.N;
        var (from, worst) = _worstSev.TryGetValue(d, out var known) && known.Lines <= n ? known : (0, -1);
        var sev = d.Sev;
        for (var i = from; i < n; i++)
        {
            if (sev[i] > worst)
            {
                worst = sev[i];
            }
        }
        _worstSev[d] = (n, worst);
        return worst;
    }

    /// <summary>Merging needs at least two unmerged files to combine.</summary>
    private void SyncMergeButton()
    {
        if (_bMerge == null)
        {
            return;
        }
        // Same test DoMerge applies, so the button is never enabled for a merge
        // that would only produce the "needs at least two files" dialog.
        var candidates = 0;
        foreach (var d in _docs)
        {
            if (!d.Merged && d.N > 0)
            {
                candidates++;
            }
        }
        _bMerge.Enabled = candidates > 1;
        _bMerge.Invalidate();
    }

    private void RebuildRecentMenu()
    {
        _miRecent.DropDownItems.Clear();
        var l = Settings.LoadRecent();
        if (l.Count == 0)
        {
            var none = Mi("(nothing yet)", Keys.None, null);
            none.Enabled = false;
            _miRecent.DropDownItems.Add(none);
            return;
        }
        foreach (var path in l)
        {
            var p = path;
            var it = Mi(Path.GetFileName(p) + "    " + Path.GetDirectoryName(p), Keys.None,
                delegate { OpenMany(new[] { p }); });
            it.Enabled = System.IO.File.Exists(p);
            _miRecent.DropDownItems.Add(it);
        }
        _miRecent.DropDownItems.Add(new ToolStripSeparator());
        _miRecent.DropDownItems.Add(Mi("Clear this list", Keys.None, delegate
        {
            try { File.WriteAllText(Paths.Recent, ""); }
            catch (Exception ex) { IgnoreNonCritical(ex, "Clear recent list"); }
            RebuildRecentMenu();
        }));
    }

}
