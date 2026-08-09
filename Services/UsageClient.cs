using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageOverlay.Services;

/// <summary>Raised when the endpoint rejects the token, so the caller can ask for a sign-in.</summary>
public sealed class UnauthorizedUsageException : Exception
{
    public UnauthorizedUsageException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads the account's own rate-limit utilisation from the OAuth usage endpoint. This is
/// the same figure Claude Code shows for <c>/usage</c>: authoritative, server-side, and it
/// costs no model tokens to read.
/// </summary>
public sealed class UsageClient
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient _http;
    private readonly CredentialStore _credentials;

    public UsageClient(HttpClient http, CredentialStore credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    /// <summary>Subscription tier reported by the last successful token read, if any.</summary>
    public string? PlanHint { get; private set; }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        var tokens = await _credentials.GetValidTokensAsync(ct).ConfigureAwait(false);
        if (tokens is null)
        {
            throw new UnauthorizedUsageException("no usable Claude Code credentials found");
        }

        PlanHint = tokens.SubscriptionType;

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedUsageException($"usage endpoint returned {(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>
    /// Turns the endpoint's payload into the two (or three) windows the overlay shows.
    /// Unknown or newly added fields are ignored rather than treated as errors.
    /// </summary>
    public static UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var windows = new List<UsageWindow>();

        var session = ReadWindow(root, "five_hour", "session", "Session", "5h");
        if (session is not null)
        {
            windows.Add(session);
        }

        var weekly = ReadWindow(root, "seven_day", "weekly_all", "All models", "7d");
        if (weekly is not null)
        {
            windows.Add(weekly);
        }

        // Opus has its own weekly pool on some plans. Shown only when the account has one.
        var opus = ReadWindow(root, "seven_day_opus", "weekly_opus", "Opus", "7d");
        if (opus is not null)
        {
            windows.Add(opus);
        }

        return new UsageSnapshot(windows, DateTimeOffset.Now);
    }

    /// <summary>
    /// Reads one window, preferring the top-level object and falling back to the matching
    /// entry in the "limits" array.
    /// </summary>
    private static UsageWindow? ReadWindow(
        JsonElement root,
        string objectName,
        string limitKind,
        string label,
        string keySuffix)
    {
        double? percent = null;
        DateTimeOffset? resetsAt = null;
        string? severityText = null;

        if (root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty("utilization", out var util) && util.ValueKind == JsonValueKind.Number)
            {
                percent = util.GetDouble();
            }

            resetsAt = ReadTimestamp(obj, "resets_at");
        }

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!limit.TryGetProperty("kind", out var kind) ||
                    kind.ValueKind != JsonValueKind.String ||
                    !string.Equals(kind.GetString(), limitKind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (limit.TryGetProperty("percent", out var pct) && pct.ValueKind == JsonValueKind.Number)
                {
                    percent ??= pct.GetDouble();
                }

                resetsAt ??= ReadTimestamp(limit, "resets_at");

                if (limit.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String)
                {
                    severityText = sev.GetString();
                }

                break;
            }
        }

        if (percent is null)
        {
            return null;
        }

        var value = Math.Clamp(percent.Value, 0, 100);
        return new UsageWindow(
            Key: $"{limitKind}:{keySuffix}",
            Label: label,
            Percent: value,
            ResetsAt: resetsAt,
            Severity: MapSeverity(severityText, value));
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Takes the worse of what the server says and what the percentage implies. The server's
    /// own severity may stay "normal" until it is nearly too late, and a meter that only
    /// warns at the last moment is not a warning.
    /// </summary>
    private static Severity MapSeverity(string? severityText, double percent)
    {
        var fromPercent = percent switch
        {
            >= 80 => Severity.Critical,
            >= 60 => Severity.Warning,
            >= 25 => Severity.Normal,
            _ => Severity.Calm
        };

        if (string.IsNullOrWhiteSpace(severityText))
        {
            return fromPercent;
        }

        // The server can only ever raise the band, never lower it: it reports "normal" for
        // everything up to the point where it starts to matter, which would otherwise wipe
        // out the distinction between the quiet bands.
        var fromServer = severityText!.ToLowerInvariant() switch
        {
            "warning" or "warn" or "elevated" or "high" => Severity.Warning,
            "critical" or "exceeded" => Severity.Critical,
            _ => Severity.Calm
        };

        return fromServer > fromPercent ? fromServer : fromPercent;
    }
}
