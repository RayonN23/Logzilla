using System.Windows.Forms;

namespace Logzilla.App.Controls;

/// <summary>
/// A plain Panel flickers when it hosts other custom-painted controls being
/// resized or restacked, because WinForms clears it between child paints.
/// Use this everywhere a bare Panel is used as a layout container in the shell.
/// </summary>
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }
}
