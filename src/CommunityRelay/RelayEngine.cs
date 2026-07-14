namespace CommunityRelay;

public sealed class RelayEngine
{
    private static readonly TimeSpan MinimumSubmitSpacing = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinimumReadSpacing = TimeSpan.FromSeconds(
        60d / AppConfig.MaximumPlannedListingQueriesPerMinute);
    private readonly AppConfig _config;
    private readonly IRedditClient _client;
    private readonly StateStore _state;
    private readonly LogService _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _stateNamespace;
    private readonly Dictionary<string, DateTimeOffset> _sourceNotBefore =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastSubmissionAt;
    private DateTimeOffset? _lastListingReadAt;
    private DateTimeOffset? _notBefore;

    public RelayEngine(
        AppConfig config,
        IRedditClient client,
        StateStore state,
        LogService log,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _config = config.Normalize();
        _client = client;
        _state = state;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _stateNamespace = StateNamespaces.Create(_config, client.IsDemo);
    }

    public event EventHandler<RelayEvent>? EventPublished;

    public async Task RunContinuousAsync(CancellationToken cancellationToken)
    {
        Publish(RelayEventLevel.Info, _client.IsDemo
            ? "Demo relay started. No Reddit network calls or writes will occur."
            : _config.DryRun
                ? "Reddit dry run started. Discovery is live; writes are disabled."
                : "Live relay started with native-crosspost-only safeguards.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RunOnceAsync(cancellationToken);
                var now = _clock();
                var regularDelay = TimeSpan.FromSeconds(_config.PollIntervalSeconds);
                var serverDelay = GetServerPause() is { } notBefore && notBefore > now
                    ? notBefore - now
                    : TimeSpan.Zero;
                await _delay(serverDelay > regularDelay ? serverDelay : regularDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop path.
        }
        catch (RedditAuthenticationException exception)
        {
            Publish(RelayEventLevel.Error, $"Relay stopped: {exception.Code}.");
        }
        catch (RedditPermissionException exception)
        {
            Publish(RelayEventLevel.Error, $"Relay stopped: {exception.Code}.");
        }
        finally
        {
            Publish(RelayEventLevel.Info, "Relay stopped.");
        }
    }

    public async Task RunDemoSequenceAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsDemo)
        {
            throw new InvalidOperationException("The demo sequence requires the offline demo client.");
        }
        _state.ResetNamespace(_stateNamespace);
        Publish(RelayEventLevel.Info, "Demo state reset for a repeatable walkthrough.");
        await RunOnceAsync(cancellationToken);
        await RunOnceAsync(cancellationToken);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        var now = _clock();
        _state.PurgeOlderThan(now.AddHours(-48));
        if (GetServerPause() is { } notBefore && notBefore > now)
        {
            Publish(
                RelayEventLevel.Warning,
                $"Server-directed pause is active for another {(notBefore - now).TotalSeconds:F0} seconds.");
            return;
        }

        if (!await ReconcilePendingSubmissionsAsync(now, cancellationToken))
        {
            return;
        }

        var candidates = new List<RelayPost>();
        var baselines = new List<SourceBaseline>();
        void FlushBaselines()
        {
            _state.BaselineSources(_stateNamespace, baselines);
            foreach (var baseline in baselines)
            {
                Publish(
                    RelayEventLevel.Info,
                    $"Baseline recorded for r/{baseline.Source}: {baseline.Posts.Count} existing post ID(s), no forwarding.");
            }
            baselines.Clear();
        }

        foreach (var source in _config.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sourceNotBefore.TryGetValue(source, out var sourcePause) && sourcePause > now)
            {
                continue;
            }

