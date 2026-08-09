using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeUsageOverlay.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageOverlay;

/// <summary>
/// The overlay itself.
///
/// Design commitments, in Raskin's terms:
///   * No modes. The panel shows the same facts in the same places at all times. There is
///     no hover state, no expanded state, and no state that has to be exited.
///   * Nothing is hidden behind an interaction. Everything the app knows is on screen.
///   * The interface never interrupts. No dialogs, no toasts, no sounds, no animation.
///   * It cannot be in the way: by default it is transparent to the mouse and never takes
///     focus, so it can never steal a keystroke or a click.
///   * Layout is habituating. Rows keep their order and their height for the life of the
///     process, so the eye learns one place per fact and never has to search.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<UsageRow> _rows = new();
    private readonly Dictionary<string, UsageWindow> _lastValues = new();

    private ConnectionState _state = ConnectionState.Starting;
    private DateTimeOffset? _lastSuccess;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        PanelScale.ScaleX = _settings.Scale;
        PanelScale.ScaleY = _settings.Scale;
        Opacity = _settings.Opacity;

        // Two placeholder rows exist from the first frame, so the panel does not change
        // shape when the first reading lands.
        AddRow("session:5h", "Session");
        AddRow("weekly_all:7d", "All models");

        SetState(ConnectionState.Starting);
        FooterText.Text = "Starting";

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        LocationChanged += OnLocationChanged;
        MouseLeftButtonDown += OnMouseLeftButtonDown;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Native.ApplyOverlayStyles(this, _settings.ClickThrough);
        Reposition();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Placement runs before the first layout pass has a size to work with, so run it
        // once more now that the panel has real dimensions.
        Reposition();

        Log.Info($"rendered: visible = {IsVisible}, opacity = {Opacity:0.00}, topmost = {Topmost}");
    }

    /// <summary>Re-applies the click-through style after the setting changes.</summary>
    public void ApplyInteractionMode()
    {
        Native.ApplyOverlayStyles(this, _settings.ClickThrough);
    }

    // -- data ------------------------------------------------------------------

    /// <summary>Writes a fresh reading into the panel.</summary>
    public void Apply(UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;

        foreach (var window in snapshot.Windows)
        {
            _lastValues[window.Key] = window;

            var row = _rows.FirstOrDefault(r => r.Key == window.Key);
            if (row is null)
            {
                // A window the account did not have before (Opus weekly, for example).
                // Once added, a row is never removed: a panel that changes height would
                // break the muscle memory of everything around it.
                row = AddRow(window.Key, window.Label);
            }

            row.Update(window, now);
        }

        _lastSuccess = snapshot.FetchedAt;
        SetState(ConnectionState.Live);
        UpdateFooter(now);
        UpdatePipColour();
    }

    /// <summary>Keeps the last good numbers on screen and marks them as no longer fresh.</summary>
    public void MarkStale()
    {
        if (_state != ConnectionState.SignInNeeded)
        {
            SetState(_lastSuccess is null ? ConnectionState.Starting : ConnectionState.Stale);
        }

        UpdateFooter(DateTimeOffset.Now);
    }

    public void MarkSignInNeeded()
    {
        SetState(ConnectionState.SignInNeeded);
        UpdateFooter(DateTimeOffset.Now);
    }

    /// <summary>
    /// Re-renders only the time-remaining text. Called on a short timer so the countdown
    /// stays truthful without touching the network.
    /// </summary>
    public void TickCountdown()
    {
        var now = DateTimeOffset.Now;

        foreach (var row in _rows)
        {
            if (_lastValues.TryGetValue(row.Key, out var window))
            {
                row.UpdateCountdown(window.ResetsAt, now);
            }
        }

        UpdateFooter(now);
    }

    public string TrayTooltip()
    {
        if (_lastValues.Count == 0)
        {
            return "Claude usage: waiting for data";
        }

        var parts = _rows
            .Where(r => _lastValues.ContainsKey(r.Key))
            .Select(r =>
            {
                var window = _lastValues[r.Key];
                var name = window.Label switch
                {
                    "Session" => "5h",
                    "All models" => "7d",
                    _ => window.Label.ToLowerInvariant()
                };
                return $"{name} {(int)Math.Round(window.Percent)}%";
            });

        return "Claude usage  " + string.Join("   ", parts);
    }

    /// <summary>Session percentage, used to draw the tray icon. Null before first reading.</summary>
    public double? SessionPercent =>
        _lastValues.TryGetValue("session:5h", out var window) ? window.Percent : null;

    public Severity WorstSeverity =>
        _lastValues.Count == 0
            ? Severity.Calm
            : _lastValues.Values.Max(w => w.Severity);

    // -- presentation ----------------------------------------------------------

    private UsageRow AddRow(string key, string label)
    {
        var row = new UsageRow { Key = key };

        // Every column after the first carries the rule that separates it from its neighbour.
        if (RowHost.Children.Count > 0)
        {
            row.ShowDivider();
        }

        RowHost.Children.Add(row);
        row.ShowPlaceholder(label);
        _rows.Add(row);
        return row;
    }

    private void SetState(ConnectionState state)
    {
        _state = state;

        StateText.Text = state switch
        {
            ConnectionState.Live => "Live",
            ConnectionState.Stale => "Stale",
            ConnectionState.SignInNeeded => "Sign in",
            _ => "…"
        };

        StateText.Foreground = state switch
        {
            ConnectionState.Live => (Brush)FindResource("DimTextBrush"),
            ConnectionState.Stale => (Brush)FindResource("WarningBrush"),
            ConnectionState.SignInNeeded => (Brush)FindResource("CriticalBrush"),
            _ => (Brush)FindResource("DimTextBrush")
        };

        UpdatePipColour();
    }

    private void UpdatePipColour()
    {
        Pip.Fill = _state switch
        {
            ConnectionState.SignInNeeded => (Brush)FindResource("CriticalBrush"),
            ConnectionState.Stale => (Brush)FindResource("WarningBrush"),
            _ => WorstSeverity switch
            {
                Severity.Critical => (Brush)FindResource("CriticalBrush"),
                Severity.Warning => (Brush)FindResource("WarningBrush"),
                Severity.Normal => (Brush)FindResource("NormalBrush"),
                _ => (Brush)FindResource("CalmBrush")
            }
        };
    }

    private void UpdateFooter(DateTimeOffset now)
    {
        if (_state == ConnectionState.SignInNeeded)
        {
            FooterText.Text = "Tray menu · Sign in with browser";
            return;
        }

        if (_lastSuccess is null)
        {
            FooterText.Text = "Reading credentials";
            return;
        }

        var age = now - _lastSuccess.Value;

        FooterText.Text = age.TotalSeconds < 90
            ? $"Updated {_lastSuccess.Value:HH:mm}"
            : age.TotalHours < 1
                ? $"Updated {_lastSuccess.Value:HH:mm}  ·  {(int)age.TotalMinutes}m ago"
                : $"Updated {_lastSuccess.Value:HH:mm}  ·  {(int)age.TotalHours}h ago";
    }

    // -- placement -------------------------------------------------------------

    private bool _repositioning;

    /// <summary>
    /// Puts the panel in its corner of the chosen monitor, in that monitor's own pixels.
    /// Re-run whenever the panel resizes, the display layout changes, or the setting changes.
    /// </summary>
    public void Reposition()
    {
        if (_repositioning || ActualWidth <= 0)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        _repositioning = true;
        try
        {
            var screens = WinForms.Screen.AllScreens;
            var screen = _settings.MonitorIndex < screens.Length
                ? screens[_settings.MonitorIndex]
                : WinForms.Screen.PrimaryScreen ?? screens[0];

            var transform = source.CompositionTarget.TransformFromDevice;

            var area = screen.WorkingArea;
            var topLeft = transform.Transform(new Point(area.Left, area.Top));
            var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));

            if (_settings.CustomLeft is { } customLeft && _settings.CustomTop is { } customTop)
            {
                Left = customLeft;
                Top = customTop;
                return;
            }

            var mx = _settings.MarginX;
            var my = _settings.MarginY;

            Left = _settings.Anchor switch
            {
                ScreenAnchor.TopLeft or ScreenAnchor.MiddleLeft or ScreenAnchor.BottomLeft =>
                    topLeft.X + mx,
                ScreenAnchor.TopCentre or ScreenAnchor.BottomCentre =>
                    topLeft.X + ((bottomRight.X - topLeft.X - ActualWidth) / 2),
                _ =>
                    bottomRight.X - ActualWidth - mx
            };

            Top = _settings.Anchor switch
            {
                ScreenAnchor.TopLeft or ScreenAnchor.TopCentre or ScreenAnchor.TopRight =>
                    topLeft.Y + my,
                ScreenAnchor.MiddleLeft or ScreenAnchor.MiddleRight =>
                    topLeft.Y + ((bottomRight.Y - topLeft.Y - ActualHeight) / 2),
                _ =>
                    bottomRight.Y - ActualHeight - my
            };

            Log.Info(
                $"placed at {Left:0}x{Top:0}, size {ActualWidth:0}x{ActualHeight:0}, " +
                $"screen {screen.DeviceName} work area {area.Width}x{area.Height} at {area.X},{area.Y}");
        }
        finally
        {
            _repositioning = false;
        }
    }

    /// <summary>Forgets a dragged position and returns to the chosen corner.</summary>
    public void ClearCustomPosition()
    {
        _settings.CustomLeft = null;
        _settings.CustomTop = null;
        _settings.Save();
        Reposition();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(Reposition);

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // Only remember a position the user chose by dragging.
        if (_repositioning || _settings.ClickThrough)
        {
            return;
        }

        _settings.CustomLeft = Left;
        _settings.CustomTop = Top;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Reachable only when click-through is off. Dragging is direct manipulation: no
        // handle to find, no mode to enter, and it ends when the button is released.
        if (_settings.ClickThrough)
        {
            return;
        }

        try
        {
            DragMove();
            _settings.Save();
        }
        catch (InvalidOperationException)
        {
            // Mouse released before the drag started.
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // The overlay owns no keyboard shortcuts. Anything typed belongs to the user's real
        // work, so it is never swallowed here.
        e.Handled = false;
        base.OnPreviewKeyDown(e);
    }
}
