using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Query;

namespace Logzilla.App.Controls;

/// <summary>
/// The field filter editor: pick a field, how to compare it, and one or more values.
///
/// Built to be driven from the keyboard without ever reading a manual. The list under
/// the boxes always shows what the box you are in could take — the log's fields, most
/// common first, then that field's values with how often each was seen — and narrows
/// as you type. Arrows move, Enter takes, Esc leaves. Values can be ticked with the
/// mouse to match any of several at once.
/// </summary>
internal sealed class FieldFilterEditor : Control
{
    /// <summary>A text box that keeps the keys the editor uses for itself, so the dialog
    /// hosting it does not take arrows, Enter or Esc for its own navigation first.</summary>
    private sealed class EditorBox : TextBox
    {
        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Enter or Keys.Escape or Keys.Tab || base.IsInputKey(keyData);
    }

    public sealed class CommitArgs : EventArgs
    {
        public CommitArgs(string key, FieldOp op, List<string> values, bool remove)
        {
            Key = key; Op = op; Values = values; Remove = remove;
        }
        public string Key { get; }
        public FieldOp Op { get; }
        public List<string> Values { get; }
        /// <summary>The Remove button was pressed on a filter being edited.</summary>
        public bool Remove { get; }
    }

    public event EventHandler<CommitArgs> Committed;
    public event EventHandler Cancelled;

    private const int W = 460;
    private const int PadX = 16;
    private const int LabelW = 56;
    private const int BoxH = 32;
    private const int ListRows = 8;

    private readonly FieldCatalog _catalog;
    private readonly EditorBox _key = new();
    private readonly EditorBox _value = new();
    private readonly ToggleChip[] _ops = new ToggleChip[3];
    private readonly SuggestionList _list = new();
    private readonly FlatButton _apply, _cancel, _remove;
    private readonly List<string> _ticked = new();
    private readonly ToolTip _tip = new();
    private FieldOp _op = FieldOp.Is;
    private bool _editing;
    private bool _listingValues;
    private bool _keyError, _valueError;
    private Rectangle _keyFrame, _valueFrame, _captionRect;
    private string _caption = "";