            IReadOnlyList<RelayPost> posts;
            try
            {
                await BeginListingReadAsync(cancellationToken);
                posts = await _client.GetNewPostsAsync(source, _config.ScanLimitPerSource, cancellationToken);
            }
            catch (RedditAuthenticationException)
            {
                PersistPauseIfApiHeadroomExhausted(out _);
                throw;
            }
            catch (RedditRateLimitException rateLimit)
            {
                FlushBaselines();
                SetServerPause(rateLimit.RetryAfter);
                Publish(RelayEventLevel.Warning, "Reddit rate limit reached; polling is paused until the reset.");
                return;
            }
            catch (RedditApiException exception) when (exception.IsTransient)
            {
                if (PersistPauseIfApiHeadroomExhausted(out _))
                {
                    FlushBaselines();
                    return;
                }
                _sourceNotBefore[source] = _clock().AddMinutes(2);
                Publish(
                    RelayEventLevel.Warning,
                    $"Transient listing failure for r/{source} ({exception.Code}); this source will retry after backoff.");
                continue;
            }
            catch (RedditPermissionException exception)
            {
                if (PersistPauseIfApiHeadroomExhausted(out _))
                {
                    FlushBaselines();
                    return;
                }
                _sourceNotBefore[source] = _clock().AddMinutes(30);
                Publish(
                    RelayEventLevel.Warning,
                    $"Source r/{source} is unavailable ({exception.Code}); other sources will continue.");
                continue;
            }
            catch (RedditApiException exception)
            {
                if (PersistPauseIfApiHeadroomExhausted(out _))
                {
                    FlushBaselines();
                    return;
                }
                _sourceNotBefore[source] = _clock().AddMinutes(30);
                Publish(
                    RelayEventLevel.Warning,
                    $"Source r/{source} was skipped ({exception.Code}); other sources will continue.");
                continue;
            }

            if (PersistPauseIfApiHeadroomExhausted(out _))
            {
                FlushBaselines();
                return;
            }

            if (!_state.IsSourceInitialized(_stateNamespace, source))
            {
                baselines.Add(new SourceBaseline(source, posts, now));
                continue;
            }

