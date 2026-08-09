using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using ClaudeUsageOverlay.Services;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageOverlay;

/// <summary>
/// The tray icon and its menu: the app's only control surface.
///
/// The menu is flat, short, and every item does exactly one thing the moment it is clicked.
/// Nothing asks for confirmation, because nothing here destroys anything the user cannot
/// recreate in a second.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _showItem;
    private readonly WinForms.ToolStripMenuItem _lockItem;
    private readonly Dictionary<ScreenAnchor, WinForms.ToolStripMenuItem> _anchorItems = new();

    private Icon? _currentIcon;

    public TrayIconHost(AppSettings settings)
    {
        _settings = settings;

        // The image margin stays on: it is where the tick for the checkable items is drawn,
        // and a check the user cannot see is a state the user cannot see.
        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = true
        };

        menu.Items.Add(Item("Refresh now", () => RefreshRequested?.Invoke()));
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _showItem = new WinForms.ToolStripMenuItem("Show overlay")
        {
            CheckOnClick = true,
            Checked = settings.ShowOverlay
        };
        _showItem.Click += (_, _) => VisibilityToggled?.Invoke(_showItem.Checked);
        menu.Items.Add(_showItem);

        var position = new WinForms.ToolStripMenuItem("Position");
        foreach (var anchor in Enum.GetValues<ScreenAnchor>())
        {
            var item = new WinForms.ToolStripMenuItem(Describe(anchor))
            {
                Checked = settings.Anchor == anchor
            };
            item.Click += (_, _) => AnchorChosen?.Invoke(anchor);

            _anchorItems[anchor] = item;
            position.DropDownItems.Add(item);

            // Break the eight anchors into the three rows they describe, so the menu has the
            // same shape as the screen it is talking about.
            if (anchor is ScreenAnchor.TopRight or ScreenAnchor.MiddleRight)
            {
                position.DropDownItems.Add(new WinForms.ToolStripSeparator());
            }
        }

        position.DropDownItems.Add(new WinForms.ToolStripSeparator());

        _lockItem = new WinForms.ToolStripMenuItem("Locked (click passes through)")
        {
            CheckOnClick = true,
            Checked = settings.ClickThrough
        };
        _lockItem.Click += (_, _) => LockToggled?.Invoke(_lockItem.Checked);
        position.DropDownItems.Add(_lockItem);

        menu.Items.Add(position);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        menu.Items.Add(Item("Sign in with browser", () => SignInRequested?.Invoke()));
        menu.Items.Add(Item("Open settings file", () => OpenFile(AppSettings.FilePath)));
        menu.Items.Add(Item("Open log file", () => OpenFile(Log.FilePath)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Item("Quit", () => QuitRequested?.Invoke()));

        DarkMenu.Apply(menu);

        _icon = new WinForms.NotifyIcon
        {
            Text = "Claude usage",
            ContextMenuStrip = menu,
            Visible = true,
            Icon = BuildIcon(null, Severity.Calm)
        };

        _currentIcon = _icon.Icon;

        // A left double-click is the fastest possible "tell me now".
        _icon.DoubleClick += (_, _) => RefreshRequested?.Invoke();
    }

    public Action? RefreshRequested { get; set; }

    public Action? SignInRequested { get; set; }

    public Action? QuitRequested { get; set; }

    public Action<bool>? VisibilityToggled { get; set; }

    public Action<bool>? LockToggled { get; set; }

    public Action<ScreenAnchor>? AnchorChosen { get; set; }

    /// <summary>Keeps the menu's check marks honest when settings change elsewhere.</summary>
    public void SyncMenu()
    {
        _showItem.Checked = _settings.ShowOverlay;
        _lockItem.Checked = _settings.ClickThrough;

        foreach (var (anchor, item) in _anchorItems)
        {
            item.Checked = _settings.Anchor == anchor;
        }
    }

    /// <summary>
    /// Redraws the tray icon as a ring filled to the session percentage, so the number is
    /// readable at a glance even when the overlay is hidden.
    /// </summary>
    public void Update(double? sessionPercent, Severity severity, string tooltip)
    {
        var next = BuildIcon(sessionPercent, severity);
        var previous = _currentIcon;

        _icon.Icon = next;
        _currentIcon = next;

        // NotifyIcon.Text is capped by the shell; keep it short rather than risk a throw.
        _icon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;

        previous?.Dispose();
    }

    private static WinForms.ToolStripMenuItem Item(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static string Describe(ScreenAnchor anchor) => anchor switch
    {
        ScreenAnchor.TopLeft => "Top left",
        ScreenAnchor.TopCentre => "Top centre",
        ScreenAnchor.TopRight => "Top right",
        ScreenAnchor.MiddleLeft => "Middle left",
        ScreenAnchor.MiddleRight => "Middle right",
        ScreenAnchor.BottomLeft => "Bottom left",
        ScreenAnchor.BottomCentre => "Bottom centre",
        _ => "Bottom right"
    };

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot open {path}: {ex.Message}");
        }
    }

    /// <summary>Draws the tray glyph. No text: at 16 px, text is a smudge.</summary>
    private static Icon BuildIcon(double? percent, Severity severity)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(4f, 4f, size - 8f, size - 8f);

            using var track = new Pen(Color.FromArgb(120, 90, 110, 125), 4f);
            g.DrawEllipse(track, rect);

            if (percent is > 0)
            {
                // Same four bands as the panel, so the ring and the meter never disagree.
                var colour = severity switch
                {
                    Severity.Critical => Color.FromArgb(255, 255, 80, 101),
                    Severity.Warning => Color.FromArgb(255, 242, 135, 46),
                    Severity.Normal => Color.FromArgb(255, 63, 191, 116),
                    _ => Color.FromArgb(255, 47, 151, 196)
                };

                using var arc = new Pen(colour, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var sweep = (float)(Math.Clamp(percent.Value, 0, 100) / 100.0 * 360.0);
                g.DrawArc(arc, rect, -90f, Math.Max(sweep, 6f));
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives the HICON being destroyed below.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            Native.ReleaseIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
