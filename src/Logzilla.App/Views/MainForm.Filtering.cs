using System;
using System.Threading;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Query;

namespace Logzilla.App.Views;

// Turning the toolbar state into a QuerySpec and running it off the UI thread.

internal sealed partial class MainForm

{
    private void StartFilter()
    {
        if (!_updatingTimeRange)
        {
            ApplyTimeRangeText();
        }

        var f = F;
        if (f == null)
        {
            _res = null; _view.Attach(null, null);
            _tl.Res = null; _tl.Invalidate(); _mini.File = null; _mini.Res = null; _mini.Invalidate();
            FillSide(); UpdateStatus();
            return;
        }

        var s = QuerySpec.Build(_q.Text, _regex, _case, _levelMask, _srcMask,
                            _t0, _t1, (int)_ctx.Value, _group, _filterMode);
        // The filters themselves are never changed in place (FieldFilterList replaces
        // them), so the scan thread can share them with the UI.
        s.Fields.AddRange(_fieldFilters);
        // A pattern that does not compile searches for nothing, so every entry shows.
        // Say so: a half-typed "(" otherwise looked like a search that matched it all.
        var badRegex = _regex && s.Rx == null && _q.Text.Trim().Length > 0;
        _view.Query = s;
        UpdateResetBadge();

        var gen = Interlocked.Increment(ref _liveGen);
        var target = f;
        _st3.Text = "Scanning ...";
        var th = new Thread(delegate ()
        {
            FilterResult r;
            try
            {
                r = _filterEngine.Run(target, s, delegate { return _liveGen != gen; },
                    delegate (int done, int total)
                    {
                        var pct = total > 0 ? (int)((long)done * 100 / total) : 100;
                        if (pct != _progPct)
                        {
                            _progPct = pct;
                            try { BeginInvoke((MethodInvoker)delegate { _progHost.Invalidate(); }); }
                            catch (Exception ex) { IgnoreNonCritical(ex, "Refresh scan progress"); }
                        }
                    });
            }
            catch (Exception ex)
            {
                // Clear the "Scanning ..." state rather than leave it up for good, and
                // say why nothing changed.
                var why = ex is System.Text.RegularExpressions.RegexMatchTimeoutException
                    ? "Search stopped: the regular expression took too long on one entry. Try a simpler pattern."
                    : "Search failed: " + ex.Message;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (_liveGen != gen)
                        {
                            return;
                        }
                        _progPct = -1; _progHost.Invalidate();
                        _st3.Text = why;
                    });
                }
                catch (Exception inner) { IgnoreNonCritical(inner, "Report filter failure"); }
                return;
            }
            if (r == null)
            {
                return;
            }
            var done2 = r;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (_liveGen != gen)
                    {
                        return;
                    }
                    _progPct = -1; _progHost.Invalidate();
                    _st3.Text = badRegex ? "Not a valid regular expression; nothing is searched for" : "";
                    _res = done2;
                    _view.Attach(target, done2);
                    _tl.File = target; _tl.Res = done2; _tl.T0 = _t0; _tl.T1 = _t1; _tl.View = _view; _tl.Invalidate();
                    _mini.File = target; _mini.Res = done2; _mini.Invalidate();
                    FillSide();
                    RefreshMarkBadge();
                    SyncLevels();
                    UpdateStatus();
                    if (_followEnd)
                    {
                        _followEnd = false;
                        if (done2.View.Count > 0)
                        {
                            _view.ScrollTo(done2.View.Count - 1, false);
                        }
                    }
                });
            }
            catch (Exception ex) { IgnoreNonCritical(ex, "Apply filter result"); }
        });
        th.IsBackground = true;
        th.Start();
    }

    private void ApplyTimeRangeText()
    {
        var txt = _tRange?.Text.Trim() ?? "";
        if (txt.Length == 0)
        {
            _t0 = _t1 = double.NaN;
            if (_tl != null)
            {
                _tl.T0 = _tl.T1 = double.NaN; _tl.Invalidate();
            }
            if (_tRange != null)
            {
                _tRange.ForeColor = Theme.Text;
            }
            return;
        }
        if (TimeRangeParser.TryParseRange(txt, GetBaseDay(), out var pt0, out var pt1))
        {
            // Typed in the clock the times are shown in; stored the way the lines are.
            _t0 = StoredTime(pt0); _t1 = StoredTime(pt1);
            if (_tRange != null)
            {
                _tRange.ForeColor = Theme.Text;
            }
        }
        else
        {
            if (_tRange != null)
            {
                _tRange.ForeColor = Theme.SevColor[5];
            }
        }
    }

    private long GetBaseDay()
    {
        var lo = double.NaN;
        if (_res != null && !double.IsNaN(_res.Lo))
        {
            lo = _res.Lo;
        }
        else
        {
            var f = F;
            if (f != null)
            {
                for (var i = 0; i < f.N && double.IsNaN(lo); i++)
                {
                    if (!double.IsNaN(f.Ts[i]))
                    {
                        lo = f.Ts[i];
                    }
                }
            }
        }
        if (double.IsNaN(lo))
        {
            return 0;
        }
        const long dayMs = 86400000L;
        return (long)Math.Floor(ShownTime(lo) / dayMs) * dayMs;
    }

    private void UpdateStatus()
    {
        var f = F;
        if (f == null)
        {
            _st1.Text = "No file open  —  Ctrl+O to open, or drop files here";
            _stCount.Text = ""; _stHint.Text = ""; _hits.Text = "";
            _statusDot.BackColor = Theme.TextDim;
            Text = AppInfo.Name;
            return;
        }
        if (f.Merged)
        {
            _st1.Text = f.Srcs.Count + " files merged  ·  " + DisplayFormatter.Bytes(f.Size);
        }
        else
        {
            var tsLabel = "no timestamps";
            if (f.Srcs[0].Kind != TsKind.None)
            {
                tsLabel = f.Srcs[0].Kind.ToString().ToUpperInvariant();
            }
            _st1.Text = f.Name + "  ·  " + DisplayFormatter.Bytes(f.Size) + "  ·  " + tsLabel;
        }

        if (_res == null)
        {
            _stCount.Text = ""; _stHint.Text = "";
        }
        else
        {
            var hiddenSeverities = 0;
            for (var l = 1; l <= 6; l++)
            {
                if (((_levelMask >> l) & 1) == 0)
                {
                    hiddenSeverities++;
                }
            }
            _stCount.Text = DisplayFormatter.Number(_res.View.Count) + " of " + DisplayFormatter.Number(f.N) + " shown";
            _stHint.Text = hiddenSeverities > 0
                ? "  ·  " + hiddenSeverities + " " + (hiddenSeverities == 1 ? "severity" : "severities") + " hidden  ·  "
                : "  ·  ";
            if (_fieldFilters.Count > 0)
            {
                _stHint.Text += _fieldFilters.Count + (_fieldFilters.Count == 1 ? " field filter  ·  " : " field filters  ·  ");
            }
            var picked = _view.SelectionCount;
            if (picked > 1)
            {
                _stHint.Text += DisplayFormatter.Number(picked) + " selected  ·  ";
            }
            else if (_view.Selected >= 0)
            {
                _stHint.Text += "line " + DisplayFormatter.Number(_view.Selected + 1) + "  ·  ";
            }
            _statusDot.BackColor = hiddenSeverities > 0 || _q.Text.Length > 0 || _fieldFilters.Count > 0 ? Theme.Accent : Theme.TextDim;
        }

        if (_res == null || _q.Text.Length == 0)
        {
            _hits.Text = "";
        }
        else if (_regex && _view.Query is { Rx: null } && _q.Text.Trim().Length > 0)
        {
            _hits.Text = "invalid regex";
        }
        else
        {
            _hits.Text = _res.Matches.Count == 0 ? "no hits" : DisplayFormatter.Number(_res.Matches.Count) + " hits";
        }
        _hits.ForeColor = _res != null && _q.Text.Length > 0 && _res.Matches.Count == 0 ? Theme.SevColor[5] : Theme.Text3;
        if (_miCopy != null)
        {
            _miCopy.Text = CopyLabel();
        }
        Text = f.Name + " — " + AppInfo.Name;
    }

}
