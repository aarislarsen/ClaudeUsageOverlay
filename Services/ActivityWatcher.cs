using System.IO;

namespace ClaudeUsageOverlay.Services;

/// <summary>
/// Watches Claude Code's transcript folder and raises a debounced signal when it changes.
///
/// Polling alone means the numbers can be up to a minute stale right when the user is
/// actively burning through a window. Watching the transcripts gives a cheap hint that
/// something just happened, so the overlay can re-read shortly after real activity instead
/// of only on the clock.
/// </summary>
public sealed class ActivityWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Timers.Timer _debounce;

    public ActivityWatcher(TimeSpan debounce)
    {
        _debounce = new System.Timers.Timer(debounce.TotalMilliseconds) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Activity?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised once per burst of file activity, on a thread-pool thread.</summary>
    public event EventHandler? Activity;

    public void Start()
    {
        foreach (var directory in CandidateDirectories())
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    Filter = "*.jsonl"
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
                Log.Info($"watching {directory}");
            }
            catch (Exception ex)
            {
                Log.Warn($"cannot watch {directory}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile))
        {
            yield break;
        }

        // Only the local Windows home is watched. Network paths such as \\wsl.localhost do
        // not raise reliable change notifications, and a watcher that half-works would be
        // worse than none.
        var projects = Path.Combine(userProfile, ".claude", "projects");
        if (Directory.Exists(projects))
        {
            yield return projects;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Shutting down; nothing to salvage.
            }
        }

        _watchers.Clear();
        _debounce.Dispose();
    }
}
