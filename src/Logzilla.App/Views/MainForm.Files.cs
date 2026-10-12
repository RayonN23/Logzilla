using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Logs;

namespace Logzilla.App.Views;

// Opening, indexing, merging and following log files.

internal sealed partial class MainForm : Form

{
    private void DoOpen()
    {
        using (var d = new OpenFileDialog())
        {
            d.Multiselect = true;
            d.Filter = "Log files|*.log;*.log1;*.log2;*.txt;*.out;*.err;*.trace;*.syslog|All files|*.*";
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                OpenMany(d.FileNames);
            }
        }
    }

    private void DoOpenFolder()
    {
        using (var d = new FolderBrowserDialog())
        {
            d.Description = "Open every log file in a folder";
            if (d.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            try
            {
                var hits = new List<string>();
                foreach (var ext in new string[] { "*.log", "*.txt", "*.out", "*.err" })

                {
                    hits.AddRange(Directory.GetFiles(d.SelectedPath, ext));
                }
                hits.Sort();
                if (hits.Count == 0) { Info("No log files in that folder."); return; }
                if (hits.Count > 12)
                {
                    if (MessageBox.Show(this, hits.Count + " files found. Open the first 12?",
                        AppInfo.Name, MessageBoxButtons.OKCancel) != DialogResult.OK)
                    {
                        return;
                    }
                    hits = hits.GetRange(0, 12);
                }
                OpenMany(hits.ToArray());
            }
            catch (Exception ex) { Info("Could not read that folder.\r\n" + ex.Message); }
        }
    }

    public void OpenMany(string[] paths)
    {
        foreach (var p in paths)
        {
            try { if (System.IO.File.Exists(p))
{
    LoadFile(p);
} }
            catch (Exception ex) { Info("Could not open " + p + "\r\n" + ex.Message); }
        }
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "File load coordinates indexing pipeline and UI updates.")]
    public void LoadFile(string path)
    {
        for (var i = 0; i < _docs.Count; i++)

        {
            if (!_docs[i].Merged && _docs[i].Srcs.Count == 1 &&
                string.Equals(_docs[i].Srcs[0].Path, path, StringComparison.OrdinalIgnoreCase))
            { SwitchTab(i); SyncTabs(); StartFilter(); return; }
        }

        LogSource src;
        try
        {
            src = _indexer.Prepare(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Info("Could not open " + path + "\r\n" + ex.Message);
            return;
        }

        ApplySavedZone(src);
        src.TintIndex = _docs.Count;

        var doc = new LogDocument();
        doc.Name = src.Name;
        doc.Srcs.Add(src);

        _docs.Add(doc);
        SwitchTab(_docs.Count - 1);
        SyncTabs();
        Settings.PushRecent(path);

        _st3.Text = "Indexing " + src.Name + " ...";
        _progPct = 0; _progHost.Invalidate();

        // Cancelled only when this file's tab is closed: opening another file must not
        // stop this one part-way.
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        _indexing[doc] = cancel;
        var th = new Thread(delegate ()
        {
            try
            {
                _indexer.Run(doc, 0,
                    delegate (long done, long total)
                    {
                        var pct = total > 0 ? (int)(done * 100 / total) : 100;
                        if (pct != _progPct)
                        {
                            _progPct = pct;
                            try
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    _progHost.Invalidate();
                                    _st1.Text = src.Name + "  " + DisplayFormatter.Number(doc.N) + " entries";
                                    SyncTabs();
                                });
                            }
                            catch (Exception ex) { IgnoreNonCritical(ex, "Refresh indexing progress"); }
                        }
                    },
                    delegate { return token.IsCancellationRequested; });
            }
            catch (Exception ex)
            {
                try { BeginInvoke((MethodInvoker)delegate { Info("Indexing failed:\r\n" + ex.Message); }); }
                catch (Exception inner) { IgnoreNonCritical(inner, "Show indexing failure"); }
            }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _indexing.Remove(doc);
                    cancel.Dispose();
                    if (_indexing.Count == 0)
                    {
                        _progPct = -1; _progHost.Invalidate(); _st3.Text = "";
                    }
                    SyncTabs();
                    if (!token.IsCancellationRequested)
                    {
                        StartFilter();
                    }
                });
            }
            catch (Exception ex) { IgnoreNonCritical(ex, "Finalize indexing UI"); }
        });
        th.IsBackground = true;
        th.Start();
    }

    private void DoMerge()
    {
        var parts = new List<LogDocument>();
        foreach (var d in _docs)
        {
            if (!d.Merged && d.N > 0)
{
    parts.Add(d);
}
        }
        if (parts.Count < 2)
        {
            Info("Merging needs at least two files open.\r\n\r\n" +
                 "Open them with Ctrl+O or drop them on the window, then merge.");
            return;
        }
        if (parts.Count > 12)
        {
            parts = parts.GetRange(0, 12);
        }
        // A merge copies each file's lines as they stand. One still loading would be cut
        // off where its indexing had got to, and its line count — shared with the merged
        // view — would be counted by two threads at once.
        var loading = parts.Find(d => _indexing.ContainsKey(d) || d == _tailing);
        if (loading != null)
        {
            Info(loading.Name + " is still loading. Merge once it has finished.");
            return;
        }

        var untimed = false;
        foreach (var d in parts)
        {
            if (d.Srcs[0].Kind == TsKind.None)
{
    untimed = true;
}
        }
        if (untimed && MessageBox.Show(this,
            "At least one file has no timestamps, so its lines cannot be placed on a shared timeline.\r\n\r\n" +
            "Merge anyway? Those lines will be grouped at the start.",
            AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        // Files whose zone is known are merged by the instant each line happened; a
        // file whose zone nobody knows can only be placed by its wall clock, which is
        // hours out if it was not written in UTC. Ask rather than let it slide.
        var known = parts.Exists(d => TimeZones.IsKnown(d.Srcs[0]));
        if (known)
        {
            AskUndeclared(parts.ConvertAll(d => d.Srcs[0]));
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            var m = _indexer.Merge(parts);
            _docs.Add(m);
            SwitchTab(_docs.Count - 1);
            SyncTabs();
            _sideTab = "sources"; SyncSideTabs();
            StartFilter();
            _st3.Text = "Merged " + parts.Count + " files, " + DisplayFormatter.Number(m.N) + " entries";
        }
        catch (Exception ex) { Info("Merge failed:\r\n" + ex.Message); }
        finally { Cursor = Cursors.Default; }
    }

    private void DoClose(int i)
    {
        if (i < 0 || i >= _docs.Count)
        {
            return;
        }
        _worstSev.Remove(_docs[i]);
        if (_indexing.TryGetValue(_docs[i], out var indexing))
        {
            indexing.Cancel();
        }
        var closing = _docs[i];
        var wasActive = i == _active;
        _docs.RemoveAt(i);
        ForgetTabSearch(closing);
        if (wasActive || _docs.Count == 0)
        {
            // The tab in front went: the one that takes its place brings its own search.
            SwitchTab(_docs.Count == 0 ? -1 : System.Math.Min(i, _docs.Count - 1));
        }
        else if (i < _active)
        {
            // A tab to the left closed: the one in front stays in front.
            _active--;
        }
        SyncTabs();
        if (_docs.Count == 0)
        {
            _res = null; _view.Attach(null, null);
            _tl.Res = null; _tl.Invalidate();
            _mini.Res = null; _mini.Invalidate();
            FillSide(); UpdateStatus();
        }
        else
        {
            StartFilter();
        }
    }

    private void ToggleTail()
    {
        var f = F;
        if (f != null && f.Merged) { Info("Follow works on a single file, not on a merged view."); return; }
        _tail = !_tail;
        _miFollow.Checked = _tail;
        SetToggleStates();
        if (_tail)
        {
            _tailTimer.Start();
        } else
{
    _tailTimer.Stop();
}
    }

    /// <summary>The document a follow poll is reading new lines into, on its own thread;
    /// null when none is. The next tick, merging and zone changes wait for it.</summary>
    private LogDocument _tailing;

    private void TailPoll()
    {
        var f = F;
        if (_tailing != null || f == null || f.Merged || _indexing.ContainsKey(f))
        {
            return;
        }
        var src = f.Srcs[0];
        long len;
        try { len = new FileInfo(src.Path).Length; }
        catch (Exception ex) { IgnoreNonCritical(ex, "Read file length while following"); return; }
        if (len == src.Size)
        {
            return;
        }
        try
        {
            // Both branches below re-index lines that were already counted.
            _worstSev.Remove(f);
            if (len < src.Size)
            {
                // the file was truncated or rotated: start over
                f.N = 0; src.Lines = 0; src.IndexedTo = src.DataStart; src.LastUnterminated = false;
                src.Offsets.Clear();
                f.Marks.Clear();
                _view.ForgetLines();
            }
            else if (src.LastUnterminated && f.N > 0) { f.N--; src.Lines--; }
        }
        catch (Exception ex)
        {
            IgnoreNonCritical(ex, "Follow-file poll");
            return;
        }
        // Read on a thread of its own, like the first load: a burst of output, or a
        // rotated file read again from the start, froze the window while it was indexed.
        _tailing = f;
        var th = new Thread(delegate ()
        {
            try { _indexer.Run(f, 0, null, null); }
            catch (Exception ex) { IgnoreNonCritical(ex, "Follow-file poll"); }
            BeginInvokeSafe(() =>
            {
                _tailing = null;
                SyncTabs();
                if (ReferenceEquals(f, F))
                {
                    _followEnd = true;
                    StartFilter();
                }
            });
        });
        th.IsBackground = true;
        th.Start();
    }

}
