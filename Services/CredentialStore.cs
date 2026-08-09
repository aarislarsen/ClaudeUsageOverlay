using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageOverlay.Services;

/// <summary>
/// Finds an OAuth token to talk to the usage endpoint with.
///
/// Order of preference:
///   1. The token Claude Code already wrote to <c>~/.claude/.credentials.json</c>.
///   2. Any extra credential files named in settings (for example a WSL home directory).
///   3. This app's own cache, written after a browser sign-in or a token refresh.
///
/// The app never writes to Claude Code's credentials file. Claude Code owns that file and
/// rewrites it on its own schedule; a second writer would race it. When this app refreshes
/// a token it keeps the result in its own cache and re-reads Claude Code's file every poll,
/// preferring whichever token is valid for longest.
/// </summary>
public sealed class CredentialStore
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";

    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public CredentialStore(AppSettings settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
    }

    private static string OwnCachePath => Path.Combine(AppSettings.Directory, "credentials.json");

    /// <summary>
    /// Returns a token that is valid right now, refreshing it if needed, or null when the
    /// user has to sign in again.
    /// </summary>
    public async Task<OAuthTokens?> GetValidTokensAsync(CancellationToken ct)
    {
        var best = FindBest();
        if (best is null)
        {
            return null;
        }

        if (!best.IsExpired(RefreshSkew))
        {
            return best;
        }

        if (!best.CanRefresh)
        {
            Log.Warn($"token from {best.Source} expired and cannot be refreshed");
            return null;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Claude Code may have refreshed the shared file while this call was queued.
            var recheck = FindBest();
            if (recheck is not null && !recheck.IsExpired(RefreshSkew))
            {
                return recheck;
            }

            var refreshed = await RefreshAsync(best, ct).ConfigureAwait(false);
            if (refreshed is not null)
            {
                SaveOwnCache(refreshed);
            }

            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Reads every candidate file and returns the longest-lived token found.</summary>
    public OAuthTokens? FindBest()
    {
        OAuthTokens? best = null;

        foreach (var path in CandidatePaths())
        {
            var tokens = ReadFile(path);
            if (tokens is null || !tokens.HasAccessToken)
            {
                continue;
            }

            if (best is null || tokens.ExpiresAt > best.ExpiresAt)
            {
                best = tokens;
            }
        }

        return best;
    }

    public void SaveOwnCache(OAuthTokens tokens)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);

            var payload = new
            {
                claudeAiOauth = new
                {
                    accessToken = tokens.AccessToken,
                    refreshToken = tokens.RefreshToken,
                    expiresAt = tokens.ExpiresAt,
                    refreshTokenExpiresAt = tokens.RefreshTokenExpiresAt,
                    subscriptionType = tokens.SubscriptionType
                }
            };

            var tmp = OwnCachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
            File.Move(tmp, OwnCachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not cache tokens: {ex.Message}");
        }
    }

    private IEnumerable<string> CandidatePaths()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, ".claude", ".credentials.json");
        }

        var configHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configHome))
        {
            yield return Path.Combine(configHome, ".credentials.json");
        }

        foreach (var extra in _settings.ExtraCredentialPaths)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                yield return Environment.ExpandEnvironmentVariables(extra);
            }
        }

        foreach (var wsl in DiscoverWslCredentialPaths())
        {
            yield return wsl;
        }

        yield return OwnCachePath;
    }

    /// <summary>
    /// Looks for Claude Code credentials inside WSL distributions, so the overlay works for
    /// people who only ever run Claude Code in Linux. Failures here are silent and harmless.
    /// </summary>
    private static IEnumerable<string> DiscoverWslCredentialPaths()
    {
        var results = new List<string>();

        try
        {
            const string root = @"\\wsl.localhost";
            if (!Directory.Exists(root))
            {
                return results;
            }

            foreach (var distro in Directory.EnumerateDirectories(root))
            {
                var home = Path.Combine(distro, "home");
                if (!Directory.Exists(home))
                {
                    continue;
                }

                foreach (var user in Directory.EnumerateDirectories(home))
                {
                    var candidate = Path.Combine(user, ".claude", ".credentials.json");
                    if (File.Exists(candidate))
                    {
                        results.Add(candidate);
                    }
                }
            }
        }
        catch
        {
            // WSL may be absent, stopped, or slow to answer. Not a problem.
        }

        return results;
    }

    private static OAuthTokens? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Claude Code rewrites this file; retry briefly if we catch it mid-write.
            string json = "";
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(60);
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            {
                return null;
            }

            return new OAuthTokens
            {
                AccessToken = GetString(oauth, "accessToken") ?? "",
                RefreshToken = GetString(oauth, "refreshToken") ?? "",
                ExpiresAt = GetLong(oauth, "expiresAt"),
                RefreshTokenExpiresAt = GetLong(oauth, "refreshTokenExpiresAt"),
                SubscriptionType = GetString(oauth, "subscriptionType"),
                Source = path
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read {path}: {ex.Message}");
            return null;
        }
    }

    private async Task<OAuthTokens?> RefreshAsync(OAuthTokens current, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = current.RefreshToken,
                client_id = ClientId
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"token refresh failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            return ParseTokenResponse(text, current, "refresh");
        }
        catch (Exception ex)
        {
            Log.Warn($"token refresh error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turns a token endpoint response into an <see cref="OAuthTokens"/>. Shared by the
    /// refresh path and the browser sign-in path.
    /// </summary>
    public static OAuthTokens? ParseTokenResponse(string json, OAuthTokens? previous, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var access = GetString(root, "access_token");
            if (string.IsNullOrWhiteSpace(access))
            {
                return null;
            }

            var expiresIn = GetLong(root, "expires_in");
            var expiresAt = expiresIn > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds();

            var refresh = GetString(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(refresh))
            {
                refresh = previous?.RefreshToken ?? "";
            }

            return new OAuthTokens
            {
                AccessToken = access!,
                RefreshToken = refresh!,
                ExpiresAt = expiresAt,
                RefreshTokenExpiresAt = previous?.RefreshTokenExpiresAt ?? 0,
                SubscriptionType = GetString(root, "subscription_type") ?? previous?.SubscriptionType,
                Source = source
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"token response parse failed: {ex.Message}");
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }
}
