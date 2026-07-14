using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommunityRelay;

public sealed class RedditOAuthService
{
    private const int CallbackPort = 53682;
    private static readonly string[] Scopes = ["identity", "read", "submit", "mysubreddits"];
    private readonly HttpClient _httpClient;

    public RedditOAuthService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<string> AuthorizeAsync(
        AppConfig config,
        SecretStore secretStore,
        CancellationToken cancellationToken)
    {
        var errors = config.Normalize().Validate(requireLiveFields: true);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var listener = new LoopbackOAuthListener(CallbackPort);
        listener.Start();

        var authorizationUrl = BuildAuthorizationUrl(config.ClientId, state);
        Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });

        var callback = await listener.WaitForCallbackAsync(TimeSpan.FromMinutes(5), cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state),
                Encoding.UTF8.GetBytes(callback.State ?? string.Empty)))
        {
            throw new RedditAuthenticationException("OAuth state validation failed. Please authorize again.");
        }
        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            throw new RedditAuthenticationException($"Reddit authorization was not granted ({callback.Error}).");
        }
        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            throw new RedditAuthenticationException("Reddit did not return an authorization code.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.reddit.com/api/v1/access_token");
        AddBasicAuthorization(request, config.ClientId, secretStore.GetClientSecret());
        AddUserAgent(request, config);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = callback.Code,
            ["redirect_uri"] = AppConfig.FixedRedirectUri
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RedditAuthenticationException(
                $"OAuth token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            throw new RedditAuthenticationException($"OAuth token exchange failed ({error.GetString()}).");
        }
        if (!json.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement) ||
            string.IsNullOrWhiteSpace(refreshTokenElement.GetString()))
        {
            throw new RedditAuthenticationException(
                "Reddit did not issue a permanent refresh token. Confirm permanent access and try again.");
        }

        var refreshToken = refreshTokenElement.GetString()!;
        secretStore.SetRefreshToken(refreshToken);
        return refreshToken;
    }

    public static string BuildAuthorizationUrl(string clientId, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["state"] = state,
            ["redirect_uri"] = AppConfig.FixedRedirectUri,
            ["duration"] = "permanent",
            ["scope"] = string.Join(' ', Scopes)
        };
        var query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"https://www.reddit.com/api/v1/authorize?{query}";
    }

    internal static void AddBasicAuthorization(
        HttpRequestMessage request,
        string clientId,
        string? clientSecret)
    {
        var credential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{clientId}:{clientSecret ?? string.Empty}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
    }

    internal static void AddUserAgent(HttpRequestMessage request, AppConfig config)
    {
        // Reddit's documented descriptive format contains ':' and a parenthesized
        // contact, which HttpHeaderValueCollection.ParseAdd rejects even though it
        // is valid as a raw HTTP User-Agent field value.
        if (!request.Headers.TryAddWithoutValidation("User-Agent", BuildUserAgent(config)))
        {
            throw new InvalidOperationException("Unable to set the required Reddit User-Agent header.");
        }
    }

    internal static string BuildUserAgent(AppConfig config) =>
        $"windows:community-relay-poc:v0.1.0 (by /u/{config.ContactUsername})";
}

internal sealed class LoopbackOAuthListener : IDisposable
{
    private readonly TcpListener _listener;

    public LoopbackOAuthListener(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start() => _listener.Start(backlog: 1);

    public async Task<OAuthCallback> WaitForCallbackAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(timeoutSource.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(timeoutSource.Token);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                throw new RedditAuthenticationException("The OAuth callback was empty.");
            }
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                throw new RedditAuthenticationException("The OAuth callback was malformed.");
            }

            string? header;
            do
            {
                header = await reader.ReadLineAsync(timeoutSource.Token);
            } while (!string.IsNullOrEmpty(header));

            var uri = new Uri($"http://127.0.0.1{parts[1]}");
            var query = ParseQuery(uri.Query);
            var result = new OAuthCallback(
                query.GetValueOrDefault("code"),
                query.GetValueOrDefault("state"),
                query.GetValueOrDefault("error"));

            var success = string.IsNullOrWhiteSpace(result.Error) && !string.IsNullOrWhiteSpace(result.Code);
            var heading = success ? "Authorization received" : "Authorization was not completed";
            var body = success
                ? "You can close this browser tab and return to Community Relay."
                : "Return to Community Relay and try again.";
            var html = $"<!doctype html><meta charset=\"utf-8\"><title>{heading}</title>" +
                       $"<body style=\"font:16px system-ui;margin:3rem;max-width:42rem\"><h1>{heading}</h1><p>{body}</p></body>";
            var responseBytes = Encoding.UTF8.GetBytes(html);
            var responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {responseBytes.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, timeoutSource.Token);
            await stream.WriteAsync(responseBytes, timeoutSource.Token);
            await stream.FlushAsync(timeoutSource.Token);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RedditAuthenticationException("Authorization timed out after five minutes.");
        }
    }

    public void Dispose() => _listener.Stop();

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2
                ? Uri.UnescapeDataString(pair[1].Replace('+', ' '))
                : string.Empty;
            result[key] = value;
        }
        return result;
    }
}

internal sealed record OAuthCallback(string? Code, string? State, string? Error);
