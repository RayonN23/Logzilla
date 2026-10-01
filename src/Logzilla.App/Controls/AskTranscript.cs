using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Logzilla.AI.Providers;
using Logzilla.AI.Tools;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

internal enum AskBlockKind { User, Assistant, Tool, Notice, Error }

/// <summary>One thing in the conversation: a question, an answer, a tool call or a notice.</summary>
internal sealed class AskBlock
{
    public AskBlockKind Kind;
    public string Text = "";
    public SearchRequest Search;
    public bool Streaming;

    /// <summary>What the answer cost, as the provider reported it. Null for anything but an answer.</summary>
    public UsageTally Usage;

    // Layout, in content coordinates; rebuilt when the width or the text changes.
    internal int Top;
    internal int Height;
    internal readonly List<AskPiece> Pieces = new();
}

/// <summary>One laid-out run of text, a citation chip or a link.</summary>
internal sealed class AskPiece
{
    public Rectangle Rect;
    public string Text;
    public Font Font;
    public Color Color;
    public Color Back = Color.Empty;
    public int Cite;            // a line number to jump to; 0 for none
    public bool Apply;          // the "Apply" link on a search
    public bool Action;         // the call to action in an empty state
    public string Prompt;       // a suggested question
}

/// <summary>
/// The conversation, owner-drawn like the rest of the app. Answers are laid out from
/// <see cref="AskMarkup"/>: citations become chips that jump to their line, and a
/// search the assistant ran carries an Apply link that turns it into the user's own
/// filter. It follows the newest text while an answer streams in, unless the user
/// has scrolled up to read something earlier.
/// </summary>
internal sealed class AskTranscript : Control
{
    /// <summary>The scrollbar's width; the panel around the transcript lines its right edge up with it.</summary>
    internal const int ScrollWidth = 8;
    private const int PadX = AskPanel.GutterLeft;
    private const int PadRight = AskPanel.GutterRight;
    private static Font CachedTitleFont;

    /// <summary>An empty state's title: a step up from the text under it, never smaller.</summary>
    private static Font TitleFont => CachedTitleFont ??= new Font(Theme.UiBody.FontFamily, Theme.UiBody.Size * 1.2f, FontStyle.Bold, Theme.UiBody.Unit);
    private const int Gap = 12;
    private const TextFormatFlags Measure = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private readonly List<AskBlock> _blocks = new();
    private readonly ThinScrollBar _scroll = new(true);
    private readonly List<AskPiece> _empty = new();
    private int _contentHeight;
    private int _layoutWidth = -1;
    private AskPiece _hot;
    private string _emptyTitle = "Ask about this log";
    private string _emptyBody = "Questions are answered from the log itself, with the lines they rely on. Click a line number to jump to it.";
    private string _emptyAction;
    private string[] _suggestions =
    [
        "What went wrong in this log?",
        "What are the most common errors, and when did they start?",
        "Summarise what happened around the first error.",
    ];

    public event EventHandler<int> CitationClicked;
    public event EventHandler<SearchRequest> ApplyClicked;
    public event EventHandler<string> PromptPicked;
    public event EventHandler ActionClicked;

    /// <summary>Turns placeholders back into the real values for display. Identity by default.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string> Restore { get; set; } = s => s;

    public AskTranscript()
    {
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Conversation";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.BgChrome;
        _scroll.Dock = DockStyle.Right;
        _scroll.Width = Dpi.S(ScrollWidth);
        _scroll.BackColor = Theme.BgChrome;
        _scroll.ValueChanged += delegate { Invalidate(); };
        Controls.Add(_scroll);
    }

    public IReadOnlyList<AskBlock> Blocks => _blocks;

    /// <summary>What an empty conversation shows. A non-null <paramref name="action"/>
    /// replaces the suggested questions with a single call to action.</summary>
    public void SetEmptyState(string title, string body, string action, string[] suggestions = null)
    {
        _emptyTitle = title;
        _emptyBody = body;
        _emptyAction = action;
        if (suggestions != null)
        {
            _suggestions = suggestions;
        }
        _layoutWidth = -1;
        Invalidate();
    }

