using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Dialogs;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Views;

// filter bar, the editor that adds and changes them, and every shortcut into it —
// the "+ Field filter" button, Ctrl+Shift+F, the Fields panel and the grid's
// right-click menu.

internal sealed partial class MainForm
{
    private readonly List<FieldFilter> _fieldFilters = new();
    private FieldChipBar _fieldBar;

    private FieldCatalog _catalog;
    private LogDocument _catalogDoc;
    private int _catalogN = -1;

    private void BuildFieldBar()
    {
        _fieldBar = new FieldChipBar { Dock = DockStyle.Top, Visible = false };
        _fieldBar.EditRequested += delegate (object _, FieldChipBar.IndexArgs a)
        {
            OpenFieldEditor(a.Index);
        };
        _fieldBar.RemoveRequested += delegate (object _, FieldChipBar.IndexArgs a) { RemoveFieldFilter(a.Index); };
        _fieldBar.AddRequested += delegate { OpenFieldEditor(-1); };
        _fieldBar.ClearRequested += delegate { ClearFieldFilters(); };
        _fieldBar.MenuRequested += delegate (object _, FieldChipBar.IndexArgs a) { ShowChipMenu(a.Index, a.Location); };
        _fieldBar.Resize += delegate { SyncFieldBarHeight(); };
    }

    /// <summary>Shows the current filters, and the strip only while there are any.</summary>
    private void SyncFieldBar()
    {
        _fieldBar.Filters = _fieldFilters.ToArray();
        _fieldBar.Visible = _fieldFilters.Count > 0;
        SyncFieldBarHeight();
        UpdateResetBadge();
    }

    private void SyncFieldBarHeight()
    {
        var width = _fieldBar.Width > 0 ? _fieldBar.Width : ClientSize.Width;
        var h = _fieldBar.Relayout(width);
        if (_fieldBar.Height != h)
        {
            _fieldBar.Height = h;
        }
    }

    /// <summary>Redraws the strip and re-runs the query. Straight away, not debounced:
    /// a filter is added by one deliberate click, not by typing.</summary>
    private void ApplyFieldFilters(string status = null)
    {
        SyncFieldBar();
        StartFilter();
        if (status != null)
        {
            _st3.Text = status;
        }
    }

