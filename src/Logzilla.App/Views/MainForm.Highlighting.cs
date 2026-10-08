using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Views;

// The Highlighting tab: rules that colour matching text wherever it appears, without
// filtering anything out. Rules are added right on the tab — type and press Enter — and
// each one has a right-click menu to switch it, recolour it or remove it.

internal sealed partial class MainForm
{
    private Panel _hlBar;
    private TextBox _hlBox;
    private ToggleChip _hlRegex;

    /// <summary>The bar at the top of the Highlighting tab where a rule is typed in.</summary>
    private Panel BuildHighlightBar()
    {
        var pad = Dpi.S(AskPanel.GutterLeft);
        _hlBar = new Panel
        {
            Name = "HighlightBar", Dock = DockStyle.Top, Height = Dpi.S(Theme.ControlHeight) + Dpi.S(14),
            BackColor = Theme.BgChrome, Padding = new Padding(pad, Dpi.S(4), Dpi.S(AskPanel.GutterRight), Dpi.S(10)),
            Visible = false,
        };
        var add = new FlatButton { Text = "Add", Name = "HighlightAdd", Dock = DockStyle.Right, AccessibleName = "Add highlight" };
        add.FitTo(52, Theme.ControlHeight, 12);
        add.Click += delegate { AddHighlightFromBar(); };
        _hlRegex = new ToggleChip { Label = ".*", BorderlessOff = true, Dock = DockStyle.Right, Name = "HighlightRegex", BackColor = Theme.BgData, AccessibleName = "Treat as a regular expression" };
        _hlRegex.Fit(28);
        _hlRegex.Click2 += delegate { _hlRegex.On = !_hlRegex.On; _hlRegex.Invalidate(); ValidateHighlightBox(); };
        new ToolTip().SetToolTip(_hlRegex, "Treat the text as a regular expression");
        _hlBox = new TextBox
        {
            Name = "HighlightInput", Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Theme.BgData, ForeColor = Theme.Text, Font = Theme.UiBody,
            PlaceholderText = "Text to highlight, then Enter", AccessibleName = "Text to highlight",
        };
        _hlBox.KeyDown += delegate (object _, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddHighlightFromBar();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _hlBox.Text = "";
            }
        };
        _hlBox.TextChanged += delegate { ValidateHighlightBox(); };
        // The box sits in a padded host of its own so its text lines up with the field's
        // inset, the way the Find box does; the .* chip keeps the field's full height.
        var field = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgData, Padding = new Padding(Dpi.S(8), 0, Dpi.S(2), 0) };
        var boxHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgData, Padding = new Padding(0, Dpi.S(7), Dpi.S(4), 0) };
        boxHost.Controls.Add(_hlBox);
        field.Controls.Add(boxHost);
        field.Controls.Add(_hlRegex);
        _hlBar.Controls.Add(field);
        _hlBar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = Dpi.S(8), BackColor = Theme.BgChrome });
        _hlBar.Controls.Add(add);
        return _hlBar;
    }

    /// <summary>Colours the box red while it holds a regular expression that will not compile.</summary>
    private bool ValidateHighlightBox()
    {
        var ok = !_hlRegex.On || IsValidPattern(_hlBox.Text.Trim());
        _hlBox.ForeColor = ok ? Theme.Text : Theme.SevColor[5];
        return ok;
    }

    private static bool IsValidPattern(string pattern)
    {
        if (pattern.Length == 0)
        {
            return true;
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void AddHighlightFromBar()
    {
        var text = _hlBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        if (!ValidateHighlightBox())
        {
            _st3.Text = "That is not a valid regular expression; turn off .* to highlight it as plain text.";
            return;
        }
        var existing = _rules.Find(r => r.Pattern == text && r.IsRegex == _hlRegex.On);
        if (existing != null)
        {
            existing.On = true;     // already there: make sure it shows rather than add it twice
        }
        else
        {
            _rules.Add(new HighlightRule { Pattern = text, IsRegex = _hlRegex.On, C = Palette.Pick(_rules.Count + 3), On = true });
        }
        _hlBox.Text = "";
        _view.Invalidate();
        FillSide();
    }

    /// <summary>The right-click menu on one rule.</summary>
    private void ShowRuleMenu(int index)
    {
        if (index < 0 || index >= _rules.Count)
        {
            return;
        }
        var rule = _rules[index];
        var cm = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
            BackColor = Theme.BgChrome, ForeColor = Theme.Text, Font = Theme.UiBody,
        };
        cm.Items.Add(Mi(rule.On ? "Turn off" : "Turn on", Keys.None, delegate { rule.On = !rule.On; RulesChanged(); }));
        cm.Items.Add(Mi("Change colour...", Keys.None, delegate { RecolourRule(rule); }));
        var asRegex = Mi("Match as a regular expression", Keys.None, delegate
        {
            if (!rule.IsRegex && !IsValidPattern(rule.Pattern))
            {
                Info("\"" + rule.Pattern + "\" is not a valid regular expression, so it stays plain text.");
                return;
            }
            rule.IsRegex = !rule.IsRegex;
            RulesChanged();
        });
        asRegex.Checked = rule.IsRegex;
        cm.Items.Add(asRegex);
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add(Mi("Remove", Keys.None, delegate { _rules.Remove(rule); RulesChanged(); }));
        foreach (ToolStripItem it in cm.Items) { it.BackColor = Theme.BgChrome; it.ForeColor = Theme.Text; }
        cm.Show(Cursor.Position);
    }

    private void RecolourRule(HighlightRule rule)
    {
        using var cd = new ColorDialog { Color = rule.C };
        if (cd.ShowDialog(this) == DialogResult.OK)
        {
            rule.C = cd.Color;
            RulesChanged();
        }
    }

    private void RulesChanged()
    {
        _view.Invalidate();
        FillSide();
    }
}