    public AskBlock Add(AskBlockKind kind, string text, SearchRequest search = null)
    {
        var stick = AtBottom();
        var b = new AskBlock { Kind = kind, Text = text ?? "", Search = search };
        _blocks.Add(b);
        Relayout(stick);
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        return b;
    }

    /// <summary>Adds a block above <paramref name="anchor"/>: a tool call goes above the
    /// answer that is still being written, so the evidence reads before the conclusion.</summary>
    public AskBlock AddBefore(AskBlock anchor, AskBlockKind kind, string text, SearchRequest search = null)
    {
        var stick = AtBottom();
        var b = new AskBlock { Kind = kind, Text = text ?? "", Search = search };
        var at = anchor == null ? -1 : _blocks.IndexOf(anchor);
        _blocks.Insert(at < 0 ? _blocks.Count : at, b);
        Relayout(stick);
        return b;
    }

    /// <summary>Appends streamed text to a block and lays out only what changed.</summary>
    public void Append(AskBlock block, string text)
    {
        ArgumentNullException.ThrowIfNull(block);
        var stick = AtBottom();
        block.Text += text;
        Relayout(stick, block);
    }

    public void Changed(AskBlock block)
    {
        var stick = AtBottom();
        Relayout(stick, block);
    }

    public void Remove(AskBlock block)
    {
        _blocks.Remove(block);
        Relayout(AtBottom());
    }

    public void Clear()
    {
        _blocks.Clear();
        _scroll.Value = 0;
        Relayout(false);
    }

    /// <summary>The whole conversation as plain text, real values restored.</summary>
    public string AllText()
    {
        return string.Join(Environment.NewLine + Environment.NewLine, _blocks
            .Where(b => b.Kind is AskBlockKind.User or AskBlockKind.Assistant)
            .Select(b => (b.Kind == AskBlockKind.User ? "You: " : "Assistant: ") + Restore(b.Text)));
    }

    // ── layout ───────────────────────────────────────────────────────

    private int ContentWidth => Math.Max(Dpi.S(80), ClientSize.Width - _scroll.Width - Dpi.S(PadX) - Dpi.S(PadRight));

    private bool AtBottom() => _scroll.Value >= _scroll.Maximum - _scroll.LargeChange - Dpi.S(4);

    /// <summary>
    /// Lays the blocks out from <paramref name="from"/> down, or all of them. A streamed
    /// answer changes only its own block (usually the last), and the blocks above it keep
    /// their places; laying out the whole conversation again for every token made answers
    /// stream slower the longer the conversation ran.
    /// </summary>
    private void Relayout(bool stickToBottom, AskBlock from = null)
    {
        var start = 0;
        if (from != null && _layoutWidth == ContentWidth)
        {
            start = Math.Max(0, _blocks.IndexOf(from));
        }
        _layoutWidth = ContentWidth;
        var y = start == 0 ? Dpi.S(Gap) : _blocks[start].Top;
        for (var k = start; k < _blocks.Count; k++)
        {
            var b = _blocks[k];
            b.Top = y;
            b.Pieces.Clear();
            b.Height = LayoutBlock(b, y);
            y += b.Height + Dpi.S(Gap);
        }
        _contentHeight = y;
        _scroll.LargeChange = Math.Max(1, ClientSize.Height);
        _scroll.Maximum = Math.Max(0, _contentHeight);
        if (stickToBottom)
        {
            _scroll.Value = _scroll.Maximum;
        }
        Invalidate();
    }

