namespace CommunityRelay;

public interface IRedditClient : IAsyncDisposable
{
    bool IsDemo { get; }
    RateLimitSnapshot? RateLimits { get; }
    Task<IdentityResult> ValidateAsync(string destinationSubreddit, CancellationToken cancellationToken);
    Task<IReadOnlyList<RelayPost>> GetNewPostsAsync(
        string sourceSubreddit,
        int limit,
        CancellationToken cancellationToken);
    Task<CrosspostResult> CrosspostAsync(
        RelayPost post,
        string destinationSubreddit,
        CancellationToken cancellationToken);
    Task<CrosspostResult?> FindRecentCrosspostAsync(
        string sourceFullname,
        string destinationSubreddit,
        CancellationToken cancellationToken);
}

public class RedditApiException : Exception
{
    public RedditApiException(string code, string message, bool isTransient = false, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        IsTransient = isTransient;
    }

    public string Code { get; }
    public bool IsTransient { get; }
}

public sealed class RedditAuthenticationException : RedditApiException
{
    public RedditAuthenticationException(string message)
        : base("AUTHENTICATION_REQUIRED", message)
    {
    }
}

public sealed class RedditPermissionException : RedditApiException
{
    public RedditPermissionException(string message)
        : base("PERMISSION_DENIED", message)
    {
    }
}

public sealed class RedditRateLimitException : RedditApiException
{
    public RedditRateLimitException(TimeSpan retryAfter)
        : base("RATE_LIMITED", $"Reddit requested a retry after {retryAfter.TotalSeconds:F0} seconds.")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}
