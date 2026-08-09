using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using ClaudeUsageOverlay.Services;

namespace ClaudeUsageOverlay;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\ClaudeUsageOverlay.SingleInstance";

    /// <summary>Never call the endpoint more often than this, whatever triggers a refresh.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);

    private Mutex? _instanceMutex;
    private AppSettings _settings = null!;
    private HttpClient _http = null!;
    private CredentialStore _credentials = null!;
    private UsageClient _usage = null!;
    private OAuthPkce _oauth = null!;
    private OverlayWindow _overlay = null!;
    private TrayIconHost _tray = null!;
    private ActivityWatcher? _watcher;

    private DispatcherTimer _pollTimer = null!;
    private DispatcherTimer _countdownTimer = null!;
    private CancellationTokenSource _shutdown = new();

    private DateTimeOffset _lastFetchStarted = DateTimeOffset.MinValue;
    private int _fetchInFlight;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Start();
        }
        catch (Exception ex)
        {
            // Startup is the one moment where failing silently is worse than interrupting:
            // there is no panel and no tray icon yet, so the user would be left with nothing
            // at all and no way to find out why.
            Log.Error($"startup failed: {ex}");

            MessageBox.Show(
                $"Claude Usage Overlay could not start.\n\n{ex.Message}\n\nDetails: {Log.FilePath}",
                "Claude Usage Overlay",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
        }
    }

    private void Start()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            // A second copy would put two identical panels in the same corner. Leave quietly
            // rather than interrupt with a dialog about something the user cannot act on.
            Log.Info("another instance is already running; exiting");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error($"unhandled: {args.Exception}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"unhandled (domain): {args.ExceptionObject}");

        Log.Info($"starting, version {Environment.Version}, pid {Environment.ProcessId}");

        _settings = AppSettings.Load();

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeUsageOverlay/1.0 (+windows)");

        _credentials = new CredentialStore(_settings, _http);
        _usage = new UsageClient(_http, _credentials);
        _oauth = new OAuthPkce(_http);

        _overlay = new OverlayWindow(_settings);
        if (_settings.ShowOverlay)
        {
            _overlay.Show();
        }

        Log.Info($"overlay created, shown = {_settings.ShowOverlay}");

        _tray = new TrayIconHost(_settings)
        {
            RefreshRequested = () => _ = RefreshAsync(force: true),
            SignInRequested = () => _ = SignInAsync(),
            QuitRequested = QuitNow,
            VisibilityToggled = OnVisibilityToggled,
            LockToggled = OnLockToggled,
            AnchorChosen = OnAnchorChosen
        };

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.PollSeconds) };
        _pollTimer.Tick += (_, _) => _ = RefreshAsync(force: false);
        _pollTimer.Start();

        // The countdown is local arithmetic, so it can be honest every ten seconds without
        // costing a request.
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _countdownTimer.Tick += (_, _) => _overlay.TickCountdown();
        _countdownTimer.Start();

        if (_settings.WatchClaudeActivity)
        {
            _watcher = new ActivityWatcher(TimeSpan.FromSeconds(6));
            _watcher.Activity += (_, _) => Dispatcher.InvokeAsync(() => _ = RefreshAsync(force: false));
            _watcher.Start();
        }

        Log.Info("tray icon created; startup complete");

        _ = RefreshAsync(force: true);
    }

    // -- polling ---------------------------------------------------------------

    private async Task RefreshAsync(bool force)
    {
        if (Interlocked.CompareExchange(ref _fetchInFlight, 1, 0) == 1)
        {
            return;
        }

        try
        {
            if (!force && DateTimeOffset.UtcNow - _lastFetchStarted < MinimumInterval)
            {
                return;
            }

            _lastFetchStarted = DateTimeOffset.UtcNow;

            var snapshot = await _usage.FetchAsync(_shutdown.Token).ConfigureAwait(true);

            _overlay.Apply(snapshot);
            UpdateTray();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (UnauthorizedUsageException ex)
        {
            Log.Warn(ex.Message);
            _overlay.MarkSignInNeeded();
            UpdateTray();
        }
        catch (Exception ex)
        {
            // Network blips are ordinary. Keep the last good numbers on screen, mark them
            // stale, and try again on the next tick.
            Log.Warn($"fetch failed: {ex.Message}");
            _overlay.MarkStale();
            UpdateTray();
        }
        finally
        {
            Interlocked.Exchange(ref _fetchInFlight, 0);
        }
    }

    private async Task SignInAsync()
    {
        try
        {
            var tokens = await _oauth.SignInAsync(_shutdown.Token).ConfigureAwait(true);
            if (tokens is null)
            {
                Log.Warn("browser sign-in did not complete");
                return;
            }

            _credentials.SaveOwnCache(tokens);
            Log.Info("browser sign-in succeeded");

            await RefreshAsync(force: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error($"sign-in error: {ex.Message}");
        }
    }

    private void UpdateTray() =>
        _tray.Update(_overlay.SessionPercent, _overlay.WorstSeverity, _overlay.TrayTooltip());

    // -- menu actions ----------------------------------------------------------

    private void OnVisibilityToggled(bool visible)
    {
        _settings.ShowOverlay = visible;
        _settings.Save();

        if (visible)
        {
            _overlay.Show();
            _overlay.Reposition();
        }
        else
        {
            _overlay.Hide();
        }

        _tray.SyncMenu();
    }

    private void OnLockToggled(bool clickThrough)
    {
        _settings.ClickThrough = clickThrough;
        _settings.Save();

        _overlay.ApplyInteractionMode();
        _tray.SyncMenu();
    }

    private void OnAnchorChosen(ScreenAnchor anchor)
    {
        _settings.Anchor = anchor;

        // Choosing an anchor is also how a dragged panel is put back where it belongs.
        _settings.CustomLeft = null;
        _settings.CustomTop = null;
        _settings.Save();

        _overlay.Reposition();
        _tray.SyncMenu();
    }

    /// <summary>
    /// Quits at once. There is nothing unsaved and nothing to lose, so asking "are you
    /// sure" would only cost the user a decision they already made.
    /// </summary>
    private void QuitNow()
    {
        try
        {
            _shutdown.Cancel();
            _pollTimer?.Stop();
            _countdownTimer?.Stop();
            _watcher?.Dispose();
            _tray?.Dispose();
            _overlay?.Close();
        }
        catch (Exception ex)
        {
            Log.Warn($"shutdown: {ex.Message}");
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _http?.Dispose();
            _shutdown.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // Process is going away anyway.
        }

        base.OnExit(e);
    }
}
