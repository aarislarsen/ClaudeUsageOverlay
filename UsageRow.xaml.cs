using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeUsageOverlay;

/// <summary>
/// Displays one usage window: its name, its percentage, a segmented meter, and when it
/// resets. Values are written in place; nothing moves, animates, or blinks.
/// </summary>
public partial class UsageRow : UserControl
{
    private const int CellCount = 24;

    private readonly Rectangle[] _cells = new Rectangle[CellCount];

    private Brush _calm = Brushes.SteelBlue;
    private Brush _normal = Brushes.MediumSeaGreen;
    private Brush _warning = Brushes.Orange;
    private Brush _critical = Brushes.Red;
    private Brush _track = Brushes.DimGray;

    public UsageRow()
    {
        InitializeComponent();
        ResolveBrushes();
        BuildCells();
    }

    /// <summary>Identity of the window this row is bound to, so rows keep a stable order.</summary>
    public string Key { get; set; } = "";

    private void BuildCells()
    {
        for (var i = 0; i < CellCount; i++)
        {
            var cell = new Rectangle
            {
                Margin = new Thickness(0, 0, 2, 0),
                RadiusX = 0.5,
                RadiusY = 0.5,
                Fill = _track
            };

            _cells[i] = cell;
            Cells.Items.Add(cell);
        }
    }

    private void ResolveBrushes()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        _calm = (Brush)resources["CalmBrush"];
        _normal = (Brush)resources["NormalBrush"];
        _warning = (Brush)resources["WarningBrush"];
        _critical = (Brush)resources["CriticalBrush"];
        _track = (Brush)resources["TrackBrush"];
    }

    /// <summary>Writes a reading into the row.</summary>
    public void Update(UsageWindow window, DateTimeOffset now)
    {
        Key = window.Key;
        LabelText.Text = window.Label;

        // No decimals: a tenth of a percent is not a fact anyone acts on, and a changing
        // decimal place would pull the eye for no reason.
        PercentText.Text = ((int)Math.Round(window.Percent)).ToString(CultureInfo.InvariantCulture) + "%";

        var accent = window.Severity switch
        {
            Severity.Critical => _critical,
            Severity.Warning => _warning,
            Severity.Normal => _normal,
            _ => _calm
        };

        PercentText.Foreground = accent;
        PaintMeter(window.Percent, accent);
        ResetText.Text = FormatReset(window.ResetsAt, now);
    }

    /// <summary>Shows the row without data, before the first reading arrives.</summary>
    public void ShowPlaceholder(string label)
    {
        LabelText.Text = label;
        PercentText.Text = "--%";
        PercentText.Foreground = (Brush)Application.Current.Resources["DimTextBrush"];
        PaintMeter(0, _track);
        ResetText.Text = "Reset --:--";
    }

    /// <summary>Refreshes only the countdown, so the clock stays honest between polls.</summary>
    public void UpdateCountdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        ResetText.Text = FormatReset(resetsAt, now);
    }

    private void PaintMeter(double percent, Brush accent)
    {
        var lit = (int)Math.Round(percent / 100.0 * CellCount, MidpointRounding.AwayFromZero);

        // Any usage at all lights the first cell: zero and "almost zero" should not look
        // identical when one of them means work has started.
        if (percent > 0 && lit < 1)
        {
            lit = 1;
        }

        lit = Math.Clamp(lit, 0, CellCount);

        for (var i = 0; i < CellCount; i++)
        {
            _cells[i].Fill = i < lit ? accent : _track;
            _cells[i].Opacity = i < lit ? 1.0 : 0.55;
        }
    }

    /// <summary>
    /// Both the wall-clock reset time and the time remaining. Showing both means the user
    /// never has to do arithmetic, and never has to remember what time it is now.
    /// </summary>
    private static string FormatReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return "Reset --:--";
        }

        var local = resetsAt.Value.ToLocalTime();
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);

        var remaining = local - now;
        if (remaining <= TimeSpan.Zero)
        {
            return $"Reset {clock}  ·  due now";
        }

        var relative = remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m"
                : $"{(int)remaining.TotalMinutes}m";

        var day = local.Date == now.Date
            ? clock
            : $"{local:ddd} {clock}";

        return $"Reset {day}  ·  in {relative}";
    }

    /// <summary>
    /// Draws the rule that separates this column from the one on its left. Called for every
    /// column except the first.
    /// </summary>
    public void ShowDivider()
    {
        Divider.BorderThickness = new Thickness(1, 0, 0, 0);
        Divider.Padding = new Thickness(15, 0, 0, 0);
        Margin = new Thickness(15, 0, 0, 0);
    }
}
