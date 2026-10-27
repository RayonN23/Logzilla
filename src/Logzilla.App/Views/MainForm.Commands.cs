using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Dialogs;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Query;

namespace Logzilla.App.Views;

// User-invoked actions: navigation, marks, export, setup files, help.

internal sealed partial class MainForm

{
    private const int ExportReadBuffer = 1 << 20;
    private const int MarksReadBuffer = 1 << 18;

    private static void ShowInExplorer(string path)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            System.Diagnostics.Process.Start(explorer, "/select,\"" + path + "\"");
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Show in Explorer"); }
    }

    private void Info(string msg) { MessageBox.Show(this, msg, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); }

    private void AddTerm(string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return;
        }
        var tok = term.Contains(' ') ? "\"" + term + "\"" : term;
        if (_q.Text.Contains(tok))
        {
            return;
        }
        _regex = false; SetToggleStates();
        _q.Text = (_q.Text.Length > 0 ? _q.Text + " " : "") + tok;
        _debounce.Stop();
        StartFilter();
    }

    private void ExcludeTerm(string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return;
        }
        var tok = "-" + (term.Contains(' ') ? "\"" + term + "\"" : term);
        if (_q.Text.Contains(tok))
        {
            return;
        }
        _regex = false; SetToggleStates();
        _q.Text = (_q.Text.Length > 0 ? _q.Text + " " : "") + tok;
        _debounce.Stop();
        StartFilter();
    }

    private void SeekTime(double t)
    {
        if (_res == null)
        {
            return;
        }
        var f = F; if (f == null)
{
    return;
}
        for (var k = 0; k < _res.View.Count; k++)
        {
            var v = f.Ts[_res.View[k]];
            if (!double.IsNaN(v) && v >= t) { _view.Select(_res.View[k], true); return; }
        }
    }

    private void DoReset()
    {
        _q.Text = "";
        _levelMask = 0x7F;
        _srcMask = -1;
        _t0 = _t1 = double.NaN;
        _tl.T0 = _tl.T1 = double.NaN;
        _ctx.Value = 0;
        _regex = false;
        _fieldFilters.Clear();
        SyncFieldBar();
        _updatingTimeRange = true;
        _tRange.Text = "";
        _tRange.ForeColor = Theme.Text;
        _bTRangeClear.ForeColor = Theme.TextDim;
        _updatingTimeRange = false;
        UpdateTimeTrigger();
        SetToggleStates();
        SyncLevels();
        StartFilter();
    }

    private void Zoom(int dir)
    {
        if (dir == 0)
        {
            _fontSize = 11.5f;
        }
        else
        {
            _fontSize = Math.Max(6.5f, Math.Min(18f, _fontSize + dir * 0.5f));
        }
        ApplyFontSize();
        _view.Invalidate();
    }

    private void ViewWheel(object sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            Zoom(Math.Sign(e.Delta));
        }
    }

    private void ViewMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _view.Selected < 0 || F == null)
        {
            return;
        }
        // The word or field value under the pointer as the grid drew it — not a column
        // of the raw line, which for a JSON record is not what is on screen at all.
        var word = _view.PickAt(e.Location, out var fieldKey);

        var cm = new ContextMenuStrip();
        cm.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        cm.BackColor = Theme.BgChrome; cm.ForeColor = Theme.Text; cm.Font = Theme.UiBody;
        if (word.Length > 0)
        {
            var what = fieldKey == null ? "\"" + Trim(word) + "\"" : FieldNames.Display(fieldKey) + "  " + Trim(word);
            var picked = word;
            var label = fieldKey == null ? "text" : FieldNames.Display(fieldKey);
            cm.Items.Add(Mi("Copy " + what, Keys.None, delegate { CopyText(picked, label); }));
        }
        cm.Items.Add(Mi(CopyLabel(), Keys.None, delegate { CopySelected(); }));
        cm.Items.Add(Mi("Copy all shown entries", Keys.None, delegate { CopyShown(); }));
        if (word.Length > 0)
        {
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Mi("Find \"" + Trim(word) + "\"", Keys.None, delegate { AddTerm(word); }));
            cm.Items.Add(Mi("Hide entries with \"" + Trim(word) + "\"", Keys.None, delegate { ExcludeTerm(word); }));
            cm.Items.Add(Mi("Highlight \"" + Trim(word) + "\"", Keys.None, delegate
            {
                var r = new HighlightRule(); r.Pattern = word; r.IsRegex = false;
                r.C = Palette.Pick(_rules.Count + 3);
                _rules.Add(r); _view.Invalidate(); FillSide();
            }));
        }
        cm.Items.Add(new ToolStripSeparator());
        AddFollowThread(cm, _view.Selected);
        AddFieldMenus(cm, _view.Selected);
        cm.Items.Add(new ToolStripSeparator());
        var entry = _view.Selected;
        cm.Items.Add(Mi("Ask AI about this entry", Keys.None, delegate { AskAboutEntry(entry); }));
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add(Mi(MarkLabel(), Keys.None, delegate { ToggleMark(); }));
        cm.Items.Add(Mi("Jump to next error  (E)", Keys.None, delegate { JumpSeverity(1, 5); }));
        cm.Items.Add(new ToolStripSeparator());
        var selSev = F.Sev[_view.Selected];
        if (selSev > 0)
        {
            var sevName = Logzilla.Core.Common.Levels.Name[selSev];
            cm.Items.Add(Mi("Show only " + sevName + " entries", Keys.None, delegate
            {
                _levelMask = 1 << selSev; SyncLevels(); StartFilter();
            }));
        }
        cm.Items.Add(Mi("Clear filters and show this in context", Keys.None, delegate
        {
            var keep = _view.Selected;
            _q.Text = ""; _levelMask = 0x7F; _srcMask = -1; _t0 = _t1 = double.NaN;
            _fieldFilters.Clear(); SyncFieldBar();
            _tl.T0 = _tl.T1 = double.NaN; SyncLevels(); SetToggleStates();
            StartFilter();
            BeginInvoke((MethodInvoker)delegate { _view.Select(keep, true); });
        }));
        if (F.Merged)
        {
            int si = F.Src[_view.Selected];
            cm.Items.Add(Mi("Show only " + F.Srcs[si].Label, Keys.None,
                delegate { _srcMask = 1L << si; StartFilter(); }));
        }
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add(Mi("Show file in Explorer", Keys.None,
            delegate { ShowInExplorer(F.Srcs[F.Src[_view.Selected]].Path); }));
        foreach (ToolStripItem it in cm.Items) { it.BackColor = Theme.BgChrome; it.ForeColor = Theme.Text; }
        cm.Show(_view, e.Location);
    }

    private static string Trim(string s) => s.Length > 24 ? string.Concat(s.AsSpan(0, 24), "...") : s;

    /// <summary>Puts one value on the clipboard and says what was copied.</summary>
    private void CopyText(string text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
            _st3.Text = "Copied " + what + ": " + Trim(text);
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Copy " + what); }
    }

    /// <summary>
    /// "Follow thread 7": shows only the entries the same thread wrote, in order — what
    /// that thread did next, which interleaved threads otherwise bury. Offered when the
    /// entry names its thread (ThreadId, thread, tid …).
    /// </summary>
    private void AddFollowThread(ContextMenuStrip cm, int line)
    {
        foreach (var (key, value) in _view.FieldsOf(line, 24))
        {
            if (!FieldNames.IsThread(key))
            {
                continue;
            }
            var k = key;
            var v = value;
            cm.Items.Add(Mi("Follow thread " + Trim(v), Keys.None, delegate { AddFieldFilter(k, FieldOp.Is, v); }));
            return;
        }
    }

    private void DoExport()
    {
        var f = F;
        if (f == null || _res == null || _res.View.Count == 0) { Info("Nothing to export."); return; }
        using (var d = new SaveFileDialog())
        {
            d.Filter = "Log file|*.log|Text file|*.txt|All files|*.*";
            d.FileName = Path.GetFileNameWithoutExtension(f.Name) + "-view.log";
            if (d.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            try
            {
                using (var outp = new FileStream(d.FileName, FileMode.Create, FileAccess.Write))
                using (var feed = _indexer.OpenReader(f, ExportReadBuffer))
                {
                    byte[] nl = { 13, 10 };
                    var tag = f.Merged;
                    for (var k = 0; k < _res.View.Count; k++)
                    {
                        var i = _res.View[k];
                        if (tag)
                        {
                            var pre = Encoding.UTF8.GetBytes("[" + f.Srcs[f.Src[i]].Label + "] ");
                            outp.Write(pre, 0, pre.Length);
                        }
                        byte[] b; var o = feed.Locate(i, out b);
                        var len = Math.Min(f.Len[i], b.Length - o);
                        if (len > 0)
                        {
                            outp.Write(b, o, len);
                        }
                        outp.Write(nl, 0, 2);
                    }
                }
                _st3.Text = "Exported " + DisplayFormatter.Number(_res.View.Count) + " entries";
            }
            catch (Exception ex) { Info("Export failed:\r\n" + ex.Message); }
        }
    }

    /// <summary>
    /// Writes the shown entries of a JSON log as a plain-text log: one
    /// "time LEVEL message" line per record, in the clock the time column reads in,
    /// with an exception's stack trace on the lines under it. Lines from a plain-text
    /// source in a merged view go over as they are. Fields other than the time,
    /// level, message and exception do not survive — that is the point of the format.
    /// </summary>
    private void DoExportPlain()
    {
        var f = F;
        if (f == null || _res == null || _res.View.Count == 0) { Info("Nothing to export."); return; }
        if (!f.Srcs.Exists(s => s.IsJson)) { Info("This log is already plain text; use Export shown entries instead."); return; }
        using (var d = new SaveFileDialog())
        {
            d.Filter = "Log file|*.log|Text file|*.txt|All files|*.*";
            d.FileName = Path.GetFileNameWithoutExtension(f.Name) + "-plain.log";
            if (d.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            var old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                using (var outp = new StreamWriter(d.FileName, false, new UTF8Encoding(false), 1 << 16))
                using (var feed = _indexer.OpenReader(f, ExportReadBuffer))
                {
                    outp.NewLine = "\r\n";
                    var tag = f.Merged;
                    for (var k = 0; k < _res.View.Count; k++)
                    {
                        var i = _res.View[k];
                        var text = feed.Text(i);
                        if (f.SrcOf(i).IsJson)
                        {
                            text = _jsonReader.RenderFull(text);
                            if (f.Flag[i] == 0)
                            {
                                text = EntryClip.Entry(ShowLineTime(f, i), Levels.Name[f.Sev[i]], text);
                            }
                            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                        }
                        if (tag)
                        {
                            outp.Write('[');
                            outp.Write(f.Srcs[f.Src[i]].Label);
                            outp.Write("] ");
                        }
                        outp.WriteLine(text);
                    }
                }
                _st3.Text = "Exported " + DisplayFormatter.Number(_res.View.Count) + " entries as plain text";
            }
            catch (Exception ex) { Info("Export failed:\r\n" + ex.Message); }
            finally { Cursor.Current = old; }
        }
    }

    private void SaveSetup()
    {
        using (var d = new SaveFileDialog())
        {
            d.Filter = "Logzilla setup|*.cnx|All files|*.*";
            d.FileName = "logzilla-setup.cnx";
            if (d.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("query=" + _q.Text);
            sb.AppendLine("mask=" + _levelMask);
            sb.AppendLine("regex=" + (_regex ? 1 : 0));
            sb.AppendLine("case=" + (_case ? 1 : 0));
            foreach (var r in _rules)

            {
                sb.AppendLine("rule=" + (r.On ? 1 : 0) + "|" + (r.IsRegex ? 1 : 0) + "|" + r.C.ToArgb() + "|" + r.Pattern);
            }
            foreach (var f in _fieldFilters)
            {
                var line = FieldFilterList.Serialize(f);
                if (line != null)
                {
                    sb.AppendLine("field=" + line);
                }
            }
            try { System.IO.File.WriteAllText(d.FileName, sb.ToString()); _st3.Text = "Setup saved"; }
            catch (Exception ex) { Info(ex.Message); }
        }
    }

    private void LoadSetup()
    {
        using (var d = new OpenFileDialog())
        {
            d.Filter = "Logzilla setup|*.cnx|All files|*.*";
            if (d.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            try
            {
                _rules.Clear();
                _fieldFilters.Clear();
                foreach (var raw in System.IO.File.ReadAllLines(d.FileName))
                {
                    var eq = raw.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string k = raw.Substring(0, eq), v = raw.Substring(eq + 1);
                    if (k == "query")
                    {
                        _q.Text = v;
                    }
                    // A line that does not parse is skipped rather than allowed to stop the
                    // load half way, with the highlights and field filters already cleared.
                    else if (k == "mask")
                    {
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
                        {
                            _levelMask = mask & 0x7F;
                        }
                    }
                    else if (k == "regex")
{
    _regex = v == "1";
}
                    else if (k == "case")
{
    _case = v == "1";
}
                    else if (k == "rule")
                    {
                        var parts = v.Split(Separator, 4);
                        if (parts.Length < 4 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var argb))
                        {
                            continue;
                        }
                        var r = new HighlightRule();
                        r.On = parts[0] == "1";
                        r.IsRegex = parts[1] == "1";
                        r.C = Color.FromArgb(argb);
                        r.Pattern = parts[3];
                        _rules.Add(r);
                    }
                    else if (k == "field")
                    {
                        var f = FieldFilterList.Parse(v);
                        if (f != null)
                        {
                            FieldFilterList.Add(_fieldFilters, f.Key, f.Op, f.Values);
                        }
                    }
                }
                SyncFieldBar();
                _view.Rules = _rules;
                SetToggleStates(); SyncLevels(); FillSide(); StartFilter();
                _st3.Text = "Setup loaded";
            }
            catch (Exception ex) { Info(ex.Message); }
        }
    }

    private void DoRegister()
    {
        var exe = Application.ExecutablePath;
        try
        {
            FileAssociations.Register(exe);
            _miAssoc.Checked = true;
            using (var f = new Form())
            {
                f.Text = AppInfo.Name + " is now registered";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(560, 250);
                f.BackColor = Theme.BgChrome; f.ForeColor = Theme.Text;
                f.MinimizeBox = false; f.MaximizeBox = false;
                f.Icon = Icon;

                var l = new Label();
                l.Font = Theme.UiBody;
                l.ForeColor = Theme.Text;
                l.SetBounds(16, 14, 528, 170);
                l.Text =
                    "Logzilla now appears in the \"Open with\" list for .log, .trace, .out,\r\n" +
                    ".err and .syslog files.\r\n\r\n" +
                    "Windows will not let an application make itself the default, so the\r\n" +
                    "last step is yours. Either:\r\n\r\n" +
                    "  1.  Right-click any .log file, choose Open with > Choose another app,\r\n" +
                    "      pick Logzilla and tick \"Always use this app\", or\r\n\r\n" +
                    "  2.  Open Settings > Apps > Default apps, search for Logzilla and\r\n" +
                    "      set the file types there.";
                f.Controls.Add(l);

                var b1 = new Button();
                b1.Text = "Open Default apps"; b1.SetBounds(16, 200, 160, 28);
                b1.FlatStyle = FlatStyle.Flat; b1.BackColor = Theme.BgChrome; b1.ForeColor = Theme.Text;
                b1.Click += delegate { FileAssociations.OpenDefaultAppsSettings(); };
                var b2 = new Button();
                b2.Text = "Close"; b2.SetBounds(452, 200, 90, 28);
                b2.FlatStyle = FlatStyle.Flat; b2.BackColor = Theme.BgChrome; b2.ForeColor = Theme.Text;
                b2.DialogResult = DialogResult.OK;
                f.Controls.Add(b1); f.Controls.Add(b2);
                f.AcceptButton = b2;
                f.ShowDialog(this);
            }
        }
        catch (Exception ex)
        {
            Info("Could not register the file types.\r\n" + ex.Message);
        }
    }

    private void DoUnregister()
    {
        try
        {
            FileAssociations.Unregister(Application.ExecutablePath);
            _miAssoc.Checked = false;
            Info("File associations removed.\r\n\r\n" +
                 "Windows may still list Logzilla until you sign out and back in.");
        }
        catch (Exception ex) { Info(ex.Message); }
    }

    /// <summary>
    /// M. Marks or unmarks every selected entry. With a run selected the whole run
    /// moves together, and it moves the same way: if any of it is still unmarked the
    /// key marks the rest, so a second press is what clears it.
    /// </summary>
    private void ToggleMark()
    {
        var f = F;
        if (f == null || _view.Selected < 0)
        {
            return;
        }
        var lines = _view.SelectedLines();
        if (lines.Count == 0)
        {
            return;
        }
        var marking = lines.Exists(i => !f.Marks.Contains(i));
        foreach (var i in lines)
        {
            if (marking)
            {
                f.Marks.Add(i);
            }
            else
            {
                f.Marks.Remove(i);
            }
        }
        _view.Invalidate(); _mini.Invalidate();
        RefreshMarkBadge();
        if (_sideTab == "marks")
        {
            FillSide();
        }
    }

    private void ClearFind()
    {
        _debounce.Stop();
        if (_q.Text.Length > 0) { _q.Text = ""; StartFilter(); }
        _st3.Text = "";
        _q.Focus();
    }
[SuppressMessage("Major Code Smell", "S3776", Justification = "Binary search navigation has intentional branching.")]

    // Computed from where the cursor is now rather than from a saved index, so it
    // keeps working after the user clicks a row, and the first press always moves.
    private     void JumpMatch(int dir)
    {
        if (_res == null || _res.Matches.Count == 0)
        {
            _st3.Text = _q.Text.Length > 0 ? "no matches" : "";
            return;
        }
        var m = _res.Matches;
        var anchor = _view.Selected;
        int idx;
        var wrapped = false;

        if (anchor < 0)
        {
            idx = dir > 0 ? 0 : m.Count - 1;
        }
        else if (dir > 0)
        {
            int lo = 0, hi = m.Count - 1, found = -1;
            while (lo <= hi)                      // first match strictly after the cursor
            {
                var mid = (lo + hi) / 2;
                if (m[mid] > anchor) { found = mid; hi = mid - 1; } else
{
    lo = mid + 1;
}
            }
            if (found >= 0)
            {
                idx = found;
            } else { idx = 0; wrapped = true; }
        }
        else
        {
            int lo = 0, hi = m.Count - 1, found = -1;
            while (lo <= hi)                      // last match strictly before the cursor
            {
                var mid = (lo + hi) / 2;
                if (m[mid] < anchor) { found = mid; lo = mid + 1; } else
{
    hi = mid - 1;
}
            }
            if (found >= 0)
            {
                idx = found;
            } else { idx = m.Count - 1; wrapped = true; }
        }

        _view.Select(m[idx], true);
        _hits.Text = DisplayFormatter.Number(idx + 1) + " of " + DisplayFormatter.Number(m.Count);
        _st3.Text = wrapped ? "wrapped to " + (idx == 0 ? "start" : "end") : "";
    }

    internal static readonly char[] Separator = new[] { '|' };

    private void JumpMark(int dir)
    {
        var f = F;
        if (f == null || f.Marks.Count == 0)
        {
            _st3.Text = "no bookmarks  —  press M on an entry to add one";
            return;
        }
        if (_res == null)
        {
            return;
        }

        var ms = new List<int>(f.Marks);
        ms.Sort();

        var visible = new List<int>();
        foreach (var m in ms)
        {
            if (_view.ViewPosOf(m) >= 0)
            {
                visible.Add(m);
            }
        }
        if (visible.Count == 0)
        {
            _st3.Text = "bookmarks not visible  —  Ctrl+R to reset filters";
            return;
        }

        int anchor = _view.Selected;
        bool wrapped = false;
        int idx;
        if (anchor < 0)
        {
            idx = dir > 0 ? 0 : visible.Count - 1;
        }
        else if (dir > 0)
        {
            idx = visible.FindIndex(m => m > anchor);
            if (idx < 0)
            {
                idx = 0;
                wrapped = true;
            }
        }
        else
        {
            idx = visible.FindLastIndex(m => m < anchor);
            if (idx < 0)
            {
                idx = visible.Count - 1;
                wrapped = true;
            }
        }

        _view.Select(visible[idx], true);
        _st3.Text = "bookmark " + (idx + 1) + " of " + visible.Count + (wrapped ? "  (wrapped)" : "");
    }

    private void JumpSeverity(int dir, int min)
    {
        var f = F;
        if (f == null || _res == null)
        {
            return;
        }
        var p = _view.ViewPosOf(_view.Selected >= 0 ? _view.Selected : 0);
        for (var k = p + dir; k >= 0 && k < _res.View.Count; k += dir)

        {
            if (f.Sev[_res.View[k]] >= min) { _view.Select(_res.View[k], true); return; }
        }
        _st3.Text = dir > 0 ? "no more errors below" : "no more errors above";
    }

    private void GoToLine()
    {
        var f = F; if (f == null)
{
    return;
}
        var s = Prompt.Ask(this, "Go to line number");
        int n;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0 && n <= f.N)

        {
            _view.Select(n - 1, true);
        }
    }

    /// <summary>
    /// Ctrl+C. Copies every selected entry, which is one of them unless the user
    /// shift-clicked or shift-arrowed a run.
    ///
    /// The entry as its row reads: the shown time and level, then the readable
    /// message (JSON rendered to its summary line, or the plain-text line after its
    /// own prefix) — not the raw record, which for a JSON log is an unreadable blob
    /// once pasted elsewhere.
    /// </summary>
    private void CopySelected()
    {
        // The menu owns Ctrl+C, so the form sees it first. A box being typed in keeps
        // its own, and so does a read-only one (the Entry panel) with text picked out.
        if (FocusedTextBox() is { } box && (!box.ReadOnly || box.SelectionLength > 0))
        {
            box.Copy();
            return;
        }
        CopyEntries(_view.SelectedLines(), raw: false, "copied");
    }

    /// <summary>The raw line already is the JSON record; "as JSON" means untouched,
    /// not re-rendered through the summary line.</summary>
    private void CopySelectedAsJson()
    {
        CopyEntries(_view.SelectedLines(), raw: true, "copied as JSON");
    }

    /// <summary>Entries above this go to the clipboard only after the user confirms;
    /// the whole point of a log viewer is files big enough for that to matter.</summary>
    private const int CopyCap = 200000;

    /// <summary>What the Copy command calls itself right now, so the menu and the
    /// context menu say how many entries Ctrl+C is about to take.</summary>
    private string CopyLabel()
    {
        var n = _view.SelectionCount;
        return n > 1 ? "Copy " + DisplayFormatter.Number(n) + " entries" : "Copy entry";
    }

    /// <summary>Likewise for M, which follows the same "mark the rest, or clear
    /// them all" rule as <see cref="ToggleMark"/>.</summary>
    private string MarkLabel()
    {
        var f = F;
        var lines = _view.SelectedLines();
        if (f == null || lines.Count == 0)
        {
            return "Bookmark this entry  (M)";
        }
        var marking = lines.Exists(i => !f.Marks.Contains(i));
        if (lines.Count == 1)
        {
            return marking ? "Bookmark this entry  (M)" : "Remove bookmark  (M)";
        }
        return (marking ? "Bookmark " : "Remove the bookmarks from ") + DisplayFormatter.Number(lines.Count) + " entries  (M)";
    }

    /// <summary>Reads the given entries and puts them on the clipboard; see
    /// <see cref="EntryClip"/> for how one entry differs from several.</summary>
    private void CopyEntries(List<int> lines, bool raw, string what)
    {
        var f = F;
        if (f == null || lines == null || lines.Count == 0)
        {
            return;
        }
        if (lines.Count > CopyCap &&
            MessageBox.Show(this, DisplayFormatter.Number(lines.Count) + " entries are selected. Copy the first " +
                DisplayFormatter.Number(CopyCap) + "?", AppInfo.Name, MessageBoxButtons.OKCancel) != DialogResult.OK)
        {
            return;
        }
        var take = Math.Min(CopyCap, lines.Count);
        var texts = new List<string>(take);
        var labels = f.Merged ? new List<string>(take) : null;
        for (var k = 0; k < take; k++)
        {
            var i = lines[k];
            texts.Add(raw ? _view.TextOf(i) : _view.CopyTextOf(i));
            labels?.Add(f.Srcs[f.Src[i]].Label);
        }
        try
        {
            Clipboard.SetText(EntryClip.Compose(texts, labels));
            _st3.Text = take == 1 ? "Entry " + what : DisplayFormatter.Number(take) + " entries " + what;
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Copy selected entries"); }
    }

    private void CopyShown()
    {
        var f = F;
        if (f == null || _res == null || _res.View.Count == 0)
        {
            return;
        }
        var cap = 200000;
        if (_res.View.Count > cap &&
            MessageBox.Show(this, DisplayFormatter.Number(_res.View.Count) + " entries are shown. Copy the first " + DisplayFormatter.Number(cap) + "?",
                AppInfo.Name, MessageBoxButtons.OKCancel) != DialogResult.OK)
        {
            return;
        }
        var sb = new StringBuilder();
        using (var feed = _indexer.OpenReader(f, ExportReadBuffer))

        {
            for (var k = 0; k < Math.Min(cap, _res.View.Count); k++)
            {
                var i = _res.View[k];
                if (f.Merged)
                {
                    sb.Append('[').Append(f.Srcs[f.Src[i]].Label).Append("] ");
                }
                sb.AppendLine(feed.Text(i));
            }
        }
        try { Clipboard.SetText(sb.ToString()); _st3.Text = "Copied " + DisplayFormatter.Number(Math.Min(cap, _res.View.Count)) + " entries"; }
        catch (Exception ex) { IgnoreNonCritical(ex, "Copy shown entries"); }
    }

    private void CopyMarks()
    {
        var f = F;
        if (f == null || f.Marks.Count == 0)
        {
            return;
        }
        var ms = new List<int>(f.Marks);
        ms.Sort();
        var sb = new StringBuilder();
        using (var feed = _indexer.OpenReader(f, MarksReadBuffer))

        {
            foreach (var i in ms)
            {
                sb.AppendLine(feed.Text(i));
            }
        }
        try { Clipboard.SetText(sb.ToString()); _st3.Text = "Copied " + ms.Count + " bookmarked entries"; }
        catch (Exception ex) { IgnoreNonCritical(ex, "Copy marked entries"); }
    }

    private void ShowHelp()
    {
        var t =
            "SHORTCUTS\r\n" +
            "  Ctrl+O           Open files\r\n" +
            "  Ctrl+Shift+O     Open every log in a folder\r\n" +
            "  Ctrl+M           Merge open files into one timeline\r\n" +
            "  Ctrl+F  or  /    Focus the Find box\r\n" +
            "  Enter / Shift+Enter, F3    Next / previous match\r\n" +
            "  E / Shift+E      Next / previous error or worse\r\n" +
            "  M                Bookmark the selected entries, or remove the bookmark\r\n" +
            "  B / Shift+B      Next / previous bookmarked entry\r\n" +
            "  Ctrl+G           Go to line number\r\n" +
            "  Ctrl+R           Reset filters\r\n" +
            "  Ctrl+A           Select every entry shown\r\n" +
            "  Ctrl+C           Copy the selected entries\r\n" +
            "  Ctrl+Shift+C     Copy every entry shown\r\n" +
            "  Ctrl+E           Export the entries shown\r\n" +
            "  Ctrl+W           Close the current tab\r\n" +
            "  W                Show only warnings and above\r\n" +
            "  F5               Re-read the file from disk\r\n" +
            "  F6               Follow the file as it grows\r\n" +
            "  Ctrl+plus / minus / 0      Text size\r\n" +
            "  Arrows, PgUp/PgDn, Home/End   Move through entries\r\n" +
            "  Shift with any of those   Select a run of entries\r\n" +
            "  Shift+click, or drag down the rows   The same with the mouse\r\n\r\n" +
            "FIND SYNTAX\r\n" +
            "  timeout retry          Entries containing both words\r\n" +
            "  \"connection reset\"     Exact phrase\r\n" +
            "  -heartbeat             Hide entries containing this\r\n" +
            "  .* button              Read the whole query as a regular expression\r\n\r\n" +
            "FIELD FILTERS\r\n" +
            "  Ctrl+Shift+F, or + Field filter   Filter on a field: Feature,\r\n" +
            "                   tenant ... Pick the field, then is / contains / is not, then\r\n" +
            "                   a value. The lists show what this log carries, as you type.\r\n" +
            "  Tick several values    Entries with any of them (Feature is A or B)\r\n" +
            "  Several filters        Entries matching all of them\r\n" +
            "  Click a chip to edit it, x to remove it, right-click to flip is / is not.\r\n" +
            "  Right-click an entry > Show only where... to follow its Feature.\r\n" +
            "  Works on JSON keys and on key=value pairs in plain-text logs,\r\n" +
            "  stack traces included.\r\n\r\n" +
            "DRILL DOWN\r\n" +
            "  Right-click an entry for find, hide, highlight and bookmark.\r\n" +
            "  Double-click a word to add it to the Find box.\r\n" +
            "  Click a value under Fields to filter on it (Ctrl+click hides it instead),\r\n" +
            "  or Ctrl+click a shape under Patterns.\r\n" +
            "  Drag across the timeline, or type in the TIME box (e.g. 20:00 - 21:00,\r\n" +
            "  8pm - 9pm, 8:30am - 5:30pm, 2026-01-15 08:00 - 09:30) to set a range.\r\n" +
            "  Right-click a severity button to isolate that level.\r\n\r\n" +
            "AI ASSISTANT\r\n" +
            "  Ctrl+8, or View > AI Chat, opens the AI Chat tab. Turn it on and\r\n" +
            "  connect GitHub Copilot, Claude or OpenAI in Tools > AI settings. It is off\r\n" +
            "  until you turn it on, and nothing is sent until you ask.\r\n" +
            "  Answers cite lines like #1234; click one to jump to it. Apply turns a\r\n" +
            "  search the assistant ran into your own filters.\r\n" +
            "  Right-click an entry > Ask AI about this entry.\r\n" +
            "  Personal data is hidden before sending. ••• > What was sent shows\r\n" +
            "  exactly what left this computer; ••• > Usage shows the tokens used.\r\n\r\n" +
            "TIME ZONES\r\n" +
            "  The Log time / Local time / UTC chip (View > Show times) picks the\r\n" +
            "  clock times are shown in. Log time is exactly what the file wrote.\r\n" +
            "  Stamps ending in Z or an offset (+02:00, or +0000 in web server logs)\r\n" +
            "  convert on their own. For stamps with no zone you are asked once which\r\n" +
            "  zone they are in; until then they stay in log time, dimmed.\r\n" +
            "  View > Time zone of this file, or the Sources tab, changes it.\r\n\r\n" +
            "MERGING\r\n" +
            "  Open two or more logs, then Ctrl+M. Lines are interleaved by timestamp\r\n" +
            "  into one timeline and a colour-coded source column appears. Stack traces\r\n" +
            "  stay attached to the line above them. Under the Sources tab you can hide\r\n" +
            "  and show each file without rebuilding the merge.\r\n\r\n" +
            "OPENING .LOG FILES BY DOUBLE-CLICK\r\n" +
            "  Tools > Set up as the log file handler. Windows does not let a program\r\n" +
            "  make itself the default, so finish in Open with > Choose another app,\r\n" +
            "  or in Settings > Default apps.";
        Prompt.Info(this, "Logzilla help", t);
    }

    private void ShowAbout()
    {
        Prompt.Info(this, "About " + AppInfo.Name,
            AppInfo.Name + "  " + AppInfo.Version + "\r\n" +
            "by " + AppInfo.Author + "\r\n\r\n" +
            "A log analyzer for very large files.\r\n\r\n" +
            "The file is never fully decoded. Indexing and search run over raw bytes and\r\n" +
            "only the lines on screen become text, so file size is limited by disk rather\r\n" +
            "than memory. Around 24 bytes of index are kept per line.\r\n\r\n" +
            "Settings and the recent file list live in:\r\n" + Paths.Dir);
    }

}