            var initializedAt = _state.GetSourceInitializedAt(_stateNamespace, source) ?? now;
            var retentionCutoff = now.AddHours(-48);
            var candidateCutoff = initializedAt > retentionCutoff ? initializedAt : retentionCutoff;
            candidates.AddRange(posts.Where(post =>
                post.CreatedAt > candidateCutoff &&
                _state.ShouldProcess(_stateNamespace, post.Fullname, now)));
        }

        FlushBaselines();

        foreach (var post in candidates
                     .DistinctBy(post => post.Fullname, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(post => post.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFilter(post, out var filterReason))
            {
                _state.Mark(_stateNamespace, post, RelayOutcome.Filtered, now, errorCode: filterReason);
                Publish(
                    RelayEventLevel.Info,
                    $"Skipped {post.Fullname} from r/{post.SourceSubreddit}: {filterReason}.",
                    post.Permalink);
                continue;
            }

            if (_config.DryRun || _client.IsDemo)
            {
                _state.Mark(_stateNamespace, post, RelayOutcome.DryRun, now);
                Publish(
                    RelayEventLevel.Success,
                    $"Dry run matched {post.Fullname} from r/{post.SourceSubreddit}; no Reddit post was created.",
                    post.Permalink);
                continue;
            }

            if (WriteCapReached(now))
            {
                Publish(
                    RelayEventLevel.Warning,
                    "The configured write cap was reached. Remaining discoveries stay unprocessed for a later poll.");
                return;
            }

            var attempt = await ForwardOneAsync(post, cancellationToken);
            if (!attempt.Continue)
            {
                return;
            }
        }

        var limits = _client.RateLimits;
        var limitText = limits?.Remaining is { } remaining
            ? $" API allowance reported {remaining:F0} remaining."
            : string.Empty;
        Publish(RelayEventLevel.Info, $"Poll complete; {candidates.Count} unseen candidate(s).{limitText}");
    }

    public async Task<CrosspostResult> TestOneAsync(CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        if (!_client.IsDemo && _config.DryRun)
        {
            throw new InvalidOperationException("Disable Dry run before creating a test crosspost.");
        }

        var now = _clock();
        _state.PurgeOlderThan(now.AddHours(-48));
        if (GetServerPause() is { } notBefore && notBefore > now)
        {
            throw new InvalidOperationException(
                $"A server-directed API pause is active for another {(notBefore - now).TotalSeconds:F0} seconds.");
        }
        if (!_client.IsDemo && WriteCapReached(now))
        {
            throw new InvalidOperationException("The configured write cap has already been reached.");
        }
        if (!_client.IsDemo)
        {
            var missingBaselines = _config.Sources
                .Where(source => !_state.IsSourceInitialized(_stateNamespace, source))
                .ToList();
            if (missingBaselines.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Run Poll once with Dry run enabled before testing a write. Missing baseline: " +
                    string.Join(", ", missingBaselines.Select(source => $"r/{source}")) + ".");
            }
        }

        var posts = new List<RelayPost>();
        foreach (var source in _config.Sources)
        {
            await BeginListingReadAsync(cancellationToken);
            IReadOnlyList<RelayPost> sourcePosts;
            try
            {
                sourcePosts = await _client.GetNewPostsAsync(
                    source,
                    Math.Min(10, _config.ScanLimitPerSource),
                    cancellationToken);
            }
            catch (RedditRateLimitException rateLimit)
            {
                SetServerPause(rateLimit.RetryAfter);
                throw;
            }
            catch (RedditApiException)
            {
                PersistPauseIfApiHeadroomExhausted(out _);
                throw;
            }

            if (PersistPauseIfApiHeadroomExhausted(out var resetAfter))
            {
                throw new RedditRateLimitException(resetAfter);
            }

            if (_client.IsDemo)
            {
                posts.AddRange(sourcePosts);
                continue;
            }

            var initializedAt = _state.GetSourceInitializedAt(_stateNamespace, source)!.Value;
            var retentionCutoff = now.AddHours(-48);
            var candidateCutoff = initializedAt > retentionCutoff ? initializedAt : retentionCutoff;
            posts.AddRange(sourcePosts.Where(post =>
                post.CreatedAt > candidateCutoff &&
                _state.ShouldProcess(_stateNamespace, post.Fullname, now)));
        }
        var selected = posts
            .Where(post => TryFilter(post, out _))
            .Where(post => _client.IsDemo
                ? !_state.WasForwarded(_stateNamespace, post.Fullname)
                : true)
            .OrderByDescending(post => post.CreatedAt)
            .FirstOrDefault() ?? throw new InvalidOperationException("No eligible source post was found.");

        var destination = string.IsNullOrWhiteSpace(_config.DestinationSubreddit)
            ? "demo_animals"
            : _config.DestinationSubreddit;
        var attempt = await ForwardOneAsync(
            selected,
            cancellationToken,
            destination,
            isExplicitTest: true);
        return attempt.Result ?? throw new InvalidOperationException(
            attempt.Continue
                ? "The crosspost was not confirmed. A terminal manual-review state was recorded; it will not be submitted again."
                : "The crosspost was paused by a server-directed limit.");
    }

    private async Task<ForwardAttempt> ForwardOneAsync(
        RelayPost post,
        CancellationToken cancellationToken,
        string? destinationOverride = null,
        bool isExplicitTest = false)
    {
        var destination = destinationOverride ?? _config.DestinationSubreddit;
        var existingOutcome = _state.GetOutcome(_stateNamespace, post.Fullname);
        if (existingOutcome is RelayOutcome.Retry or RelayOutcome.Submitting)
        {
            CrosspostResult? recovered;
            try
            {
                recovered = await TryReconcileAsync(post.Fullname, destination, cancellationToken);
            }
            catch (RedditRateLimitException rateLimit)
            {
                _state.RescheduleRetry(
                    _stateNamespace,
                    post,
                    _clock(),
                    rateLimit.Code,
                    _clock().Add(rateLimit.RetryAfter));
                SetServerPause(rateLimit.RetryAfter);
                Publish(RelayEventLevel.Warning, "Reconciliation was rate-limited; the write remains paused.");
                return new ForwardAttempt(false, null);
            }

            if (recovered is not null)
            {
                RecordForwarded(post, recovered, recoveredAfterUncertainResponse: true);
                return new ForwardAttempt(!IsServerPauseActive(), recovered);
            }

            _state.Mark(
                _stateNamespace,
                post,
                RelayOutcome.TerminalSkip,
                _clock(),
                errorCode: "UNCONFIRMED_PRIOR_SUBMISSION_REVIEW_REQUIRED");
            Publish(
                RelayEventLevel.Warning,
                "A prior submission was not visible during reconciliation. It requires manual destination review and will not be submitted again.");
            return new ForwardAttempt(!IsServerPauseActive(), null);
        }

        await PaceSubmissionAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var attemptAt = _clock();
        _state.Mark(
            _stateNamespace,
            post,
            RelayOutcome.Submitting,
            attemptAt,
            errorCode: "SUBMITTING");
        RecordSubmissionAttempt(attemptAt);
        try
        {
            var result = await _client.CrosspostAsync(
                post,
                destination,
                // Once dispatch starts, the window waits for this bounded HTTP call to
                // quiesce. Cancelling mid-request could lose an accepted response and
                // create a duplicate on restart.
                CancellationToken.None);
            RecordForwarded(post, result, recoveredAfterUncertainResponse: false, isExplicitTest);
            var headroomExhausted = PersistPauseIfApiHeadroomExhausted(out _);
            return new ForwardAttempt(!headroomExhausted, result);
        }
        catch (RedditRateLimitException rateLimit)
        {
            _state.Mark(
                _stateNamespace,
                post,
                RelayOutcome.TerminalSkip,
                _clock(),
                errorCode: "RATE_LIMITED_NO_AUTORETRY");
            SetServerPause(rateLimit.RetryAfter);
            Publish(RelayEventLevel.Warning, "Reddit rate-limited the write. The API pause is persisted, and this source ID will not be submitted again automatically.");
            return new ForwardAttempt(false, null);
        }
        catch (RedditAuthenticationException)
        {
            PersistPauseIfApiHeadroomExhausted(out _);
            _state.Mark(
                _stateNamespace,
                post,
                RelayOutcome.TerminalSkip,
                _clock(),
                errorCode: "AUTHENTICATION_FAILED_NO_AUTORETRY");
            throw;
        }
        catch (RedditPermissionException)
        {
            PersistPauseIfApiHeadroomExhausted(out _);
            _state.Mark(
                _stateNamespace,
                post,
                RelayOutcome.TerminalSkip,
                _clock(),
                errorCode: "PERMISSION_FAILED_NO_AUTORETRY");
            throw;
        }
        catch (RedditApiException exception)
        {
            if (exception.IsTransient)
            {
                if (PersistPauseIfApiHeadroomExhausted(out _))
                {
                    _state.Mark(
                        _stateNamespace,
                        post,
                        RelayOutcome.TerminalSkip,
                        _clock(),
                        errorCode: $"UNCERTAIN_{exception.Code}");
                    Publish(
                        RelayEventLevel.Warning,
                        "The write response was uncertain at the API headroom boundary. Manual review is required; no reconciliation or resubmission was attempted.");
                    return new ForwardAttempt(false, null);
                }

                CrosspostResult? reconciled;
                try
                {
                    reconciled = await TryReconcileAsync(post.Fullname, destination, CancellationToken.None);
                }
                catch (RedditRateLimitException rateLimit)
                {
                    var rateRetryAt = _clock().Add(rateLimit.RetryAfter);
                    _state.RescheduleRetry(
                        _stateNamespace,
                        post,
                        _clock(),
                        rateLimit.Code,
                        rateRetryAt);
                    SetServerPause(rateLimit.RetryAfter);
                    Publish(RelayEventLevel.Warning, "The uncertain write could not be reconciled before a rate-limit pause.");
                    return new ForwardAttempt(false, null);
                }
                if (reconciled is not null)
                {
                    RecordForwarded(post, reconciled, recoveredAfterUncertainResponse: true, isExplicitTest);
                    return new ForwardAttempt(!IsServerPauseActive(), reconciled);
                }

                _state.Mark(
                    _stateNamespace,
                    post,
                    RelayOutcome.TerminalSkip,
                    _clock(),
                    errorCode: $"UNCERTAIN_{exception.Code}");
                Publish(
                    RelayEventLevel.Warning,
                    $"The forwarding response for {post.Fullname} was uncertain ({exception.Code}). " +
                    "Inspect the destination manually; this source ID will not be submitted again.");
                return new ForwardAttempt(!IsServerPauseActive(), null);
            }

            _state.Mark(
                _stateNamespace,
                post,
                RelayOutcome.TerminalSkip,
                _clock(),
                errorCode: exception.Code);
            var headroomExhausted = PersistPauseIfApiHeadroomExhausted(out _);
            Publish(
                RelayEventLevel.Warning,
                $"Reddit rejected {post.Fullname} ({exception.Code}); no retry or copy/link fallback was attempted.");
            return new ForwardAttempt(!headroomExhausted, null);
        }
    }

    private async Task<CrosspostResult?> TryReconcileAsync(
        string sourceFullname,
        string destination,
        CancellationToken cancellationToken)
    {
        await BeginListingReadAsync(cancellationToken);
        try
        {
            var result = await _client.FindRecentCrosspostAsync(
                sourceFullname,
                destination,
                cancellationToken);
            PersistPauseIfApiHeadroomExhausted(out _);
            return result;
        }
        catch (RedditApiException exception) when (exception.IsTransient)
        {
            PersistPauseIfApiHeadroomExhausted(out _);
            return null;
        }
        catch (RedditApiException)
        {
            PersistPauseIfApiHeadroomExhausted(out _);
            throw;
        }
    }

    private async Task<bool> ReconcilePendingSubmissionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var pending in _state.GetPendingReconciliations(_stateNamespace, now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CrosspostResult? recovered;
            try
            {
                recovered = await TryReconcileAsync(
                    pending.Fullname,
                    _config.DestinationSubreddit,
                    cancellationToken);
            }
            catch (RedditRateLimitException rateLimit)
            {
                _state.RescheduleRetry(
                    _stateNamespace,
                    pending.Fullname,
                    _clock(),
                    rateLimit.Code,
                    _clock().Add(rateLimit.RetryAfter));
                SetServerPause(rateLimit.RetryAfter);
                Publish(RelayEventLevel.Warning, "Startup write reconciliation was rate-limited; polling is paused.");
                return false;
            }

            var post = new RelayPost(
                pending.Fullname,
                pending.Fullname.StartsWith("t3_", StringComparison.OrdinalIgnoreCase)
                    ? pending.Fullname[3..]
                    : pending.Fullname,
                pending.SourceSubreddit,
                string.Empty,
                pending.FirstSeenAt,
                string.Empty,
                false,
                false,
                false,
                true);
            if (recovered is not null)
            {
                RecordForwarded(post, recovered, recoveredAfterUncertainResponse: true);
                if (IsServerPauseActive())
                {
                    return false;
                }
                continue;
            }

            _state.Mark(
                _stateNamespace,
                post,
                RelayOutcome.TerminalSkip,
                now,
                errorCode: "UNCONFIRMED_PRIOR_SUBMISSION_REVIEW_REQUIRED");
            Publish(
                RelayEventLevel.Warning,
                "An in-flight write from the prior run was not visible. It requires manual destination review and will not be submitted again.");
            if (IsServerPauseActive())
            {
                return false;
            }
        }
        return true;
    }

    private void RecordForwarded(
        RelayPost post,
        CrosspostResult result,
        bool recoveredAfterUncertainResponse,
        bool isExplicitTest = false)
    {
        _state.Mark(
            _stateNamespace,
            post,
            RelayOutcome.Forwarded,
            _clock(),
            destinationFullname: result.DestinationFullname);
        var message = recoveredAfterUncertainResponse
            ? $"Recovered an accepted crosspost for {post.Fullname} after an uncertain response."
            : _client.IsDemo && isExplicitTest
                ? $"Simulated one native crosspost for {post.Fullname}."
                : isExplicitTest
                    ? $"Created one native crosspost for {post.Fullname}."
                    : $"Native crosspost created for {post.Fullname} from r/{post.SourceSubreddit}.";
        Publish(RelayEventLevel.Success, message, post.Permalink, result.DestinationUrl);
    }

    private bool TryFilter(RelayPost post, out string reason)
    {
        if (!post.IsCrosspostable)
        {
            reason = "NATIVE_CROSSPOST_DISABLED";
            return false;
        }
        if (_config.SkipStickied && post.IsStickied)
        {
            reason = "STICKIED";
            return false;
        }
        if (!_config.AllowNsfw && post.IsNsfw)
        {
            reason = "NSFW_DISABLED";
            return false;
        }

        var title = post.Title.ToLowerInvariant();
        if (_config.IncludeKeywords.Count > 0 &&
            !_config.IncludeKeywords.Any(title.Contains))
        {
            reason = "INCLUDE_KEYWORD_MISS";
            return false;
        }
        if (_config.ExcludeKeywords.Any(title.Contains))
        {
            reason = "EXCLUDE_KEYWORD_MATCH";
            return false;
        }
        reason = "ELIGIBLE";
        return true;
    }

    private bool WriteCapReached(DateTimeOffset now) =>
        _state.CountWriteBudgetSinceAcrossNamespaces(_client.IsDemo, now.AddHours(-1)) >= _config.MaxForwardsPerHour ||
        _state.CountWriteBudgetSinceAcrossNamespaces(_client.IsDemo, now.AddDays(-1)) >= _config.MaxForwardsPerDay;

    private async Task PaceSubmissionAsync(CancellationToken cancellationToken)
    {
        var last = Latest(
            _lastSubmissionAt,
            _client.IsDemo ? null : _state.GetLastSubmissionAt(isDemo: false));
        if (last is null)
        {
            return;
        }
        var remaining = MinimumSubmitSpacing - (_clock() - last.Value);
        if (remaining > TimeSpan.Zero)
        {
            Publish(RelayEventLevel.Info, $"Write pacing pause: {remaining.TotalSeconds:F0} seconds.");
            await _delay(remaining, cancellationToken);
        }
    }

    private async Task PaceListingReadAsync(CancellationToken cancellationToken)
    {
        if (_client.IsDemo)
        {
            return;
        }
        var last = Latest(
            _lastListingReadAt,
            _state.GetLastListingReadAt(isDemo: false));
        if (last is null)
        {
            return;
        }
        var remaining = MinimumReadSpacing - (_clock() - last.Value);
        if (remaining > TimeSpan.Zero)
        {
            await _delay(remaining, cancellationToken);
        }
    }

    private async Task BeginListingReadAsync(CancellationToken cancellationToken)
    {
        await PaceListingReadAsync(cancellationToken);
        var attemptedAt = _clock();
        _lastListingReadAt = attemptedAt;
        if (!_client.IsDemo)
        {
            _state.RecordListingReadAt(isDemo: false, attemptedAt);
        }
    }

    private void RecordSubmissionAttempt(DateTimeOffset attemptedAt)
    {
        _lastSubmissionAt = attemptedAt;
        if (!_client.IsDemo)
        {
            _state.RecordSubmissionAt(isDemo: false, attemptedAt);
        }
    }

    private bool PersistPauseIfApiHeadroomExhausted(out TimeSpan resetAfter)
    {
        resetAfter = TimeSpan.Zero;
        if (_client.IsDemo || _client.RateLimits is not { Remaining: <= 1 } limits)
        {
            return false;
        }

        resetAfter = limits.ResetAfter is { } reported && reported > TimeSpan.Zero
            ? reported
            : TimeSpan.FromMinutes(1);
        SetServerPause(resetAfter);
        Publish(
            RelayEventLevel.Warning,
            "Reddit reported one or fewer API queries remaining; the persisted API pause is active until reset.");
        return true;
    }

    private bool IsServerPauseActive() =>
        GetServerPause() is { } notBefore && notBefore > _clock();

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left > right ? left : right;
    }

    private void SetServerPause(TimeSpan retryAfter)
    {
        var safeDelay = retryAfter <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : retryAfter;
        var proposed = _clock().Add(safeDelay);
        if (_notBefore is null || proposed > _notBefore)
        {
            _notBefore = proposed;
        }
        _state.ExtendApiPause(_client.IsDemo, proposed);
    }

    private DateTimeOffset? GetServerPause()
    {
        var persisted = _state.GetApiNotBefore(_client.IsDemo);
        if (_notBefore is null)
        {
            return persisted;
        }
        if (persisted is null)
        {
            return _notBefore;
        }
        return _notBefore > persisted ? _notBefore : persisted;
    }

    private void ValidateConfiguration()
    {
        var errors = _config.Validate(requireLiveFields: !_client.IsDemo);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    private void Publish(
        RelayEventLevel level,
        string message,
        string? sourceUrl = null,
        string? destinationUrl = null)
    {
        var relayEvent = new RelayEvent(_clock(), level, message, sourceUrl, destinationUrl);
        _log.Write(relayEvent);
        EventPublished?.Invoke(this, relayEvent);
    }

    private sealed record ForwardAttempt(bool Continue, CrosspostResult? Result);
}
