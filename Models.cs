namespace ClaudeUsageOverlay;

/// <summary>
/// Severity of a usage window. Mirrors the "severity" field the API returns, with a
/// threshold-based fallback when the field is absent.
/// </summary>
public enum Severity
{
    /// <summary>Plenty left. Deliberately the quietest colour on the panel.</summary>
    Calm,

    /// <summary>Working through it at a normal rate.</summary>
    Normal,

    /// <summary>Worth knowing about.</summary>
    Warning,

    /// <summary>Nearly out.</summary>
    Critical
}

/// <summary>
/// One rate-limit window (for example the rolling 5-hour session window, or the
/// 7-day all-models window).
/// </summary>
/// <param name="Key">Stable identity used to keep rows in a fixed order.</param>
/// <param name="Label">Text shown to the user. Never changes once chosen.</param>
/// <param name="Percent">Utilisation, 0-100.</param>
/// <param name="ResetsAt">When the window rolls over, or null if unknown.</param>
/// <param name="Severity">Colour band for the row.</param>
public sealed record UsageWindow(
    string Key,
    string Label,
    double Percent,
    DateTimeOffset? ResetsAt,
    Severity Severity);

/// <summary>
/// A complete reading of the account's usage at a point in time.
/// </summary>
public sealed record UsageSnapshot(
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset FetchedAt);

/// <summary>
/// How the overlay is currently doing. The overlay always shows exactly one of these,
/// in the same place, so the user never has to hunt for the app's state.
/// </summary>
public enum ConnectionState
{
    /// <summary>Data is fresh and the last poll succeeded.</summary>
    Live,

    /// <summary>Showing the last good reading; the most recent poll failed.</summary>
    Stale,

    /// <summary>No usable token was found or the token was rejected.</summary>
    SignInNeeded,

    /// <summary>First fetch has not completed yet.</summary>
    Starting
}

/// <summary>
/// An OAuth token set, as stored by Claude Code in <c>.credentials.json</c>.
/// </summary>
public sealed class OAuthTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>Unix time in milliseconds.</summary>
    public long ExpiresAt { get; set; }

    /// <summary>Unix time in milliseconds. Zero when unknown.</summary>
    public long RefreshTokenExpiresAt { get; set; }

    public string? SubscriptionType { get; set; }

    /// <summary>Where this token set came from, for diagnostics only.</summary>
    public string Source { get; set; } = "";

    public DateTimeOffset ExpiresAtUtc =>
        DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt);

    public bool HasAccessToken => !string.IsNullOrWhiteSpace(AccessToken);

    public bool IsExpired(TimeSpan skew) =>
        ExpiresAt <= 0 || ExpiresAtUtc - skew <= DateTimeOffset.UtcNow;

    public bool CanRefresh =>
        !string.IsNullOrWhiteSpace(RefreshToken) &&
        (RefreshTokenExpiresAt <= 0 ||
         DateTimeOffset.FromUnixTimeMilliseconds(RefreshTokenExpiresAt) > DateTimeOffset.UtcNow);
}
