using System;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Dialogs;

/// <summary>
/// The window the field filter editor sits in: a borderless dialog, centred over the
/// main window and kept on the same monitor, that can be dragged by its header.
///
/// It used to be a drop-down under the "+ Field filter" button. That button sits at
/// the right edge of the window, so with the window maximised the drop-down ran off
/// the screen and onto the next monitor — Windows does not pull a drop-down back
/// onto the monitor its owner is on.
/// </summary>
internal sealed class FieldFilterDialog : Form
{
    private const int CsDropShadow = 0x00020000;

    public FieldFilterDialog(FieldFilterEditor editor, Form owner)
    {
        ArgumentNullException.ThrowIfNull(editor);
        Editor = editor;
        Text = "Filter by field";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.BgChrome;
        KeyPreview = true;
        Icon = owner?.Icon;
        ClientSize = editor.Size;
        editor.Location = Point.Empty;
        Controls.Add(editor);
        editor.Cancelled += delegate { Close(); };
        editor.DragMoved += delegate (object _, Size delta) { Location += delta; };
    }

    public FieldFilterEditor Editor { get; }

    /// <summary>A soft shadow, since there is no window frame to lift it off the log.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc from anywhere in the dialog, not only from the two text boxes.
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Editor.FocusFirst();
    }

    /// <summary>
    /// Where the dialog goes: where it was last left, relative to the main window, when
    /// that still lies wholly on the main window's monitor; centred over the main
    /// window otherwise. Always pulled inside the monitor's working area, so no part of
    /// it can end up on another screen or under the taskbar.
    /// </summary>
    public static Point Place(Rectangle owner, Rectangle workingArea, Size size, Point? lastOffset)
    {
        Point at;
        if (lastOffset is { } off)
        {
            at = new Point(owner.X + off.X, owner.Y + off.Y);
            if (workingArea.Contains(new Rectangle(at, size)))
            {
                return at;
            }
        }
        at = new Point(owner.X + (owner.Width - size.Width) / 2, owner.Y + Math.Max(0, (owner.Height - size.Height) / 3));
        var x = Math.Clamp(at.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - size.Width));
        var y = Math.Clamp(at.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - size.Height));
        return new Point(x, y);
    }
}