    private void AddFieldFilter(string key, FieldOp op, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value))
        {
            return;
        }
        var at = FieldFilterList.Add(_fieldFilters, key, op, [value]);
        ApplyFieldFilters(at >= 0 ? "Filter: " + FieldNames.Describe(_fieldFilters[at]) : null);
    }

    private void RemoveFieldFilter(int index)
    {
        if (index < 0 || index >= _fieldFilters.Count)
        {
            return;
        }
        var gone = _fieldFilters[index];
        _fieldFilters.RemoveAt(index);
        ApplyFieldFilters("Removed: " + FieldNames.Describe(gone));
        if (_fieldFilters.Count > 0)
        {
            _fieldBar.Focus();
        }
    }

    private void ClearFieldFilters()
    {
        if (_fieldFilters.Count == 0)
        {
            return;
        }
        _fieldFilters.Clear();
        ApplyFieldFilters("Field filters cleared");
        _view.Focus();
    }

    // ── the editor ─────────────────────────────────────────────────────

    /// <summary>The fields and values of the open log, sampled once and kept until the
    /// log changes or grows.</summary>
    private FieldCatalog Catalog()
    {
        var f = F;
        if (f == null)
        {
            return new FieldCatalog();
        }
        if (_catalog != null && _catalogDoc == f && _catalogN == f.N)
        {
            return _catalog;
        }
        var cursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            using var reader = _indexer.OpenReader(f, 1 << 20);
            _catalog = FieldCatalog.Build(f, reader, _jsonReader);
            _catalogDoc = f;
            _catalogN = f.N;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            IgnoreNonCritical(ex, "Sample fields");
            _catalog = new FieldCatalog();
        }
        finally
        {
            Cursor = cursor;
        }
        return _catalog;
    }

    /// <summary>Where the user last left the dialog, relative to the main window, so it
    /// opens there again for the rest of the session.</summary>
    private Point? _fieldDialogOffset;

    /// <summary>
    /// Opens the editor in its dialog. With <paramref name="editIndex"/> it edits that
    /// filter; otherwise it starts a new one, optionally with the field (and operator
    /// and values) already filled in.
    /// </summary>
    private void OpenFieldEditor(int editIndex, string key = null, FieldOp op = FieldOp.Is, IEnumerable<string> values = null)
    {
        if (F == null)
        {
            Info("Open a log first, then filter it by a field.");
            return;
        }

        var editor = new FieldFilterEditor(Catalog());
        var editing = editIndex >= 0 && editIndex < _fieldFilters.Count;
        if (editing)
        {
            var existing = _fieldFilters[editIndex];
            editor.Begin(existing.Key, existing.Op, existing.Values, editing: true);
        }
        else
        {
            editor.Begin(key, op, values, editing: false);
        }

        FieldFilterEditor.CommitArgs result = null;
        using (var dialog = new FieldFilterDialog(editor, this))
        {
            editor.Committed += delegate (object _, FieldFilterEditor.CommitArgs a)
            {
                result = a;
                dialog.Close();
            };
            // The monitor the main window is on, not wherever the button happens to be.
            var area = Screen.FromControl(this).WorkingArea;
            dialog.Location = FieldFilterDialog.Place(Bounds, area, dialog.Size, _fieldDialogOffset);
            dialog.ShowDialog(this);
            _fieldDialogOffset = new Point(dialog.Left - Left, dialog.Top - Top);
        }

        if (result == null)
        {
            return;
        }
        if (result.Remove)
        {
            RemoveFieldFilter(editIndex);
        }
        else if (editing)
        {
            FieldFilterList.Replace(_fieldFilters, editIndex, result.Key, result.Op, result.Values);
            ApplyFieldFilters("Filter: " + FieldNames.Describe(new FieldFilter(result.Key, result.Op, result.Values)));
        }
        else
        {
            var i = FieldFilterList.Add(_fieldFilters, result.Key, result.Op, result.Values);
            ApplyFieldFilters(i >= 0 ? "Filter: " + FieldNames.Describe(_fieldFilters[i]) : null);
        }
    }

    /// <summary>Ctrl+Shift+F and the Edit menu: a new filter.</summary>
    private void OpenFieldEditorFromKeyboard() => OpenFieldEditor(-1);

    private void ShowChipMenu(int index, Point location)
    {
        if (index < 0 || index >= _fieldFilters.Count)
        {
            return;
        }
        var f = _fieldFilters[index];
        var cm = new ContextMenuStrip();
        cm.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        cm.BackColor = Theme.BgChrome; cm.ForeColor = Theme.Text; cm.Font = Theme.UiBody;
        cm.Items.Add(Mi("Edit…", Keys.None, delegate { OpenFieldEditor(index); }));
        if (FieldFilterList.CanInvert(f))
        {
            var label = f.Op == FieldOp.IsNot ? "Show these values instead" : "Hide these values instead";
            cm.Items.Add(Mi(label, Keys.None, delegate
            {
                FieldFilterList.Invert(_fieldFilters, index);
                ApplyFieldFilters();
            }));
        }
        cm.Items.Add(Mi("Remove", Keys.None, delegate { RemoveFieldFilter(index); }));
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add(Mi("Clear all field filters", Keys.None, delegate { ClearFieldFilters(); }));
        foreach (ToolStripItem it in cm.Items) { it.BackColor = Theme.BgChrome; it.ForeColor = Theme.Text; }
        cm.Closed += delegate { BeginInvoke((MethodInvoker)cm.Dispose); };
        cm.Show(_fieldBar, location);
    }

    /// <summary>
    /// The grid's right-click menu gains "Show only where…" and "Hide where…", listing
    /// the clicked entry's own fields, so following one through the log
    /// is right-click, click. The entry's correlation-like ids come first.
    /// </summary>
    private void AddFieldMenus(ContextMenuStrip cm, int line)
    {
        var fields = _view.FieldsOf(line, 12);
        if (fields.Count == 0)
        {
            return;
        }
        fields = fields.OrderBy(f => FieldNames.CorrelationRank(f.Key) is var r && r >= 0 ? r : 9).ToList();
        var show = new ToolStripMenuItem("Show only where…") { BackColor = Theme.BgChrome, ForeColor = Theme.Text };
        var hide = new ToolStripMenuItem("Hide where…") { BackColor = Theme.BgChrome, ForeColor = Theme.Text };
        show.DropDown.BackColor = Theme.BgChrome;
        hide.DropDown.BackColor = Theme.BgChrome;
        foreach (var (key, value) in fields)
        {
            var k = key;
            var v = value;
            var label = FieldNames.Display(key) + " = " + FieldChipBar.Shorten(value);
            show.DropDownItems.Add(Mi(label, Keys.None, delegate { AddFieldFilter(k, FieldOp.Is, v); }));
            hide.DropDownItems.Add(Mi(label, Keys.None, delegate { AddFieldFilter(k, FieldOp.IsNot, v); }));
        }
        show.DropDownItems.Add(new ToolStripSeparator());
        show.DropDownItems.Add(Mi("Other field…  (Ctrl+Shift+F)", Keys.None, delegate { OpenFieldEditorFromKeyboard(); }));
        cm.Items.Add(show);
        cm.Items.Add(hide);
    }
}
