using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Dialogs;

internal static class Prompt
{
    public static string Ask(IWin32Window owner, string title)
    {
        using var f = new Form();
        f.Text = title;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.StartPosition = FormStartPosition.CenterParent;
        f.ClientSize = new Size(300, 84);
        f.BackColor = Theme.BgChrome;
        f.ForeColor = Theme.Text;
        f.MinimizeBox = false;
        f.MaximizeBox = false;
        f.Icon = OwnerIcon(owner);
        var t = new TextBox();
        t.Location = new Point(12, 16);
        t.Width = 276;
        t.BackColor = Theme.BgData;
        t.ForeColor = Theme.Text;
        t.BorderStyle = BorderStyle.FixedSingle;
        var ok = new Button();
        ok.Text = "OK";
        ok.Location = new Point(210, 50);
        ok.Width = 78;
        ok.FlatStyle = FlatStyle.Flat;
        ok.BackColor = Theme.BgControl;
        ok.ForeColor = Theme.Text;
        ok.FlatAppearance.BorderColor = Theme.LineControl;
        ok.DialogResult = DialogResult.OK;
        f.Controls.Add(t);
        f.Controls.Add(ok);
        f.AcceptButton = ok;
        return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
    }

    public static void Info(IWin32Window owner, string title, string body)
    {
        using var f = new Form();
        f.Text = title;
        f.StartPosition = FormStartPosition.CenterParent;
        f.ClientSize = new Size(680, 560);
        f.BackColor = Theme.BgChrome;
        f.ForeColor = Theme.Text;
        f.Icon = OwnerIcon(owner);
        var t = new TextBox();
        t.Multiline = true;
        t.ReadOnly = true;
        t.ScrollBars = ScrollBars.Vertical;
        t.Dock = DockStyle.Fill;
        t.BackColor = Theme.BgData;
        t.ForeColor = Theme.Text;
        t.BorderStyle = BorderStyle.None;
        t.Font = Theme.MonoBody;
        t.Text = body;
        f.Controls.Add(t);
        // a modal you cannot dismiss with Escape is a small daily irritation
        f.KeyPreview = true;
        f.KeyDown += delegate (object o2, KeyEventArgs k)
        {
            if (k.KeyCode == Keys.Escape)
            {
                f.Close();
            }
        };
        t.KeyDown += delegate (object o2, KeyEventArgs k)
        {
            if (k.KeyCode == Keys.Escape)
            {
                f.Close();
            }
        };
        f.ShowDialog(owner);
    }

    /// <summary>
    /// These dialogs are plain Forms, which without an explicit Icon show WinForms'
    /// generic default icon instead of Logzilla's, even though they're owned by a
    /// window that already carries the right one.
    /// </summary>
    private static Icon OwnerIcon(IWin32Window owner) => (owner as Form)?.Icon;
}
