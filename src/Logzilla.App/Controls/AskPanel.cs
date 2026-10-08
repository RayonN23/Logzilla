using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

/// <summary>
/// The Ask tab. Four bands, nothing else: which model is answering, what it can see,
/// the conversation, and the box to type in. Connecting providers lives in a dialog
/// of its own, so none of that crowds the tab.
///
/// Every band shares one left and one right edge — the same insets as the other
/// inspector panels — so text lines up from the header down to the input box.
/// </summary>
internal sealed class AskPanel : Control
{
    /// <summary>Where text starts, as in every other inspector panel (PanelList.LeftPad).</summary>
    internal const int GutterLeft = 12;

    /// <summary>Where text ends, as in every other inspector panel (PanelList.RightPad).</summary>
    internal const int GutterRight = 14;

    private const int MaxInputLines = 6;
    private const int ModelPadX = 10;
    private const int MorePadX = 8;

    private readonly FlatButton _model;
    private readonly FlatButton _newChat;
    private readonly FlatButton _more;
    private readonly EdgeLabel _scope;
    private readonly EdgeLabel _status;
    private readonly InputField _field;
    private readonly FlatButton _send;
    private readonly Panel _composer;
    private bool _busy;

    public event EventHandler<string> SendRequested;
    public event EventHandler StopRequested;
    public event EventHandler NewChatRequested;
    public event EventHandler ModelMenuRequested;
    public event EventHandler MoreMenuRequested;
    public event EventHandler ScopeToggled;

