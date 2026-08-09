using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageOverlay.Services;

/// <summary>
/// Where the panel sits on its monitor. Declared in reading order — top row, middle row,
/// bottom row — because the tray menu is built straight from this list.
/// </summary>
public enum ScreenAnchor
{
    TopLeft,
    TopCentre,
    TopRight,
    MiddleLeft,
    MiddleRight,
    BottomLeft,
    BottomCentre,
    BottomRight
}

/// <summary>
/// User settings, persisted as JSON next to the app's own token cache.
/// Every value has a sane default, so a missing or damaged file is never fatal and the
/// user is never asked to configure anything before the app works.
/// </summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenAnchor Anchor { get; set; } = ScreenAnchor.TopRight;

    /// <summary>
    /// Accepts the older <c>corner</c> key so an existing settings file keeps its placement.
    /// Never written back out.
    /// </summary>
    [JsonPropertyName("corner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenAnchor? LegacyCorner
    {
        get => null;
        set
        {
            if (value is { } corner)
            {
                Anchor = corner;
            }
        }
    }

    /// <summary>Gap from the working-area edge, in device-independent pixels.</summary>
    public double MarginX { get; set; } = 18;

    public double MarginY { get; set; } = 18;

    /// <summary>Index into the system's screen list. Falls back to the primary screen.</summary>
    public int MonitorIndex { get; set; }

    /// <summary>Seconds between polls of the usage endpoint. Clamped to 15-3600.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Overall panel opacity, 0.35-1.0.</summary>
    public double Opacity { get; set; } = 0.94;

    /// <summary>Panel size multiplier, 0.7-2.0.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// When true the overlay cannot be clicked and never takes focus: it is scenery,
    /// not an obstacle. Turning it off lets you drag the panel with the mouse.
    /// </summary>
    public bool ClickThrough { get; set; } = true;

    public bool ShowOverlay { get; set; } = true;

    /// <summary>
    /// Re-poll shortly after Claude Code writes to its transcript folder, so the numbers
    /// track real usage instead of only the poll clock.
    /// </summary>
    public bool WatchClaudeActivity { get; set; } = true;

    /// <summary>
    /// Extra <c>.credentials.json</c> locations to read, in priority order. Useful when
    /// Claude Code runs inside WSL: for example
    /// <c>\\wsl.localhost\Ubuntu\home\you\.claude\.credentials.json</c>.
    /// </summary>
    public List<string> ExtraCredentialPaths { get; set; } = new();

    /// <summary>Free-form position override, written when the panel is dragged.</summary>
    public double? CustomLeft { get; set; }

    public double? CustomTop { get; set; }

    // ---------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageOverlay");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings load failed: {ex.Message}");
        }

        var fresh = new AppSettings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Clamp();
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"settings save failed: {ex.Message}");
        }
    }

    private void Clamp()
    {
        PollSeconds = Math.Clamp(PollSeconds, 15, 3600);
        Opacity = Math.Clamp(Opacity, 0.35, 1.0);
        Scale = Math.Clamp(Scale, 0.7, 2.0);
        MarginX = Math.Clamp(MarginX, 0, 400);
        MarginY = Math.Clamp(MarginY, 0, 400);
        MonitorIndex = Math.Max(0, MonitorIndex);
        ExtraCredentialPaths ??= new List<string>();
    }
}
