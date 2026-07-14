using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CommunityRelay;

public sealed class AppConfig
{
    public const string FixedRedirectUri = "http://127.0.0.1:53682/callback";
    public const int MaximumPocSources = 500;
    public const double MaximumPlannedListingQueriesPerMinute = 80;

    public string ClientId { get; set; } = string.Empty;
    public string ContactUsername { get; set; } = string.Empty;
    public string DestinationSubreddit { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = ["cats", "dogs"];
    public int PollIntervalSeconds { get; set; } = 30;
    public int ScanLimitPerSource { get; set; } = 50;
    public int MaxForwardsPerHour { get; set; } = 6;
    public int MaxForwardsPerDay { get; set; } = 20;
    public bool DemoMode { get; set; } = true;
    public bool DryRun { get; set; } = true;
    public bool AllowNsfw { get; set; }
    public bool SkipStickied { get; set; } = true;
    public List<string> IncludeKeywords { get; set; } = [];
    public List<string> ExcludeKeywords { get; set; } = [];
    public bool ApiApprovalConfirmed { get; set; }
    public bool PrivateDestinationConfirmed { get; set; }
    public bool ModeratorConfirmed { get; set; }

    [JsonIgnore]
    public bool LiveAccessAcknowledged =>
        ApiApprovalConfirmed && PrivateDestinationConfirmed && ModeratorConfirmed;

    public AppConfig Normalize()
    {
        ClientId = (ClientId ?? string.Empty).Trim();
        ContactUsername = SubredditNames.NormalizeUsername(ContactUsername);
        DestinationSubreddit = SubredditNames.Normalize(DestinationSubreddit);
        Sources = (Sources ?? []).Select(SubredditNames.Normalize)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IncludeKeywords = NormalizeKeywords(IncludeKeywords ?? []);
        ExcludeKeywords = NormalizeKeywords(ExcludeKeywords ?? []);
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 30, 3600);
        ScanLimitPerSource = Math.Clamp(ScanLimitPerSource, 1, 100);
        MaxForwardsPerHour = Math.Clamp(MaxForwardsPerHour, 1, 30);
        MaxForwardsPerDay = Math.Clamp(MaxForwardsPerDay, 1, 100);
        return this;
    }

    public IReadOnlyList<string> Validate(bool requireLiveFields)
    {
        var errors = new List<string>();
        var sources = Sources ?? [];
        if (sources.Count is < 1 or > MaximumPocSources)
        {
            errors.Add($"Choose between 1 and {MaximumPocSources} source communities.");
        }

        foreach (var source in sources)
        {
            if (!SubredditNames.IsValid(source))
            {
                errors.Add($"Invalid source community: r/{source}");
            }
        }

        var normalizedDestination = SubredditNames.Normalize(DestinationSubreddit);
        if (normalizedDestination.Length > 0 &&
            sources.Any(source => string.Equals(
                SubredditNames.Normalize(source),
                normalizedDestination,
                StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("The destination community cannot also be a source community.");
        }

        if (requireLiveFields)
        {
            if (string.IsNullOrWhiteSpace(ClientId))
            {
                errors.Add("An approved OAuth client ID is required outside Demo mode.");
            }
            if (!SubredditNames.IsValidUsername(ContactUsername))
            {
                errors.Add("Enter the Reddit username used in the API User-Agent.");
            }
            if (!SubredditNames.IsValid(DestinationSubreddit))
            {
                errors.Add("Enter a valid destination community that you moderate.");
            }
            if (!LiveAccessAcknowledged)
            {
                errors.Add("Confirm API approval, private/restricted POC destination, and moderator ownership.");
            }
        }

        if (MaxForwardsPerDay < MaxForwardsPerHour)
        {
            errors.Add("The daily forwarding cap cannot be lower than the hourly cap.");
        }
        return errors;
    }

    private static List<string> NormalizeKeywords(IEnumerable<string> keywords) =>
        keywords.Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public static class SubredditNames
{
    public static string Normalize(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.StartsWith("https://www.reddit.com/r/", StringComparison.OrdinalIgnoreCase))
        {
            result = result[25..];
        }
        if (result.StartsWith("reddit.com/r/", StringComparison.OrdinalIgnoreCase))
        {
            result = result[13..];
        }
        if (result.StartsWith("r/", StringComparison.OrdinalIgnoreCase))
        {
            result = result[2..];
        }
        return result.Trim().Trim('/');
    }

    public static string NormalizeUsername(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.StartsWith("u/", StringComparison.OrdinalIgnoreCase))
        {
            result = result[2..];
        }
        return result;
    }

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 21)
        {
            return false;
        }
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    public static bool IsValidUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 20)
        {
            return false;
        }
        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }
}