    public FieldFilterEditor(FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgChrome;
        Font = Theme.UiBody;

        foreach (var box in new[] { _key, _value })
        {
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.BgData;
            box.ForeColor = Theme.Text;
            box.Font = Theme.MonoBody;
            Controls.Add(box);
        }
        _key.AccessibleName = "Field name";
        _value.AccessibleName = "Value";
        _key.PlaceholderText = "Feature, tenant …";
        _value.PlaceholderText = "type or pick a value";
        _key.TextChanged += delegate { _keyError = false; ListKeys(); };
        _value.TextChanged += delegate { _valueError = false; ListValues(); };
        _key.Enter += delegate { ListKeys(); Invalidate(); };
        _value.Enter += delegate { ListValues(); Invalidate(); };
        _key.Leave += delegate { Invalidate(); };
        _value.Leave += delegate { Invalidate(); };
        _key.KeyDown += KeyBoxKeyDown;
        _value.KeyDown += ValueBoxKeyDown;

        string[] opNames = ["is", "contains", "is not"];
        string[] opTips =
        [
            "The whole value matches  (Alt+I)",
            "The value contains the text  (Alt+C)",
            "Hide entries with this value  (Alt+N)",
        ];
        for (var i = 0; i < 3; i++)
        {
            var op = (FieldOp)i;
            var chip = new ToggleChip { Label = opNames[i], BackColor = Theme.BgChrome };
            chip.Height = Dpi.S(Theme.ChipHeight);
            chip.Fit(64);
            chip.AccessibleName = "Compare: " + opNames[i];
            chip.Click2 += delegate { SetOp(op); };
            _tip.SetToolTip(chip, opTips[i]);
            _ops[i] = chip;
            Controls.Add(chip);
        }

        _list.ItemClicked += ListClicked;
        _list.ItemDoubleClicked += ListDoubleClicked;
        Controls.Add(_list);

        _apply = new FlatButton { Text = "Apply", BorderColor = Theme.Accent, ForeColor = Theme.Text };
        _apply.FitTo(76, Theme.ControlHeight);
        _apply.Click += delegate { Apply(); };
        _cancel = new FlatButton { Text = "Cancel", Style = FlatButton.Look.Ghost };
        _cancel.FitTo(64, Theme.ControlHeight);
        _cancel.Click += delegate { Cancelled?.Invoke(this, EventArgs.Empty); };
        _remove = new FlatButton { Text = "Remove filter", Style = FlatButton.Look.Ghost, ForeColor = Theme.SevColor[5], Visible = false };
        _remove.FitTo(64, Theme.ControlHeight);
        _remove.Click += delegate { Committed?.Invoke(this, new CommitArgs(_key.Text.Trim(), _op, new List<string>(), remove: true)); };
        Controls.Add(_apply);
        Controls.Add(_cancel);
        Controls.Add(_remove);

        LayoutParts();
        SetOp(FieldOp.Is);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Fills the editor. <paramref name="editing"/> is for an existing filter:
    /// its values start ticked, and a Remove button appears.</summary>
    public void Begin(string key, FieldOp op, IEnumerable<string> values, bool editing)
    {
        _editing = editing;
        _remove.Visible = editing;
        _apply.Text = editing ? "Update" : "Apply";
        _apply.FitTo(76, Theme.ControlHeight);
        _ticked.Clear();
        if (values != null)
        {
            _ticked.AddRange(values.Where(v => !string.IsNullOrEmpty(v)));
        }
        SetOp(op);
        _key.Text = key ?? "";
        _value.Text = "";
        LayoutParts();
        Invalidate();
    }

    /// <summary>Puts the caret where typing should start: the field box for a new
    /// filter, the value box once the field is known.</summary>
    public void FocusFirst()
    {
        var box = _key.Text.Trim().Length == 0 ? _key : _value;
        box.Focus();
        box.SelectAll();
        if (box == _key)
        {
            ListKeys();
        }
        else
        {
            ListValues();
        }
    }

    // ── layout and paint ───────────────────────────────────────────────

    private static int Row(int unscaled) => Dpi.S(unscaled);

    private void LayoutParts()
    {
        var w = Dpi.S(W);
        var padX = Dpi.S(PadX);
        var boxX = padX + Dpi.S(LabelW);
        var boxW = w - boxX - padX;

        _keyFrame = new Rectangle(boxX, Row(48), boxW, Dpi.S(BoxH));
        PlaceBox(_key, _keyFrame);

        var x = boxX;
        foreach (var chip in _ops)
        {
            chip.Location = new Point(x, Row(90));
            x += chip.Width + Dpi.S(6);
        }

        _valueFrame = new Rectangle(boxX, Row(126), boxW, Dpi.S(BoxH));
        PlaceBox(_value, _valueFrame);

        _captionRect = new Rectangle(padX, Row(168), w - padX * 2, Dpi.S(18));
        var listTop = _captionRect.Bottom + Dpi.S(4);
        _list.SetBounds(padX, listTop, w - padX * 2, SuggestionList.RowHeight * ListRows);

        var footY = _list.Bottom + Dpi.S(12);
        _apply.Location = new Point(w - padX - _apply.Width, footY);
        _cancel.Location = new Point(_apply.Left - Dpi.S(6) - _cancel.Width, footY);
        _remove.Location = new Point(padX - Dpi.S(4), footY);
        Size = new Size(w, footY + _apply.Height + Dpi.S(30));
    }

    private static void PlaceBox(TextBox box, Rectangle frame)
    {
        var inset = Dpi.S(10);
        box.SetBounds(frame.X + inset, frame.Y + (frame.Height - box.PreferredHeight) / 2, frame.Width - inset * 2, box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var border = new Pen(Theme.LineControlStrong))
        {
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        var padX = Dpi.S(PadX);
        // The header is the handle the dialog is dragged by; a faint band and grip
        // dots say so before the cursor does.
        using (var band = new SolidBrush(_headerHot || _dragFrom.HasValue ? Theme.BgRaised : Theme.BgChromeLow))
        {
            g.FillRectangle(band, 1, 1, Width - 2, HeaderHeight - 1);
        }
        using (var line = new Pen(Theme.Line))
        {
            g.DrawLine(line, 1, HeaderHeight, Width - 2, HeaderHeight);
        }
        TextUtil.DrawTracked(g, _editing ? "Edit field filter" : "Filter by field", Theme.UiHead,
            new Point(padX, (HeaderHeight - Theme.UiHead.Height) / 2), Theme.TextDim, 0.8f);
        DrawGrip(g);
        var close = CloseRect;
        if (_closeHot)
        {
            using var b = new SolidBrush(Theme.BgControl);
            g.FillRectangle(b, close);
        }
        TextRenderer.DrawText(g, "×", Theme.UiBody, close, _closeHot ? Theme.Text : Theme.TextDim, Theme.TextFlagsCenter);

        DrawLabel(g, "Field", _keyFrame);
        DrawLabel(g, "Match", new Rectangle(_keyFrame.X, _ops[0].Top, 0, _ops[0].Height));
        DrawLabel(g, "Value", _valueFrame);
        DrawFrame(g, _keyFrame, _key.Focused, _keyError);
        DrawFrame(g, _valueFrame, _value.Focused, _valueError);

        TextUtil.DrawTracked(g, _caption, Theme.UiTag, new Point(_captionRect.X, _captionRect.Y + Dpi.S(3)),
            _valueError || _keyError ? Theme.SevColor[5] : Theme.TextDim, 0.5f);

        // One line on how the pieces combine, so "or" and "and" never need guessing.
        const string hint = "Enter applies  ·  Esc closes  ·  ticked values match any of them";
        var hintY = _apply.Bottom + Dpi.S(8);
        TextRenderer.DrawText(g, hint, Theme.MonoTiny, new Point(padX, hintY), Theme.TextDim, Theme.TextFlagsPoint);
    }

    // ── moving the dialog ──────────────────────────────────────────────
    // The editor has no window frame, so its header is what the dialog is dragged by.
    // It reports how far the pointer moved and the dialog moves itself.

    /// <summary>The header was dragged by this much, in screen pixels.</summary>
    public event EventHandler<Size> DragMoved;

    private static int HeaderHeight => Dpi.S(34);

    private Rectangle CloseRect
    {
        get
        {
            var s = Dpi.S(26);
            return new Rectangle(Width - Dpi.S(6) - s, (HeaderHeight - s) / 2, s, s);
        }
    }

    private Point? _dragFrom;
    private bool _headerHot, _closeHot;

    internal bool InHeader(Point p) => p.Y >= 0 && p.Y < HeaderHeight && !CloseRect.Contains(p);

    private void DrawGrip(Graphics g)
    {
        // Two rows of dots left of the ×: the usual "you can move this" mark.
        using var b = new SolidBrush(_headerHot || _dragFrom.HasValue ? Theme.Text3 : Theme.TextDim);
        var d = Math.Max(2, Dpi.S(2));
        var gap = Dpi.S(4);
        var right = CloseRect.Left - Dpi.S(10);
        var top = HeaderHeight / 2 - gap / 2 - d;
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                g.FillRectangle(b, right - (col + 1) * (d + gap), top + row * (d + gap), d, d);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && InHeader(e.Location))
        {
            _dragFrom = PointToScreen(e.Location);
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is { } from)
        {
            var now = PointToScreen(e.Location);
            var delta = new Size(now.X - from.X, now.Y - from.Y);
            if (delta != Size.Empty)
            {
                // The dialog moves under the pointer, so the next delta is measured from here.
                _dragFrom = now;
                DragMoved?.Invoke(this, delta);
            }
            return;
        }
        var header = InHeader(e.Location);
        var close = CloseRect.Contains(e.Location);
        Cursor = header ? Cursors.SizeAll : close ? Cursors.Hand : Cursors.Default;
        if (header != _headerHot || close != _closeHot)
        {
            _headerHot = header;
            _closeHot = close;
            Invalidate(new Rectangle(0, 0, Width, HeaderHeight + 1));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragFrom.HasValue)
        {
            _dragFrom = null;
            Capture = false;
            Invalidate(new Rectangle(0, 0, Width, HeaderHeight + 1));
            return;
        }
        if (e.Button == MouseButtons.Left && CloseRect.Contains(e.Location))
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_headerHot || _closeHot)
        {
            _headerHot = _closeHot = false;
            Invalidate(new Rectangle(0, 0, Width, HeaderHeight + 1));
        }
    }

