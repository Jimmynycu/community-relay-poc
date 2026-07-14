namespace CommunityRelay;

public sealed class DemoRedditClient : IRedditClient
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDemo => true;
    public RateLimitSnapshot? RateLimits => new(0, 100, TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);

    public Task<IdentityResult> ValidateAsync(
        string destinationSubreddit,
        CancellationToken cancellationToken) =>
        Task.FromResult(new IdentityResult(
            "demo_operator",
            ModeratesDestination: true,
            IsSubscribedToDestination: true,
            DestinationKind: "private-demo"));

    public Task<IReadOnlyList<RelayPost>> GetNewPostsAsync(
        string sourceSubreddit,
        int limit,
        CancellationToken cancellationToken)
    {
        _calls.TryGetValue(sourceSubreddit, out var count);
        count++;
        _calls[sourceSubreddit] = count;
        var now = DateTimeOffset.UtcNow;
        var safeSource = sourceSubreddit.ToLowerInvariant();
        var posts = new List<RelayPost>
        {
            new(
                $"t3_demo_{safeSource}_baseline",
                $"demo_{safeSource}_baseline",
                sourceSubreddit,
                $"Existing r/{sourceSubreddit} post used to establish the baseline",
                now.AddMinutes(-10),
                $"https://www.reddit.com/r/{sourceSubreddit}/comments/demo_baseline",
                false,
                false,
                false,
                true)
        };
        if (count > 1)
        {
            posts.Insert(0, new RelayPost(
                $"t3_demo_{safeSource}_new",
                $"demo_{safeSource}_new",
                sourceSubreddit,
                $"New demo animal post from r/{sourceSubreddit}",
                now,
                $"https://www.reddit.com/r/{sourceSubreddit}/comments/demo_new",
                false,
                false,
                false,
                true));
        }
        return Task.FromResult<IReadOnlyList<RelayPost>>(posts.Take(limit).ToList());
    }

    public Task<CrosspostResult> CrosspostAsync(
        RelayPost post,
        string destinationSubreddit,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CrosspostResult(
            $"t3_forwarded_{post.Id}",
            $"https://www.reddit.com/r/{destinationSubreddit}/comments/demo_forwarded"));

    public Task<CrosspostResult?> FindRecentCrosspostAsync(
        string sourceFullname,
        string destinationSubreddit,
        CancellationToken cancellationToken) =>
        Task.FromResult<CrosspostResult?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