public static class OfficialRedditLinks
{
    public static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = parsed.IdnHost;
        if (!(host.Equals("reddit.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".reddit.com", StringComparison.OrdinalIgnoreCase) ||
              host.Equals("support.reddithelp.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}

public static class StateNamespaces
{
    public static string Create(AppConfig config, bool isDemo)
    {
        ArgumentNullException.ThrowIfNull(config);

        var mode = isDemo ? "demo" : "live";
        var destination = SubredditNames.Normalize(config.DestinationSubreddit).ToLowerInvariant();
        var clientId = (config.ClientId ?? string.Empty).Trim();
        var contact = SubredditNames.NormalizeUsername(config.ContactUsername).ToLowerInvariant();
        var canonicalIdentity = string.Join(
            "|",
            "community-relay-state-v1",
            Segment(mode),
            Segment(destination),
            Segment(clientId),
            Segment(contact));
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity)));
        return $"{mode}-v1-{digest[..32].ToLowerInvariant()}";
    }

    private static string Segment(string value) => $"{value.Length}:{value}";
}

public sealed record RelayPost(
    string Fullname,
    string Id,
    string SourceSubreddit,
    string Title,
    DateTimeOffset CreatedAt,
    string Permalink,
    bool IsNsfw,
    bool IsStickied,
    bool IsSpoiler,
    bool IsCrosspostable);

public sealed record CrosspostResult(string DestinationFullname, string DestinationUrl);

public sealed record IdentityResult(
    string Username,
    bool ModeratesDestination,
    bool IsSubscribedToDestination,
    string DestinationKind);

public sealed record RateLimitSnapshot(
    double? Used,
    double? Remaining,
    TimeSpan? ResetAfter,
    DateTimeOffset CapturedAt);

public enum RelayEventLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record RelayEvent(
    DateTimeOffset Timestamp,
    RelayEventLevel Level,
    string Message,
    string? SourceUrl = null,
    string? DestinationUrl = null);

public enum RelayOutcome
{
    Baseline,
    Filtered,
    DryRun,
    Forwarded,
    TerminalSkip,
    Retry,
    Submitting
}

public sealed class PersistedPostState
{
    public string Fullname { get; set; } = string.Empty;
    public string SourceSubreddit { get; set; } = string.Empty;
    public RelayOutcome Outcome { get; set; }
    public string? DestinationFullname { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
}

public sealed class PersistedFingerprintState
{
    public RelayOutcome Outcome { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record PostStateUpdate(
    RelayPost Post,
    RelayOutcome Outcome,
    DateTimeOffset UpdatedAt,
    string? DestinationFullname = null,
    string? ErrorCode = null,
    DateTimeOffset? NextRetryAt = null);

public sealed record SourceBaseline(
    string Source,
    IReadOnlyList<RelayPost> Posts,
    DateTimeOffset InitializedAt);

public sealed class RelayStateDocument
{
    public int SchemaVersion { get; set; } = 5;
    public Dictionary<string, DateTimeOffset> ApiNotBefore { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> LastListingReadAt { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> LastSubmissionAt { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> InitializedSources { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PersistedPostState> Posts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PersistedFingerprintState> CompletedFingerprints { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
