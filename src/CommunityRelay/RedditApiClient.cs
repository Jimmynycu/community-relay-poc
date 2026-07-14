using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CommunityRelay;

public sealed class RedditApiClient : IRedditClient
{
    private const string OAuthBase = "https://oauth.reddit.com";
    private readonly AppConfig _config;
    private readonly SecretStore _secretStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public RedditApiClient(AppConfig config, SecretStore secretStore, HttpClient? httpClient = null)
    {
        _config = config.Normalize();
        _secretStore = secretStore;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool IsDemo => false;
    public RateLimitSnapshot? RateLimits { get; private set; }

    public async Task<IdentityResult> ValidateAsync(
        string destinationSubreddit,
        CancellationToken cancellationToken)
    {
        using var me = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{OAuthBase}/api/v1/me?raw_json=1"),
            cancellationToken);
        using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync(cancellationToken));
        var username = RequiredString(meJson.RootElement, "name");

        using var about = await SendAuthorizedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{OAuthBase}/r/{Uri.EscapeDataString(destinationSubreddit)}/about?raw_json=1"),
            cancellationToken);
        using var aboutJson = JsonDocument.Parse(await about.Content.ReadAsStringAsync(cancellationToken));
        var data = aboutJson.RootElement.GetProperty("data");
        var moderates = OptionalBoolean(data, "user_is_moderator");
        var subscribed = OptionalBoolean(data, "user_is_subscriber");
        var kind = OptionalString(data, "subreddit_type") ?? "unknown";
        return new IdentityResult(username, moderates, subscribed, kind);
    }

    public async Task<IReadOnlyList<RelayPost>> GetNewPostsAsync(
        string sourceSubreddit,
        int limit,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{OAuthBase}/r/{Uri.EscapeDataString(sourceSubreddit)}/new?limit={Math.Clamp(limit, 1, 100)}&raw_json=1"),
            cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var children = json.RootElement.GetProperty("data").GetProperty("children");
        var posts = new List<RelayPost>();
        foreach (var child in children.EnumerateArray())
        {
            var data = child.GetProperty("data");
            var fullname = RequiredString(data, "name");
            var id = RequiredString(data, "id");
            var source = RequiredString(data, "subreddit");
            var title = RequiredString(data, "title");
            var created = DateTimeOffset.FromUnixTimeSeconds(
                Convert.ToInt64(data.GetProperty("created_utc").GetDouble()));
            var permalink = RequiredString(data, "permalink");
            posts.Add(new RelayPost(
                fullname,
                id,
                source,
                title,
                created,
                $"https://www.reddit.com{permalink}",
                OptionalBoolean(data, "over_18"),
                OptionalBoolean(data, "stickied"),
                OptionalBoolean(data, "spoiler"),
                OptionalBoolean(data, "crosspostable")));
        }
        return posts;
    }

    public async Task<CrosspostResult> CrosspostAsync(
        RelayPost post,
        string destinationSubreddit,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{OAuthBase}/api/submit");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["api_type"] = "json",
                    ["kind"] = "crosspost",
                    ["sr"] = destinationSubreddit,
                    ["title"] = post.Title,
                    ["crosspost_fullname"] = post.Fullname,
                    ["resubmit"] = "true",
                    ["sendreplies"] = "false",
                    ["nsfw"] = post.IsNsfw ? "true" : "false",
                    ["spoiler"] = post.IsSpoiler ? "true" : "false",
                    ["raw_json"] = "1"
                });
                return request;
            },
            cancellationToken);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (root.TryGetProperty("json", out var jsonContainer))
        {
            var errors = jsonContainer.GetProperty("errors");
            if (errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                var code = first.GetArrayLength() > 0 ? first[0].GetString() ?? "SUBMIT_REJECTED" : "SUBMIT_REJECTED";
                if (code.Equals("RATELIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new RedditRateLimitException(TimeSpan.FromMinutes(10));
                }
                throw new RedditApiException(code, $"Reddit rejected the native crosspost ({code}).");
            }

            var data = jsonContainer.GetProperty("data");
            var returnedId = OptionalString(data, "id") ?? throw new RedditApiException(
                "MISSING_SUBMISSION_ID",
                "Reddit accepted the request but did not return a post ID.",
                isTransient: true);
            return CreateCrosspostResult(returnedId, destinationSubreddit);
        }

        throw new RedditApiException(
            "UNEXPECTED_SUBMIT_RESPONSE",
            "Reddit returned an unexpected submit response.",
            isTransient: true);
    }

    public async Task<CrosspostResult?> FindRecentCrosspostAsync(
        string sourceFullname,
        string destinationSubreddit,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{OAuthBase}/r/{Uri.EscapeDataString(destinationSubreddit)}/new?limit=100&raw_json=1"),
            cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var children = json.RootElement.GetProperty("data").GetProperty("children");
        foreach (var child in children.EnumerateArray())
        {
            var data = child.GetProperty("data");
            if (!string.Equals(
                    OptionalString(data, "crosspost_parent"),
                    sourceFullname,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return CreateCrosspostResult(RequiredString(data, "name"), destinationSubreddit);
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await EnsureAccessTokenAsync(cancellationToken);
            var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            RedditOAuthService.AddUserAgent(request, _config);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                request.Dispose();
                throw new RedditApiException(
                    "NETWORK_TIMEOUT",
                    "The Reddit request timed out.",
                    isTransient: true,
                    exception);
            }
            catch (HttpRequestException exception)
            {
                request.Dispose();
                throw new RedditApiException(
                    "NETWORK_ERROR",
                    "The Reddit request could not be completed.",
                    isTransient: true,
                    exception);
            }
            request.Dispose();
            CaptureRateLimits(response);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                _accessToken = null;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new RedditAuthenticationException("Reddit authorization is expired or revoked.");
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new RedditPermissionException(
                    "Reddit denied this action. Confirm destination moderator access and posting rules.");
            }
            if ((int)response.StatusCode == 429)
            {
                var retry = ParseRetryAfter(response) ?? RateLimits?.ResetAfter ?? TimeSpan.FromMinutes(10);
                response.Dispose();
                throw new RedditRateLimitException(retry);
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var transient = status >= 500 || status == 408;
                response.Dispose();
                throw new RedditApiException(
                    $"HTTP_{status}",
                    $"Reddit returned HTTP {status}.",
                    transient);
            }
            return response;
        }
        throw new RedditAuthenticationException("Unable to authorize the Reddit request.");
    }

    private static CrosspostResult CreateCrosspostResult(
        string returnedId,
        string destinationSubreddit)
    {
        var id = returnedId.StartsWith("t3_", StringComparison.OrdinalIgnoreCase)
            ? returnedId[3..]
            : returnedId;
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsAsciiLetterOrDigit))
        {
            throw new RedditApiException(
                "INVALID_SUBMISSION_ID",
                "Reddit returned a malformed destination post ID.",
                isTransient: true);
        }

        var destination = Uri.EscapeDataString(destinationSubreddit);
        var escapedId = Uri.EscapeDataString(id);
        return new CrosspostResult(
            $"t3_{id}",
            $"https://www.reddit.com/r/{destination}/comments/{escapedId}");
    }

    private async Task EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return;
            }
            var refreshToken = _secretStore.GetRefreshToken();
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new RedditAuthenticationException("Authorize the app before connecting to Reddit.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.reddit.com/api/v1/access_token");
            RedditOAuthService.AddBasicAuthorization(
                request,
                _config.ClientId,
                _secretStore.GetClientSecret());
            RedditOAuthService.AddUserAgent(request, _config);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            });

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new RedditAuthenticationException(
                    $"Reddit token refresh failed with HTTP {(int)response.StatusCode}.");
            }
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                throw new RedditAuthenticationException(
                    $"Reddit token refresh failed ({error.GetString()}).");
            }
            _accessToken = RequiredString(json.RootElement, "access_token");
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiry)
                ? expiry.GetInt32()
                : 3600;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void CaptureRateLimits(HttpResponseMessage response)
    {
        RateLimits = new RateLimitSnapshot(
            ParseDoubleHeader(response, "X-Ratelimit-Used"),
            ParseDoubleHeader(response, "X-Ratelimit-Remaining"),
            ParseDoubleHeader(response, "X-Ratelimit-Reset") is { } reset
                ? TimeSpan.FromSeconds(Math.Max(0, reset))
                : null,
            DateTimeOffset.UtcNow);
    }

    private static double? ParseDoubleHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }
        return double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }
        return null;
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new RedditApiException("INVALID_RESPONSE", $"Reddit response omitted {name}.", true);

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool OptionalBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    public ValueTask DisposeAsync()
    {
        _tokenLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