    private int LayoutBlock(AskBlock b, int top)
    {
        var left = Dpi.S(PadX);
        var width = _layoutWidth;
        switch (b.Kind)
        {
            case AskBlockKind.User:
            {
                var pad = Dpi.S(10);
                var bottom = Flow(b.Pieces, [Plain(Restore(b.Text))], left + pad, top + pad, width - pad * 2, Theme.Text);
                return bottom - top + pad;
            }
            case AskBlockKind.Tool:
            {
                var font = Theme.MonoSmall;
                var label = "› " + Restore(b.Text);
                var linkW = b.Search == null ? 0 : TextWidth("Apply", Theme.UiBody) + Dpi.S(12);
                var h = font.Height + Dpi.S(2);
                b.Pieces.Add(new AskPiece
                {
                    Rect = new Rectangle(left, top, width - linkW, h),
                    Text = label, Font = font, Color = Theme.TextDim,
                });
                if (b.Search != null)
                {
                    b.Pieces.Add(new AskPiece
                    {
                        Rect = new Rectangle(left + width - linkW + Dpi.S(12), top, linkW - Dpi.S(12), h),
                        Text = "Apply", Font = Theme.UiBody, Color = Theme.Accent, Apply = true,
                    });
                }
                return h;
            }
            case AskBlockKind.Notice:
            case AskBlockKind.Error:
            {
                var color = b.Kind == AskBlockKind.Error ? Theme.SevColor[5] : Theme.TextDim;
                return Flow(b.Pieces, [Plain(b.Text)], left, top, width, color) - top;
            }
            default:
            {
                var text = Restore(b.Text);
                if (text.Length == 0 && b.Streaming)
                {
                    text = "Thinking…";
                    return Flow(b.Pieces, [Plain(text)], left, top, width, Theme.TextDim) - top;
                }
                var y = top;
                var first = true;
                var prevCode = false;
                foreach (var p in AskMarkup.Parse(text))
                {
                    if (!first)
                    {
                        y += p.Code && prevCode ? 0 : Dpi.S(p.Heading ? 10 : 6);
                    }
                    first = false;
                    prevCode = p.Code;
                    var indent = 0;
                    if (p.Bullet)
                    {
                        indent = Dpi.S(16);
                        b.Pieces.Add(new AskPiece
                        {
                            Rect = new Rectangle(left, y, indent, Theme.UiBody.Height),
                            Text = p.Marker, Font = Theme.UiBody, Color = Theme.Text3,
                        });
                    }
                    if (p.Code)
                    {
                        var h = Theme.MonoSmall.Height + Dpi.S(4);
                        b.Pieces.Add(new AskPiece
                        {
                            Rect = new Rectangle(left, y, width, h),
                            Text = p.Runs.Count > 0 ? p.Runs[0].Text : "",
                            Font = Theme.MonoSmall, Color = Theme.TextBright, Back = Theme.BgControl,
                        });
                        y += h;
                        continue;
                    }
                    y = Flow(b.Pieces, p.Runs, left + indent, y, width - indent, Theme.Text);
                }
                var usage = b.Usage?.Line();
                if (!string.IsNullOrEmpty(usage))
                {
                    y += Dpi.S(6);
                    b.Pieces.Add(new AskPiece
                    {
                        Rect = new Rectangle(left, y, width, Theme.MonoTiny.Height + Dpi.S(2)),
                        Text = usage, Font = Theme.MonoTiny, Color = Theme.TextDim,
                    });
                    y += Theme.MonoTiny.Height + Dpi.S(2);
                }
                return Math.Max(y - top, Theme.UiBody.Height);
            }
        }
    }

    private static AskRun Plain(string text) => new(RunKind.Plain, text);

    /// <summary>Word-wraps runs into pieces from (x, y); returns the y below the last line.</summary>
    private static int Flow(List<AskPiece> pieces, IEnumerable<AskRun> runs, int x0, int y, int width, Color color)
    {
        var lineH = Math.Max(Theme.UiBody.Height, Theme.MonoSmall.Height) + Dpi.S(4);
        var x = x0;
        var right = x0 + width;
        foreach (var run in runs)
        {
            var font = run.Kind switch
            {
                RunKind.Bold => Theme.UiBodyBold,
                RunKind.Code => Theme.MonoSmall,
                RunKind.Cite => Theme.MonoSmall,
                _ => Theme.UiBody,
            };
            if (run.Kind == RunKind.Cite)
            {
                var w = TextWidth(run.Text, font) + Dpi.S(10);
                if (x + w > right && x > x0)
                {
                    x = x0;
                    y += lineH;
                }
                pieces.Add(new AskPiece
                {
                    Rect = new Rectangle(x, y + Dpi.S(1), w, lineH - Dpi.S(3)),
                    Text = run.Text, Font = font, Color = Theme.Accent, Back = Theme.AccentFaint, Cite = run.Line,
                });
                x += w + Dpi.S(3);
                continue;
            }
            foreach (var word in Words(run.Text))
            {
                var w = TextWidth(word, font);
                if (x + w > right && x > x0)
                {
                    if (word.Trim().Length == 0)
                    {
                        continue;   // a space never starts a line
                    }
                    x = x0;
                    y += lineH;
                }
                if (w > width)
                {
                    // One word wider than the line: a URL or a path. Break it anywhere.
                    foreach (var chunk in Chunks(word, font, width))
                    {
                        var cw = TextWidth(chunk, font);
                        if (x + cw > right && x > x0)
                        {
                            x = x0;
                            y += lineH;
                        }
                        AddText(pieces, chunk, font, run, color, x, y, cw, lineH);
                        x += cw;
                    }
                    continue;
                }
                AddText(pieces, word, font, run, color, x, y, w, lineH);
                x += w;
            }
        }
        return y + lineH;
    }