    private static void DrawLabel(Graphics g, string text, Rectangle frame)
    {
        TextRenderer.DrawText(g, text, Theme.UiBody, new Rectangle(Dpi.S(PadX), frame.Y, Dpi.S(LabelW), frame.Height),
            Theme.Text2, Theme.TextFlags);
    }

    private static void DrawFrame(Graphics g, Rectangle r, bool focused, bool error)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = TextUtil.RoundedRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Dpi.S(Theme.RadiusField)))
        {
            using (var b = new SolidBrush(Theme.BgData))
            {
                g.FillPath(b, path);
            }
            using var p = new Pen(error ? Theme.SevColor[5] : focused ? Theme.AccentDim : Theme.LineControl);
            g.DrawPath(p, path);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
    }

    // ── state ──────────────────────────────────────────────────────────

    private void SetOp(FieldOp op)
    {
        _op = op;
        for (var i = 0; i < _ops.Length; i++)
        {
            _ops[i].On = (int)op == i;
            _ops[i].Invalidate();
        }
    }

    private void SetCaption(string caption)
    {
        _caption = caption;
        Invalidate(_captionRect);
    }

    private static bool Matches(string candidate, string typed) =>
        typed.Length == 0 || candidate.Contains(typed, StringComparison.OrdinalIgnoreCase);

    /// <summary>The fields list: what the log carries, narrowed to what is typed, the
    /// ones starting with it first.</summary>
    private void ListKeys()
    {
        _listingValues = false;
        _list.Checkable = false;
        var typed = _key.Text.Trim();
        var keys = _catalog.Keys()
            .Where(k => Matches(k.Key, typed) || Matches(FieldNames.Display(k.Key), typed))
            .OrderBy(k => k.Key.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        var items = keys.Select(k => new SuggestionList.Item
        {
            Text = k.Key,
            Label = FieldNames.Display(k.Key) == k.Key ? null : FieldNames.DisplayWithKey(k.Key),
            Right = DisplayFormatter.Number(k.Entries),
        }).ToList();
        var exact = keys.FindIndex(k => string.Equals(k.Key, typed, StringComparison.OrdinalIgnoreCase));
        _list.EmptyText = _catalog.Scanned == 0 || _catalog.Keys().Count == 0
            ? "No fields found in this log. Type the name of one, such as Feature."
            : "No field by that name in the sample. Press Tab to use it anyway.";
        _list.SetItems(items, exact >= 0 ? exact : typed.Length > 0 && items.Count > 0 ? 0 : -1);
        SetCaption(_keyError ? "Choose a field first" :
            "Fields in this log  ·  entries carrying each, of " + DisplayFormatter.Number(_catalog.Scanned) + " sampled");
    }

    /// <summary>The values list for the chosen field: ticked values first, then what
    /// was seen, most common first, narrowed to what is typed.</summary>
    private void ListValues()
    {
        _listingValues = true;
        _list.Checkable = true;
        var key = _key.Text.Trim();
        var typed = _value.Text.Trim();
        var seen = _catalog.ValuesOf(key);
        var items = new List<SuggestionList.Item>();
        foreach (var t in _ticked)
        {
            var n = seen.FindIndex(s => string.Equals(s.Value, t, StringComparison.OrdinalIgnoreCase));
            items.Add(new SuggestionList.Item { Text = t, Checked = true, Right = n >= 0 ? DisplayFormatter.Number(seen[n].Count) : "" });
        }
        foreach (var (value, count) in seen)
        {
            if (Matches(value, typed) && !_ticked.Exists(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new SuggestionList.Item { Text = value, Right = DisplayFormatter.Number(count) });
                if (items.Count >= 500)
                {
                    break;
                }
            }
        }
        _list.EmptyText = key.Length == 0
            ? "Choose a field above first."
            : "No values seen for " + key + ". Type one and press Enter.";
        var first = typed.Length > 0 ? items.FindIndex(i => !i.Checked) : -1;
        _list.SetItems(items, first);
        var what = key.Length == 0 ? "Values" : "Values of " + FieldNames.Display(key);
        SetCaption(_valueError ? "Pick a value, or type one" :
            _ticked.Count > 0 ? what + "  ·  " + _ticked.Count + " ticked" : what + "  ·  click to tick several");
    }

    private void ListClicked(object sender, int index)
    {
        var item = _list.Items[index];
        if (!_listingValues)
        {
            TakeKey(item.Text);
            return;
        }
        Toggle(item.Text);
        _value.Focus();
    }

    private void ListDoubleClicked(object sender, int index)
    {
        if (!_listingValues)
        {
            return;
        }
        var text = _list.Items[index].Text;
        if (!_ticked.Exists(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase)))
        {
            _ticked.Add(text);
        }
        Apply();
    }

    private void Toggle(string value)
    {
        var at = _ticked.FindIndex(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            _ticked.RemoveAt(at);
        }
        else
        {
            _ticked.Add(value);
        }
        _valueError = false;
        var keep = _list.Highlight;
        ListValues();
        _list.Highlight = Math.Min(keep, _list.Items.Count - 1);
    }

    private void TakeKey(string key)
    {
        var changed = !string.Equals(_key.Text.Trim(), key, StringComparison.OrdinalIgnoreCase);
        _key.Text = key;
        if (changed)
        {
            _ticked.Clear();
        }
        _value.Focus();
        _value.SelectAll();
        ListValues();
    }

    // ── keys ───────────────────────────────────────────────────────────

    private bool OpShortcut(KeyEventArgs e)
    {
        if (!e.Alt)
        {
            return false;
        }
        switch (e.KeyCode)
        {
            case Keys.I: SetOp(FieldOp.Is); break;
            case Keys.C: SetOp(FieldOp.Contains); break;
            case Keys.N: SetOp(FieldOp.IsNot); break;
            default: return false;
        }
        e.SuppressKeyPress = true;
        return true;
    }

    private void KeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (OpShortcut(e))
        {
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Down: _list.Step(1); break;
            case Keys.Up: _list.Step(-1); break;
            case Keys.Escape: Cancelled?.Invoke(this, EventArgs.Empty); break;
            case Keys.Enter:
            case Keys.Tab:
                var pick = _list.Highlight >= 0 && !_listingValues ? _list.Items[_list.Highlight].Text : _catalog.Spelling(_key.Text);
                if (string.IsNullOrWhiteSpace(pick))
                {
                    _keyError = true;
                    ListKeys();
                    Invalidate();
                    break;
                }
                TakeKey(pick);
                break;
            default:
                return;
        }
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private void ValueBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (OpShortcut(e))
        {
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Down: _list.Step(1); break;
            case Keys.Up: _list.Step(-1); break;
            case Keys.Escape: Cancelled?.Invoke(this, EventArgs.Empty); break;
            case Keys.Enter: Apply(); break;
            case Keys.Tab:
                if (e.Shift)
                {
                    _key.Focus();
                    _key.SelectAll();
                }
                else
                {
                    _apply.Focus();
                }
                break;
            case Keys.Back when _value.TextLength == 0 && _value.SelectionStart == 0:
                // Backspace in an empty value box steps back to the field.
                _key.Focus();
                _key.SelectionStart = _key.TextLength;
                break;
            default:
                return;
        }
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    /// <summary>
    /// Collects what is ticked and what is typed into a filter. With neither, Enter
    /// takes the highlighted suggestion, so arrows plus Enter is all the keyboard it
    /// takes.
    /// </summary>
    private void Apply()
    {
        var key = _catalog.Spelling(_key.Text.Trim());
        if (string.IsNullOrEmpty(key))
        {
            _keyError = true;
            _key.Focus();
            ListKeys();
            Invalidate();
            return;
        }
        var values = new List<string>(_ticked);
        var typed = _value.Text.Trim();
        if (typed.Length > 0 && !values.Exists(v => string.Equals(v, typed, StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(typed);
        }
        if (values.Count == 0 && _listingValues && _list.Highlight >= 0)
        {
            values.Add(_list.Items[_list.Highlight].Text);
        }
        if (values.Count == 0)
        {
            _valueError = true;
            _value.Focus();
            ListValues();
            Invalidate();
            return;
        }
        Committed?.Invoke(this, new CommitArgs(key, _op, values, remove: false));
    }

    // For tests, which drive the editor the way the keyboard and mouse would.
    internal TextBox KeyBox => _key;
    internal TextBox ValueBox => _value;
    internal SuggestionList Suggestions => _list;
    internal void ShowKeys() => ListKeys();
    internal void ShowValues() => ListValues();
    internal void PressEnterInKeyBox() => KeyBoxKeyDown(_key, new KeyEventArgs(Keys.Enter));
    internal void PressEnterInValueBox() => ValueBoxKeyDown(_value, new KeyEventArgs(Keys.Enter));
    internal void ClickSuggestion(int index) => ListClicked(_list, index);
    internal void ChooseOp(FieldOp op) => SetOp(op);
}
