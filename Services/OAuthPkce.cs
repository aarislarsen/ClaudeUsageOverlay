using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageOverlay.Services;

/// <summary>
/// Fallback sign-in: the standard Claude Code OAuth flow with PKCE, completed in the user's
/// normal browser and returned to a loopback listener.
///
/// This path only runs when no Claude Code credentials can be found, or when the ones found
/// have expired past refresh. In day-to-day use the app signs in zero times.
/// </summary>
public sealed class OAuthPkce
{
    private const string AuthorizeEndpoint = "https://claude.ai/oauth/authorize";
    private const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";
    private const int CallbackPort = 54545;
    private const string Scopes = "org:create_api_key user:profile user:inference";

    private static string RedirectUri => $"http://localhost:{CallbackPort}/callback";

    private readonly HttpClient _http;

    public OAuthPkce(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Opens the browser, waits for the redirect, and exchanges the code for tokens.
    /// Returns null if the user closes the browser or the flow times out.
    /// </summary>
    public async Task<OAuthTokens?> SignInAsync(CancellationToken ct)
    {
        var verifier = CreateVerifier();
        var challenge = CreateChallenge(verifier);
        var state = CreateVerifier();

        var authorizeUrl =
            $"{AuthorizeEndpoint}?code=true" +
            $"&client_id={Uri.EscapeDataString(CredentialStore.ClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"cannot listen on port {CallbackPort}: {ex.Message}");
            return null;
        }

        try
        {
            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"cannot open browser: {ex.Message}");
            return null;
        }

        string? code;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(
                contextTask,
                Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);

            if (completed != contextTask)
            {
                return null;
            }

            var context = await contextTask.ConfigureAwait(false);
            var query = context.Request.QueryString;
            code = query["code"];
            var returnedState = query["state"];

            var ok = !string.IsNullOrWhiteSpace(code) &&
                     (string.IsNullOrEmpty(returnedState) || returnedState == state);

            await WriteBrowserResponseAsync(context.Response, ok).ConfigureAwait(false);

            if (!ok)
            {
                Log.Warn("sign-in callback missing code or state mismatch");
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"sign-in callback failed: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Nothing useful to do here.
            }
        }

        // Some Claude Code builds hand back "code#state" in a single value.
        var hashIndex = code!.IndexOf('#');
        if (hashIndex > 0)
        {
            code = code[..hashIndex];
        }

        return await ExchangeAsync(code, verifier, state, ct).ConfigureAwait(false);
    }

    private async Task<OAuthTokens?> ExchangeAsync(
        string code,
        string verifier,
        string state,
        CancellationToken ct)
    {
        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                grant_type = "authorization_code",
                code,
                redirect_uri = RedirectUri,
                client_id = CredentialStore.ClientId,
                code_verifier = verifier,
                state
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
                Log.Error($"token exchange failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            return CredentialStore.ParseTokenResponse(text, previous: null, source: "browser sign-in");
        }
        catch (Exception ex)
        {
            Log.Error($"token exchange error: {ex.Message}");
            return null;
        }
    }

    private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, bool ok)
    {
        var message = ok
            ? "Signed in. You can close this tab."
            : "Sign-in did not complete. You can close this tab.";

        var html =
            "<!doctype html><meta charset=\"utf-8\">" +
            "<title>Claude Usage Overlay</title>" +
            "<body style=\"margin:0;display:grid;place-items:center;height:100vh;" +
            "background:#0a0e12;color:#c9d6df;font:15px ui-monospace,Consolas,monospace\">" +
            $"<p>{message}</p></body>";

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;

        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static string CreateVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    private static string CreateChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