    private static void AddText(List<AskPiece> pieces, string text, Font font, AskRun run, Color color, int x, int y, int w, int lineH)
    {
        pieces.Add(new AskPiece
        {
            Rect = new Rectangle(x, y, w, lineH),
            Text = text,
            Font = font,
            Color = run.Kind == RunKind.Code ? Theme.TextBright : color,
            Back = run.Kind == RunKind.Code ? Theme.BgControl : Color.Empty,
        });
    }

    /// <summary>Words with the space after each kept, so wrapped text measures honestly.</summary>
    private static IEnumerable<string> Words(string s)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == ' ')
            {
                yield return s[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < s.Length)
        {
            yield return s[start..];
        }
    }

    private static IEnumerable<string> Chunks(string word, Font font, int width)
    {
        var start = 0;
        for (var i = 1; i <= word.Length; i++)
        {
            if (i == word.Length || TextWidth(word[start..(i + 1)], font) > width)
            {
                yield return word[start..i];
                start = i;
            }
        }
    }

    private static int TextWidth(string s, Font font) =>
        s.Length == 0 ? 0 : TextRenderer.MeasureText(s, font, new Size(int.MaxValue, int.MaxValue), Measure).Width;

    // ── painting ─────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_layoutWidth != ContentWidth)
        {
            Relayout(AtBottom());
        }
        if (_blocks.Count == 0)
        {
            PaintEmpty(g);
            return;
        }
        var dy = -_scroll.Value;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var b in _blocks)
        {
            if (b.Top + b.Height + dy < 0 || b.Top + dy > ClientSize.Height)
            {
                continue;
            }
            if (b.Kind == AskBlockKind.User)
            {
                var box = new Rectangle(Dpi.S(PadX) - Dpi.S(2), b.Top + dy, _layoutWidth + Dpi.S(4), b.Height);
                using var path = TextUtil.RoundedRect(box, Dpi.S(Theme.RadiusChip));
                using var brush = new SolidBrush(Theme.BgRaised);
                g.FillPath(brush, path);
            }
            foreach (var p in b.Pieces)
            {
                PaintPiece(g, p, dy);
            }
        }
    }

    private void PaintPiece(Graphics g, AskPiece p, int dy)
    {
        var r = p.Rect;
        r.Offset(0, dy);
        if (p.Back != Color.Empty)
        {
            using var brush = new SolidBrush(ReferenceEquals(p, _hot) ? Theme.AccentDim : p.Back);
            if (p.Cite > 0 || p.Prompt != null || p.Action)
            {
                using var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusSmall));
                g.FillPath(brush, path);
            }
            else
            {
                g.FillRectangle(brush, r);
            }
        }
        var color = ReferenceEquals(p, _hot) && (p.Apply || p.Cite > 0) ? Theme.Text : p.Color;
        var flags = Theme.TextFlagsEllipsis | TextFormatFlags.VerticalCenter;
        if (p.Cite > 0 || p.Action)
        {
            flags |= TextFormatFlags.HorizontalCenter;
        }
        else if (p.Prompt != null)
        {
            r.Inflate(-Dpi.S(12), 0);   // a suggestion reads from the left, like the text above it
        }
        TextRenderer.DrawText(g, p.Text, p.Font, r, color, flags);
    }

    private void PaintEmpty(Graphics g)
    {
        _empty.Clear();
        var left = Dpi.S(PadX);
        var y = Dpi.S(28);
        var width = ContentWidth;
        _empty.Add(new AskPiece
        {
            Rect = new Rectangle(left, y, width, TitleFont.Height + Dpi.S(4)),
            Text = _emptyTitle, Font = TitleFont, Color = Theme.Text,
        });
        y += TitleFont.Height + Dpi.S(8);
        y = Flow(_empty, [Plain(_emptyBody)], left, y, width, Theme.Text3) + Dpi.S(12);
        if (_emptyAction != null)
        {
            var w = TextWidth(_emptyAction, Theme.UiBody) + Dpi.S(24);
            _empty.Add(new AskPiece
            {
                Rect = new Rectangle(left, y, w, Dpi.S(Theme.ControlHeight)),
                Text = _emptyAction, Font = Theme.UiBody, Color = Theme.Accent, Back = Theme.AccentFaint, Action = true,
            });
        }
        else
        {
            foreach (var s in _suggestions)
            {
                var h = Dpi.S(Theme.ControlHeight);
                _empty.Add(new AskPiece
                {
                    Rect = new Rectangle(left, y, width, h),
                    Text = s, Font = Theme.UiBody, Color = Theme.Text2, Back = Theme.BgRaised, Prompt = s,
                });
                y += h + Dpi.S(6);
            }
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var p in _empty)
        {
            if (p.Prompt != null || p.Action)
            {
                PaintPiece(g, p, 0);
            }
            else
            {
                TextRenderer.DrawText(g, p.Text, p.Font, p.Rect, p.Color, Theme.TextFlags | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // ── input ────────────────────────────────────────────────────────

    private AskPiece HitTest(Point pt)
    {
        if (_blocks.Count == 0)
        {
            return _empty.FirstOrDefault(p => (p.Prompt != null || p.Action) && p.Rect.Contains(pt));
        }
        var content = new Point(pt.X, pt.Y + _scroll.Value);
        foreach (var b in _blocks)
        {
            if (content.Y < b.Top || content.Y > b.Top + b.Height)
            {
                continue;
            }
            foreach (var p in b.Pieces)
            {
                if ((p.Cite > 0 || p.Apply) && p.Rect.Contains(content))
                {
                    return p;
                }
            }
        }
        return null;
    }

    private AskBlock BlockAt(Point pt)
    {
        var y = pt.Y + _scroll.Value;
        return _blocks.FirstOrDefault(b => y >= b.Top && y <= b.Top + b.Height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (!ReferenceEquals(hit, _hot))
        {
            _hot = hit;
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = null;
        Cursor = Cursors.Default;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            var block = BlockAt(e.Location);
            var menu = new ContextMenuStrip();
            if (block != null && block.Kind is AskBlockKind.User or AskBlockKind.Assistant)
            {
                menu.Items.Add("Copy", null, delegate { Clipboard.SetText(Restore(block.Text)); });
            }
            if (_blocks.Count > 0)
            {
                menu.Items.Add("Copy the whole conversation", null, delegate { Clipboard.SetText(AllText()); });
            }
            if (menu.Items.Count > 0)
            {
                menu.Show(this, e.Location);
            }
            return;
        }
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        var hit = HitTest(e.Location);
        if (hit == null)
        {
            return;
        }
        if (hit.Cite > 0)
        {
            CitationClicked?.Invoke(this, hit.Cite);
        }
        else if (hit.Apply)
        {
            var block = BlockAt(e.Location);
            if (block?.Search != null)
            {
                ApplyClicked?.Invoke(this, block.Search);
            }
        }
        else if (hit.Prompt != null)
        {
            PromptPicked?.Invoke(this, hit.Prompt);
        }
        else if (hit.Action)
        {
            ActionClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll.Value -= e.Delta / 120 * (Theme.UiBody.Height + Dpi.S(4)) * 3;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout(AtBottom());
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new TranscriptAccessible(this);

    /// <summary>Lets a screen reader read the latest message, not just "Conversation".</summary>
    private sealed class TranscriptAccessible : ControlAccessibleObject
    {
        private readonly AskTranscript _owner;

        public TranscriptAccessible(AskTranscript owner) : base(owner) => _owner = owner;

        public override string Value
        {
            get
            {
                var last = _owner._blocks.LastOrDefault(b => b.Kind is AskBlockKind.Assistant or AskBlockKind.User);
                return last == null ? "" : _owner.Restore(last.Text);
            }
        }
    }
}
