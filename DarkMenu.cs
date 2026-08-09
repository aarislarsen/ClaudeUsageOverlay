using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeUsageOverlay;

/// <summary>
/// Paints the tray menu in the same palette as the overlay.
///
/// Windows Forms menus are drawn by the framework, not by the system, so a menu that is not
/// styled arrives as a bright white rectangle hanging off a dark panel. The point here is
/// not decoration: a control surface that looks like it belongs to a different program makes
/// the user check whether it does.
/// </summary>
internal static class DarkMenu
{
    // Same values as the brushes in App.xaml, opaque: menus cannot be translucent.
    public static readonly Color Background = Color.FromArgb(255, 16, 21, 26);
    public static readonly Color Border = Color.FromArgb(255, 38, 52, 62);
    public static readonly Color Text = Color.FromArgb(255, 214, 228, 236);
    public static readonly Color DimText = Color.FromArgb(255, 107, 127, 140);
    public static readonly Color Hover = Color.FromArgb(255, 27, 39, 48);
    public static readonly Color Accent = Color.FromArgb(255, 47, 151, 196);
    public static readonly Color Separator = Color.FromArgb(255, 30, 42, 51);

    private const int CornerRadius = 9;

    private static readonly Lazy<Font> LazyFont = new(CreateFont);

    public static Font Font => LazyFont.Value;

    /// <summary>The renderer to hand to a <see cref="ToolStrip"/>.</summary>
    public static ToolStripRenderer Renderer { get; } = new DarkRenderer();

    /// <summary>
    /// Applies the palette, the font, the spacing, and the rounded outline to a menu and to
    /// every submenu hanging off it.
    /// </summary>
    public static void Apply(ToolStripDropDown menu)
    {
        menu.BackColor = Background;
        menu.ForeColor = Text;
        menu.Font = Font;
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = Renderer;
        menu.Padding = new Padding(4, 5, 4, 5);
        menu.DropShadowEnabled = true;

        RoundCorners(menu);

        foreach (ToolStripItem item in menu.Items)
        {
            ApplyToItem(item);
        }
    }

    private static void ApplyToItem(ToolStripItem item)
    {
        item.BackColor = Background;
        item.ForeColor = Text;

        if (item is ToolStripSeparator)
        {
            return;
        }

        item.Padding = new Padding(2, 3, 2, 3);

        if (item is ToolStripMenuItem menuItem && menuItem.HasDropDownItems)
        {
            Apply(menuItem.DropDown);
        }
    }

    /// <summary>
    /// Gives the menu the same corner radius as the overlay panel. The region has to be
    /// rebuilt whenever the menu is sized, which happens the first time it opens.
    /// </summary>
    private static void RoundCorners(ToolStripDropDown menu)
    {
        void Update()
        {
            if (menu.Width <= 0 || menu.Height <= 0)
            {
                return;
            }

            var previous = menu.Region;

            using var path = RoundedRectangle(new Rectangle(0, 0, menu.Width, menu.Height), CornerRadius);
            menu.Region = new Region(path);

            previous?.Dispose();
        }

        menu.SizeChanged += (_, _) => Update();
        menu.Opening += (_, _) => Update();
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();

        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static Font CreateFont()
    {
        // Same family as the panel, so the menu reads as part of the same object.
        foreach (var name in new[] { "Cascadia Mono", "Consolas", "Segoe UI" })
        {
            var candidate = new Font(name, 9f, FontStyle.Regular, GraphicsUnit.Point);
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            candidate.Dispose();
        }

        return new Font(SystemFonts.MenuFont?.FontFamily ?? FontFamily.GenericSansSerif, 9f);
    }

    // -- renderer --------------------------------------------------------------

    private sealed class DarkColours : ProfessionalColorTable
    {
        public DarkColours()
        {
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => Background;

        public override Color ImageMarginGradientBegin => Background;

        public override Color ImageMarginGradientMiddle => Background;

        public override Color ImageMarginGradientEnd => Background;

        public override Color MenuBorder => Border;

        public override Color MenuItemBorder => Hover;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color SeparatorDark => Separator;

        public override Color SeparatorLight => Separator;

        public override Color CheckBackground => Background;

        public override Color CheckSelectedBackground => Hover;

        public override Color CheckPressedBackground => Hover;
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColours())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Background);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is not ToolStripDropDown)
            {
                base.OnRenderToolStripBorder(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var path = RoundedRectangle(bounds, CornerRadius);
            using var pen = new Pen(Border);

            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = new Rectangle(2, 0, e.Item.Width - 5, e.Item.Height - 1);

            using (var fill = new SolidBrush(Hover))
            using (var path = RoundedRectangle(bounds, 5))
            {
                e.Graphics.FillPath(fill, path);
            }

            // A short accent bar on the leading edge, echoing the pip on the panel.
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, bounds.X, bounds.Y + 4, 2, Math.Max(bounds.Height - 8, 2));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : DimText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            if (e.Vertical)
            {
                base.OnRenderSeparator(e);
                return;
            }

            var y = e.Item.Height / 2;
            using var pen = new Pen(Separator);

            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Selected == true ? Text : DimText;
            base.OnRenderArrow(e);
        }

        /// <summary>Draws the tick for checked items in the accent colour.</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var box = e.ImageRectangle;
            if (box.Width <= 0 || box.Height <= 0)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var size = Math.Min(box.Width, box.Height) - 6;
            var left = box.X + (box.Width - size) / 2f;
            var top = box.Y + (box.Height - size) / 2f;

            using var pen = new Pen(Accent, 1.7f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(left, top + size * 0.55f),
                new PointF(left + size * 0.38f, top + size * 0.9f),
                new PointF(left + size, top + size * 0.12f)
            });
        }
    }
}
