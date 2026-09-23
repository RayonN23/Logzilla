using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Views;


internal sealed partial class MainForm : Form

{
    private const int DebounceMs = 260;
    private const int TailPollMs = 1000;


    private List<LogDocument> _docs = new();
    private int _active = -1;

    private LogView _view;
    private MiniMap _mini;
    private Timeline _tl;
    private PanelList _side;
    private TextBox _entry;
    private BufferedPanel _sidePanel;
    private TabStrip _tabs;
    private MenuStrip _menu;
    private ToolStripMenuItem _miRecent, _miFollow, _miRaw, _miMulti, _miSrcCol, _miAssoc, _miCopy;
    private TextBox _q;
    private ToggleChip _bRegex, _bCase, _bWrap, _bTail, _bDate;
    private SegmentedControl _mode;
    private FlatButton _bPrev, _bNext, _bClear, _bReset, _bFieldFilter, _bMerge;
    private Label _hits;
    /// <summary>Index 0 unused (severity 0 = "none" is always shown); 1..6 = TRACE..FATAL.</summary>
    private readonly ToggleChip[] _bLv = new ToggleChip[7];
    private NumericUpDown _ctx;
    private Label _st1, _st3, _stCount, _stHint;
    private Panel _statusDot;
    private Panel _progHost;
    private System.Windows.Forms.Timer _debounce, _tailTimer;
    private SplitContainer _split;
    private string _sideTab = "summary";
    private readonly List<FlatButton> _sideTabs = new();
    private FlatButton _bMarksSideTab;

    /// <summary>Every inspector panel, in tab order, and the order Ctrl+1..7 selects
    /// them in. All seven have a tab of their own: hiding three of them behind an
    /// overflow menu meant the panels people reach for most — the entry detail and,
    /// after a merge, the sources — took two clicks and a guess to find.</summary>
    private static readonly string[] SideNames =
        { "summary", "detail", "fields", "patterns", "marks", "sources", "colors" };

    private FilterResult _res;
    private volatile int _gen;
    private volatile int _liveGen;
    private int _progPct = -1;
    private double _t0 = double.NaN, _t1 = double.NaN;
    private int _levelMask = 0x7F;
    private long _srcMask = -1;
    private bool _filterMode = true, _regex, _case, _group = true, _tail, _followEnd;
    private List<HighlightRule> _rules = new();
    private float _fontSize = 11.5f;
    private Settings _cfg = new();

    private TextBox _tRange;
    private FlatButton _bTRangeClear;
    private bool _updatingTimeRange;

    private LogDocument F { get { return _active >= 0 && _active < _docs.Count ? _docs[_active] : null; } }

    private readonly ILogIndexer _indexer;
    private readonly IFilterEngine _filterEngine;
    private readonly IJsonLogReader _jsonReader;

    public MainForm(ILogIndexer indexer, IFilterEngine filterEngine, IJsonLogReader jsonReader)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(filterEngine);
        ArgumentNullException.ThrowIfNull(jsonReader);
        _indexer = indexer;
        _filterEngine = filterEngine;
        _jsonReader = jsonReader;

        Dpi.Init(this);
        Theme.InitFonts();
        Text = AppInfo.Name;
        var ic = AppIcon();
        if (ic != null)
        {
            Icon = ic;
        }
        BackColor = Theme.BgData;
        ForeColor = Theme.Text;
        Font = Theme.UiBody;
        ClientSize = new Size(1440, 920);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;
        MinimumSize = new Size(1100, 560);

        _cfg.Load();
        LoadRules();

        BuildUi();
        ApplySettings();

        _debounce = new System.Windows.Forms.Timer();
        _debounce.Interval = DebounceMs;
        _debounce.Tick += delegate { _debounce.Stop(); StartFilter(); };

        _tailTimer = new System.Windows.Forms.Timer();
        _tailTimer.Interval = TailPollMs;
        _tailTimer.Tick += delegate { TailPoll(); };
    }

    private static void IgnoreNonCritical(Exception ex, string context)
    {
        Debug.WriteLine(context + ": " + ex.Message);
    }

    private void LoadRules()
    {
        var packed = _cfg.Get("rules", "");
        if (packed.Length > 0)
        {
            foreach (var part in packed.Split('\u001f'))
            {
                var f = part.Split('\u001e');
                if (f.Length < 4)
                {
                    continue;
                }
                var r = new HighlightRule();
                r.On = f[0] == "1";
                r.IsRegex = f[1] == "1";
                int argb;
                r.C = int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out argb)
                    ? Color.FromArgb(argb) : Palette.Pick(0);
                r.Pattern = f[3];
                _rules.Add(r);
            }
        }
        if (_rules.Count == 0)
        {
            _rules.Add(NewRule("exception|traceback|stack trace", true, Theme.SevColor[5]));
            _rules.Add(NewRule("timeout|timed out|refused|unreachable", true, Theme.SevColor[4]));
            _rules.Add(NewRule("\\b\\d{1,3}(?:\\.\\d{1,3}){3}\\b", true, Theme.SevColor[2]));
        }
    }

    private static Icon AppIcon() => AppImages.Load();

    private static HighlightRule NewRule(string p, bool rx, Color c)
    {
        var r = new HighlightRule();
        r.Pattern = p;
        r.IsRegex = rx;
        r.C = c;
        r.On = true;
        return r;
    }

}