    public AskPanel()
    {
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "AI Chat";
        BackColor = Theme.BgChrome;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        Transcript = new AskTranscript { Dock = DockStyle.Fill };
        var right = GutterRight + AskTranscript.ScrollWidth;

        // ── header: model on the left, new chat and the menu on the right ──
        // Ghost buttons draw their text inset by their own padding, so the bar's
        // padding is the gutter less that inset: the text, not the button, lines up.
        var header = new Panel
        {
            Dock = DockStyle.Top, Height = Dpi.S(40), BackColor = Theme.BgChrome,
            Padding = new Padding(Dpi.S(GutterLeft - ModelPadX), Dpi.S(5), Dpi.S(right - MorePadX), Dpi.S(5)),
        };
        _model = new FlatButton { Text = "Choose a model  ▾", Style = FlatButton.Look.Ghost, Dock = DockStyle.Left, AccessibleName = "Model answering your questions" };
        _model.FitTo(120, Theme.ControlHeight, ModelPadX);
        _model.Click += delegate { ModelMenuRequested?.Invoke(this, EventArgs.Empty); };
        _more = new FlatButton
        {
            Text = "•••", Style = FlatButton.Look.Ghost, Dock = DockStyle.Right, ForeColor = Theme.Text2,
            AccessibleName = "More: usage, what was sent, AI settings",
        };
        _more.FitTo(28, Theme.ControlHeight, MorePadX);
        _more.Click += delegate { MoreMenuRequested?.Invoke(this, EventArgs.Empty); };
        new ToolTip().SetToolTip(_more, "Usage, what was sent, AI settings");
        _newChat = new FlatButton { Text = "New chat", Style = FlatButton.Look.Ghost, Dock = DockStyle.Right, AccessibleName = "Start a new conversation" };
        _newChat.FitTo(60, Theme.ControlHeight, ModelPadX);
        _newChat.Click += delegate { NewChatRequested?.Invoke(this, EventArgs.Empty); };
        new ToolTip().SetToolTip(_newChat, "Start a new conversation");
        header.Controls.Add(_model);
        header.Controls.Add(_newChat);
        header.Controls.Add(_more);

        // ── scope and status: one line, one font, two ends ──
        var scopeBar = new Panel
        {
            Dock = DockStyle.Top, Height = Dpi.S(24), BackColor = Theme.BgChrome,
            Padding = new Padding(Dpi.S(GutterLeft), 0, Dpi.S(right), Dpi.S(4)),
        };
        _scope = new EdgeLabel
        {
            Dock = DockStyle.Left, Font = Theme.UiBody,
            ForeColor = Theme.Text3, Cursor = Cursors.Hand,
            AccessibleRole = AccessibleRole.PushButton,
            AccessibleName = "What the assistant can see. Click to switch between the whole log and what your filters show.",
        };
        _scope.Click += delegate { ScopeToggled?.Invoke(this, EventArgs.Empty); };
        _scope.MouseEnter += delegate { _scope.ForeColor = Theme.Text; };
        _scope.MouseLeave += delegate { _scope.ForeColor = Theme.Text3; };
        new ToolTip().SetToolTip(_scope, "Click to switch between the whole log and only what your filters show");
        _status = new EdgeLabel { Dock = DockStyle.Fill, Font = Theme.UiBody, ForeColor = Theme.TextDim, AlignRight = true };
        scopeBar.Controls.Add(_status);
        scopeBar.Controls.Add(_scope);

        // ── composer: the field and Send share a top, a bottom and the gutters ──
        _composer = new Panel
        {
            Dock = DockStyle.Bottom, BackColor = Theme.BgChrome,
            Padding = new Padding(Dpi.S(GutterLeft), Dpi.S(10), Dpi.S(right), Dpi.S(10)),
        };
        _send = new FlatButton { Text = "Send", Dock = DockStyle.Right, AccessibleName = "Send" };
        _send.FitTo(64, Theme.ControlHeight, 14);
        _send.Click += delegate { SendOrStop(); };
        _field = new InputField { Dock = DockStyle.Fill };
        _field.Box.KeyDown += InputKeyDown;
        _field.Box.TextChanged += delegate { FitComposer(); };
        _composer.Controls.Add(_field);
        _composer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = Dpi.S(8), BackColor = Theme.BgChrome });
        _composer.Controls.Add(_send);

        Controls.Add(Transcript);
        Controls.Add(Rule(DockStyle.Bottom));
        Controls.Add(_composer);
        Controls.Add(Rule(DockStyle.Top));
        Controls.Add(scopeBar);
        Controls.Add(header);
        SetScope("Looking at: the whole log");
        FitComposer();
    }

    public AskTranscript Transcript { get; }

    /// <summary>The model picker's button, so its menu can open under it.</summary>
    public Control ModelButton => _model;

    public Control MoreButton => _more;

    public void FocusInput() => _field.Box.Focus();

    public void SetModel(string text)
    {
        _model.Text = text + "  ▾";
        _model.FitTo(120, Theme.ControlHeight, ModelPadX);
    }

    public void SetScope(string text)
    {
        _scope.Text = text;
        _scope.Width = TextRenderer.MeasureText(text, _scope.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + 1;
        _scope.Invalidate();
    }

    /// <summary>The quiet line on the right: privacy, and what this conversation has used.</summary>
    public void SetStatus(string text, bool warn)
    {
        _status.Text = text;
        _status.ForeColor = warn ? Theme.SevColor[4] : Theme.TextDim;
        _status.Invalidate();
    }

    /// <summary>While an answer is coming in, Send becomes Stop.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Text = busy ? "Stop" : "Send";
        _send.AccessibleName = _send.Text;
        _send.Invalidate();
    }

    /// <summary>Greys out the composer when there is nothing to send to.</summary>
    public void SetCanAsk(bool canAsk)
    {
        _field.Box.Enabled = canAsk;
        _field.Invalidate();
        _send.Enabled = canAsk;
        _model.Enabled = canAsk;
        _newChat.Enabled = canAsk;
    }

    private void SendOrStop()
    {
        if (_busy)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        var text = _field.Box.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        _field.Box.Clear();
        SendRequested?.Invoke(this, text);
    }

    private void InputKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter is a new line, the way chat boxes everywhere behave.
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            SendOrStop();
        }
        else if (e.KeyCode == Keys.Escape && _busy)
        {
            e.SuppressKeyPress = true;
            StopRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The box grows with what is typed, up to a few lines, then scrolls.</summary>
    private void FitComposer()
    {
        var box = _field.Box;
        var lines = Math.Clamp(box.GetLineFromCharIndex(box.TextLength) + 1, 2, MaxInputLines);
        box.ScrollBars = lines >= MaxInputLines ? ScrollBars.Vertical : ScrollBars.None;
        _composer.Height = lines * box.Font.Height + InputField.InsetY * 2 + _composer.Padding.Vertical;
    }

    private static Panel Rule(DockStyle dock) => new() { Dock = dock, Height = 1, BackColor = Theme.Line };

    /// <summary>
    /// One line of text drawn flush to its edge. A WinForms Label pads its text by a few
    /// pixels of its own, which is exactly enough to put it off the gutter everything
    /// else in the panel lines up on.
    /// </summary>
    internal sealed class EdgeLabel : Control
    {
        public EdgeLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgChrome;
            TabStop = false;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool AlignRight { get; set; }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (AlignRight ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
        }
    }

    /// <summary>
    /// A text box in a rounded field, drawn in one piece: the fill and the outline are
    /// the same shape, so no square corner shows outside the curve, and the outline
    /// follows focus as it moves in and out.
    /// </summary>
    internal sealed class InputField : Control
    {
        internal static int InsetY => Dpi.S(7);
        private static int InsetX => Dpi.S(10);

        public InputField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgChrome;
            Box = new TextBox
            {
                Multiline = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.BgData,
                ForeColor = Theme.Text,
                Font = Theme.UiBody,
                PlaceholderText = "Ask about this log…",
                AccessibleName = "Your question",
                Name = "AskInput",
                WordWrap = true,
            };
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
        }

        public TextBox Box { get; }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            Box.Bounds = new Rectangle(InsetX, InsetY, Math.Max(10, Width - InsetX * 2), Math.Max(10, Height - InsetY * 2));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusField));
            using (var fill = new SolidBrush(Theme.BgData))
            {
                g.FillPath(fill, path);
            }
            using var pen = new Pen(Box.Focused ? Theme.AccentEdge : Theme.LineControlStrong);
            g.DrawPath(pen, path);
        }
    }
}
