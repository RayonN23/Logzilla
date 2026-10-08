using System.Windows.Forms;

namespace Logzilla.App.Views;

// Keyboard handling.

internal sealed partial class MainForm : Form

{
    private void QueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            _debounce.Stop();
            StartFilter();
            BeginInvoke((MethodInvoker)delegate { JumpMatch(e.Shift ? -1 : 1); });
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            if (_q.Text.Length > 0)
            {
                ClearFind();
            }      // first Esc clears
            else
            {
                _view.Focus();
            }                        // second leaves the box
        }
    }

    private void FormKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Tab && _docs.Count > 1)
        {
            var step = e.Shift ? -1 : 1;
            SwitchTab((_active + step + _docs.Count) % _docs.Count);
            SyncTabs();
            StartFilter();
            e.Handled = true;
            return;
        }

        if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
        {
            var index = e.KeyCode - Keys.D1;
            if (index < SideNames.Length)
            {
                _sideTab = SideNames[index];
                SyncSideTabs();
                FillSide();
                e.Handled = true;
                return;
            }
        }

        // Any box being typed into — search, time range, context, the Ask question —
        // owns its letters, Enter, Home, End and the arrows, not the grid behind it.
        var typing = _q.Focused || _ctx.Focused || EditingBox() != null;
        if (e.KeyCode == Keys.F5) { e.Handled = true; TailPoll(); return; }
        if (typing)
        {
            return;
        }

        // Shift with a movement key stretches the selection instead of moving it,
        // the way a list does everywhere else. Shift already means something on E
        // and B, so those keep it.
        switch (e.KeyCode)
        {
            case Keys.Oem2: _q.Focus(); _q.SelectAll(); e.Handled = true; break;
            case Keys.Escape: if (_view.Collapse()) { e.Handled = true; } break;
            case Keys.Enter: JumpMatch(e.Shift ? -1 : 1); e.Handled = true; break;
            case Keys.E: JumpSeverity(e.Shift ? -1 : 1, 5); e.Handled = true; break;
            case Keys.M: ToggleMark(); e.Handled = true; break;
            case Keys.B: JumpMark(e.Shift ? -1 : 1); e.Handled = true; break;
            case Keys.W: _levelMask = (1 << 4) | (1 << 5) | (1 << 6); SyncLevels(); StartFilter(); e.Handled = true; break;
            case Keys.Down: MoveSel(1, e.Shift); e.Handled = true; break;
            case Keys.Up: MoveSel(-1, e.Shift); e.Handled = true; break;
            case Keys.PageDown: MoveSel(_view.VisibleRows - 2, e.Shift); e.Handled = true; break;
            case Keys.PageUp: MoveSel(-(_view.VisibleRows - 2), e.Shift); e.Handled = true; break;
            case Keys.Home: MoveSel(int.MinValue / 2, e.Shift); e.Handled = true; break;
            case Keys.End: MoveSel(int.MaxValue / 2, e.Shift); e.Handled = true; break;
        }
    }

    private void MoveSel(int delta, bool extend = false)
    {
        if (_res == null || _res.View.Count == 0)
        {
            return;
        }
        var p = _view.Selected < 0 ? 0 : _view.ViewPosOf(_view.Selected);
        var np = (long)p + delta;
        if (np < 0)
        {
            np = 0;
        }
        if (np > _res.View.Count - 1)
        {
            np = _res.View.Count - 1;
        }
        var line = _res.View[(int)np];
        if (extend)
        {
            _view.SelectTo(line, false);
        }
        else
        {
            _view.Select(line, false);
        }
        _mini.Invalidate();
    }

    /// <summary>
    /// Ctrl+A. The menu owns the shortcut, which means the form sees it before the
    /// focused control does, so the search box has to be handed its own select-all
    /// back rather than having the grid steal it.
    /// </summary>
    private void SelectAllEntries()
    {
        if (_ctx.Focused)
        {
            return;
        }
        if (EditingBox() is { } box)
        {
            box.SelectAll();
            return;
        }
        _view.SelectAll();
        _mini.Invalidate();
        UpdateStatus();
    }

    /// <summary>
    /// The text box being typed into, if the focus is in one, however deeply it sits —
    /// the Ask question box lives inside the side panel, not on the form. Found by
    /// following the focus rather than by a list of known boxes, so a box added later
    /// cannot have its keystrokes taken as grid shortcuts again.
    /// </summary>
    private TextBoxBase EditingBox() => FocusedTextBox() is { ReadOnly: false } box ? box : null;

    private TextBoxBase FocusedTextBox()
    {
        Control c = ActiveControl;
        while (c is ContainerControl { ActiveControl: { } inner })
        {
            c = inner;
        }
        // A plain panel is not a ContainerControl, so finish the walk by focus.
        while (c is { Focused: false, ContainsFocus: true })
        {
            Control next = null;
            foreach (Control child in c.Controls)
            {
                if (child.ContainsFocus)
                {
                    next = child;
                    break;
                }
            }
            if (next == null)
            {
                break;
            }
            c = next;
        }
        return c is TextBoxBase { Focused: true } box ? box : null;
    }
}
