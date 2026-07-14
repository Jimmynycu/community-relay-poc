using System.Net;
using System.Net.Http;
using System.Text;
using CommunityRelay;

namespace CommunityRelay.SelfTest;

internal static class Program
{
    private static readonly (string Name, Func<TestContext, Task> Run)[] Tests =
    [
        ("config normalization, validation, and persistence", TestConfigAsync),
        ("first-run baseline prevents historical forwarding", TestFirstRunBaselineAsync),
        ("offline demo sequence remains a dry run", TestDemoDryRunAsync),
        ("duplicate discoveries are forwarded once", TestDedupeAsync),
        ("native-crosspost and keyword filters are enforced", TestFiltersAsync),
        ("hourly and daily write caps stop submissions", TestWriteCapsAsync),
        ("uncertain responses reconcile to confirmed success", TestTransientReconciliationAsync),
        ("unreconciled submissions become terminal without retry", TestUncertainTerminalAsync),
        ("failed first dispatch still paces the next POST", TestFailedDispatchPacingAsync),
        ("explicit test reconciles uncertainty without duplication", TestOneReconciliationAsync),
        ("explicit test requires baseline and rejects expired replay", TestOneBaselineAndRetentionAsync),
        ("explicit test persists listing pauses and low headroom", TestOnePausePersistenceAsync),
        ("successful write headroom stops the remaining batch", TestSuccessfulWriteHeadroomAsync),
        ("successful reconciliation headroom stops before new work", TestSuccessfulReconciliationHeadroomAsync),
        ("pacing persists across engine instances", TestCrossEnginePacingAsync),
        ("reconciliation rate limit pauses without another POST", TestReconciliationRateLimitAsync),
        ("dispatched cancellation and startup recovery stay idempotent", TestDispatchCancellationAndStartupRecoveryAsync),
        ("bad source failures do not stop a good source", TestBadSourceIsolationAsync),
        ("Reddit API client uses fail-closed crosspost contracts", TestRedditApiClientContractsAsync),
        ("batch baselines and updates persist together", TestBatchStateUpdatesAsync),
        ("bounded baseline window blocks older unseen posts", TestBoundedBaselineWindowAsync),
        ("state expires raw records and dedupe fingerprints", TestStateRetentionAsync),
        ("legacy fingerprint state migrates with retention timestamps", TestLegacyStateMigrationAsync),
        ("atomic state temp files are cleaned without broad deletion", TestAtomicStateCleanupAsync),
        ("secrets and logs do not expose credentials", TestSecretAndLogHygieneAsync)
    ];

    public static async Task<int> Main()
    {
        var workspaceRoot = FindWorkspaceRoot();
        var runRoot = Path.Combine(
            workspaceRoot,
            "artifacts",
            "self-test-data",
            $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        var passed = 0;
        Console.WriteLine($"Community Relay dependency-free self-test ({Tests.Length} checks)");
        Console.WriteLine($"Test data: {runRoot}");
        Console.WriteLine();

        foreach (var (name, run) in Tests)
        {
            var testRoot = Path.Combine(runRoot, $"{passed + 1:D2}-{SafeFileName(name)}");
            Directory.CreateDirectory(testRoot);
            try
            {
                await run(new TestContext(testRoot));
                passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL  {name}");
                Console.Error.WriteLine(exception);
                Console.Error.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Result: {passed}/{Tests.Length} passed");
        return passed == Tests.Length ? 0 : 1;
    }

    private static Task TestConfigAsync(TestContext context)
    {
        Check.True(OfficialRedditLinks.TryParse(
            "https://support.reddithelp.com/hc/en-us/requests/new?ticket_form_id=1",
            out _));
        Check.True(OfficialRedditLinks.TryParse("https://developers.reddit.com/docs/", out _));
        Check.True(OfficialRedditLinks.TryParse("https://www.reddit.com/r/cats", out _));
        Check.False(OfficialRedditLinks.TryParse("http://www.reddit.com/r/cats", out _));
        Check.False(OfficialRedditLinks.TryParse("file:///C:/not-a-web-link", out _));
        Check.False(OfficialRedditLinks.TryParse("https://reddit.com.attacker.invalid/", out _));
        Check.False(OfficialRedditLinks.TryParse("https://evilreddit.com/", out _));

        var config = new AppConfig
        {
            ClientId = "  approved-client  ",
            ContactUsername = " u/Jimmy_Test ",
            DestinationSubreddit = " https://www.reddit.com/r/My_Animals/ ",
            Sources = [" r/Cats ", "https://www.reddit.com/r/dogs/", "cats", "  "],
            PollIntervalSeconds = 1,
            ScanLimitPerSource = 999,
            MaxForwardsPerHour = 0,
            MaxForwardsPerDay = 999,
            IncludeKeywords = ["  Rescue ", "rescue", "CUTE"],
            ExcludeKeywords = ["  Spoiler  ", "spoiler"],
            ApiApprovalConfirmed = true,
            PrivateDestinationConfirmed = true,
            ModeratorConfirmed = true
        }.Normalize();

        Check.Equal("approved-client", config.ClientId);
        Check.Equal("Jimmy_Test", config.ContactUsername);
        Check.Equal("My_Animals", config.DestinationSubreddit);
        Check.SequenceEqual(["Cats", "dogs"], config.Sources);
        Check.SequenceEqual(["rescue", "cute"], config.IncludeKeywords);
        Check.SequenceEqual(["spoiler"], config.ExcludeKeywords);
        Check.Equal(30, config.PollIntervalSeconds);
        Check.Equal(100, config.ScanLimitPerSource);
        Check.Equal(1, config.MaxForwardsPerHour);
        Check.Equal(100, config.MaxForwardsPerDay);
        Check.Equal(0, config.Validate(requireLiveFields: true).Count);
        Check.True(SubredditNames.IsValidUsername("jimmy-test"));

        var configPath = Path.Combine(context.Root, "config.json");
        var service = new ConfigService(configPath);
        service.Save(config);
        var reloaded = service.Load();
        Check.Equal("My_Animals", reloaded.DestinationSubreddit);
        Check.SequenceEqual(["Cats", "dogs"], reloaded.Sources);

        var missingLiveFields = new AppConfig
        {
            Sources = ["cats"],
            MaxForwardsPerHour = 10,
            MaxForwardsPerDay = 5
        };
        var liveErrors = string.Join(" | ", missingLiveFields.Validate(requireLiveFields: true));
        Check.Contains("approved OAuth client ID", liveErrors);
        Check.Contains("username", liveErrors);
        Check.Contains("destination community", liveErrors);
        Check.Contains("Confirm API approval", liveErrors);
        Check.Contains("daily forwarding cap", liveErrors);

        var tooManySources = new AppConfig
        {
            Sources = Enumerable.Range(0, AppConfig.MaximumPocSources + 1)
                .Select(index => $"source_{index:D3}")
                .ToList()
        };
        Check.Contains(
            $"between 1 and {AppConfig.MaximumPocSources}",
            string.Join(" | ", tooManySources.Validate(requireLiveFields: false)));

        var invalidSource = new AppConfig { Sources = ["bad-name"] };
        Check.Contains(
            "Invalid source community",
            string.Join(" | ", invalidSource.Validate(requireLiveFields: false)));

        var forwardingLoop = new AppConfig
        {
            DestinationSubreddit = " r/CATS/ ",
            Sources = ["https://www.reddit.com/r/cats/"]
        };
        Check.Contains(
            "destination community cannot also be a source",
            string.Join(" | ", forwardingLoop.Validate(requireLiveFields: false)));

        var namespaceConfig = new AppConfig
        {
            ClientId = "client-identity-value",
            ContactUsername = "u/Relay_Operator",
            DestinationSubreddit = "r/My_Animals"
        };
        var liveNamespace = StateNamespaces.Create(namespaceConfig, isDemo: false);
        var equivalentNamespace = StateNamespaces.Create(
            new AppConfig
            {
                ClientId = " client-identity-value ",
                ContactUsername = "relay_operator",
                DestinationSubreddit = "my_animals/"
            },
            isDemo: false);
        Check.Equal(liveNamespace, equivalentNamespace);
        Check.False(liveNamespace == StateNamespaces.Create(namespaceConfig, isDemo: true));
        Check.False(liveNamespace == StateNamespaces.Create(
            new AppConfig
            {
                ClientId = namespaceConfig.ClientId,
                ContactUsername = namespaceConfig.ContactUsername,
                DestinationSubreddit = "other_animals"
            },
            isDemo: false));
        Check.False(liveNamespace == StateNamespaces.Create(
            new AppConfig
            {
                ClientId = "other-client-identity",
                ContactUsername = namespaceConfig.ContactUsername,
                DestinationSubreddit = namespaceConfig.DestinationSubreddit
            },
            isDemo: false));
        Check.False(liveNamespace == StateNamespaces.Create(
            new AppConfig
            {
                ClientId = namespaceConfig.ClientId,
                ContactUsername = "different_operator",
                DestinationSubreddit = namespaceConfig.DestinationSubreddit
            },
            isDemo: false));
        Check.DoesNotContain("client-identity-value", liveNamespace);
        Check.DoesNotContain("relay_operator", liveNamespace);
        Check.DoesNotContain("my_animals", liveNamespace);
        return Task.CompletedTask;
    }

    private static async Task TestFirstRunBaselineAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
        var historical = new[]
        {
            Post("t3_old_1", "cats", "Historical cat", now.AddMinutes(-10)),
            Post("t3_old_2", "cats", "Another historical cat", now.AddMinutes(-5))
        };
        var client = new FakeRedditClient((_, _, _) => historical);
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        var events = new List<RelayEvent>();
        var engine = context.CreateEngine(config, client, state, () => now);
        engine.EventPublished += (_, relayEvent) => events.Add(relayEvent);

        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(0, client.CrosspostCalls.Count);
        Check.True(state.IsSourceInitialized(stateNamespace, "cats"));
        Check.Equal(RelayOutcome.Baseline, StateFor(state, stateNamespace, "t3_old_1").Outcome);
        Check.Equal(RelayOutcome.Baseline, StateFor(state, stateNamespace, "t3_old_2").Outcome);
        Check.True(events.Any(item => item.Message.Contains("Baseline recorded", StringComparison.Ordinal)));
    }

    private static async Task TestDemoDryRunAsync(TestContext context)
    {
        var config = new AppConfig
        {
            Sources = ["cats", "dogs"],
            DemoMode = true,
            DryRun = false
        };
        var client = new DemoRedditClient();
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        var events = new List<RelayEvent>();
        var engine = context.CreateEngine(config, client, state);
        engine.EventPublished += (_, relayEvent) => events.Add(relayEvent);

        await engine.RunDemoSequenceAsync(CancellationToken.None);

        var snapshot = state.Snapshot();
        Check.Equal(RelayOutcome.DryRun, snapshot.Posts[$"{stateNamespace}:t3_demo_cats_new"].Outcome);
        Check.Equal(RelayOutcome.DryRun, snapshot.Posts[$"{stateNamespace}:t3_demo_dogs_new"].Outcome);
        Check.Equal(0, state.CountForwardedSince(stateNamespace, DateTimeOffset.MinValue));
        Check.Equal(2, events.Count(item => item.Message.StartsWith("Dry run matched", StringComparison.Ordinal)));
    }

    private static async Task TestDedupeAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero);
        var duplicate = Post("t3_shared", "cats", "Shared animal post", now);
        var client = new FakeRedditClient((_, _, _) => [duplicate]);
        var config = LiveConfig("cats", "dogs");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        state.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-1));
        state.MarkSourceInitialized(stateNamespace, "dogs", now.AddMinutes(-1));
        var engine = context.CreateEngine(config, client, state, () => now);

        await engine.RunOnceAsync(CancellationToken.None);
        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(RelayOutcome.Forwarded, StateFor(state, stateNamespace, duplicate.Fullname).Outcome);
    }

    private static async Task TestFiltersAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 2, 0, 0, TimeSpan.Zero);
        var posts = new[]
        {
            Post("t3_no_native", "cats", "Animal link", now.AddMinutes(-6), crosspostable: false),
            Post("t3_sticky", "cats", "Animal sticky", now.AddMinutes(-5), stickied: true),
            Post("t3_nsfw", "cats", "Animal NSFW", now.AddMinutes(-4), nsfw: true),
            Post("t3_include_miss", "cats", "Garden scenery", now.AddMinutes(-3)),
            Post("t3_excluded", "cats", "Animal spoiler", now.AddMinutes(-2)),
            Post("t3_allowed", "cats", "Friendly animal portrait", now.AddMinutes(-1))
        };
        var client = new FakeRedditClient((_, _, _) => posts);
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        state.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-10));
        config.IncludeKeywords = ["animal"];
        config.ExcludeKeywords = ["spoiler"];
        config.AllowNsfw = false;
        config.SkipStickied = true;
        var engine = context.CreateEngine(config, client, state, () => now, NoDelay);

        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal("t3_allowed", client.CrosspostCalls.Single().Fullname);
        CheckFilter(state, stateNamespace, "t3_no_native", "NATIVE_CROSSPOST_DISABLED");
        CheckFilter(state, stateNamespace, "t3_sticky", "STICKIED");
        CheckFilter(state, stateNamespace, "t3_nsfw", "NSFW_DISABLED");
        CheckFilter(state, stateNamespace, "t3_include_miss", "INCLUDE_KEYWORD_MISS");
        CheckFilter(state, stateNamespace, "t3_excluded", "EXCLUDE_KEYWORD_MATCH");
        Check.Equal(RelayOutcome.Forwarded, StateFor(state, stateNamespace, "t3_allowed").Outcome);
    }

    private static async Task TestWriteCapsAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 3, 0, 0, TimeSpan.Zero);
        var hourlyPosts = new[]
        {
            Post("t3_hourly_first", "cats", "First animal", now.AddMinutes(-2)),
            Post("t3_hourly_waiting", "cats", "Second animal", now.AddMinutes(-1))
        };
        var hourlyClient = new FakeRedditClient((_, _, _) => hourlyPosts);
        var hourlyConfig = LiveConfig("cats");
        var hourlyNamespace = StateNamespaces.Create(hourlyConfig, hourlyClient.IsDemo);
        var hourlyState = context.CreateState("hourly-state.json");
        hourlyState.MarkSourceInitialized(hourlyNamespace, "cats", now.AddMinutes(-10));
        hourlyConfig.MaxForwardsPerHour = 1;
        hourlyConfig.MaxForwardsPerDay = 5;
        var hourlyEngine = context.CreateEngine(
            hourlyConfig,
            hourlyClient,
            hourlyState,
            () => now,
            NoDelay,
            "hourly.log");

        await hourlyEngine.RunOnceAsync(CancellationToken.None);

        Check.Equal(1, hourlyClient.CrosspostCalls.Count);
        Check.Equal("t3_hourly_first", hourlyClient.CrosspostCalls.Single().Fullname);
        Check.True(hourlyState.ShouldProcess(hourlyNamespace, "t3_hourly_waiting", now));
        Check.False(hourlyState.Snapshot().Posts.ContainsKey(
            $"{hourlyNamespace}:t3_hourly_waiting"));

        var priorBudgetConfig = LiveConfig("cats");
        priorBudgetConfig.DestinationSubreddit = "first_relay";
        var priorBudgetNamespace = StateNamespaces.Create(priorBudgetConfig, isDemo: false);
        var dailyConfig = LiveConfig("cats");
        dailyConfig.DestinationSubreddit = "second_relay";
        var dailyClient = new FakeRedditClient((_, _, _) =>
            [Post("t3_daily_waiting", "cats", "Waiting animal", now)]);
        var dailyNamespace = StateNamespaces.Create(dailyConfig, dailyClient.IsDemo);
        var dailyState = context.CreateState("daily-state.json");
        dailyState.MarkSourceInitialized(dailyNamespace, "cats", now.AddMinutes(-10));
        dailyState.Mark(
            priorBudgetNamespace,
            Post("t3_prior", "cats", "Earlier animal", now.AddHours(-2)),
            RelayOutcome.Retry,
            now.AddHours(-2),
            errorCode: "UNCERTAIN_SUBMISSION",
            nextRetryAt: now.AddMinutes(5));
        var dailyCandidate = Post("t3_daily_waiting", "cats", "Waiting animal", now);
        dailyConfig.MaxForwardsPerHour = 1;
        dailyConfig.MaxForwardsPerDay = 1;
        var dailyEngine = context.CreateEngine(
            dailyConfig,
            dailyClient,
            dailyState,
            () => now,
            NoDelay,
            "daily.log");

        await dailyEngine.RunOnceAsync(CancellationToken.None);

        Check.Equal(0, dailyClient.CrosspostCalls.Count);
        Check.True(dailyState.ShouldProcess(dailyNamespace, dailyCandidate.Fullname, now));
    }

    private static async Task TestTransientReconciliationAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 4, 0, 0, TimeSpan.Zero);
        var candidate = Post("t3_uncertain", "cats", "Uncertain animal", now);
        var client = new FakeRedditClient((_, _, _) => [candidate])
        {
            CrosspostHandler = (_, _, _) => throw new RedditApiException(
                "UPSTREAM_TIMEOUT",
                "The submit response was uncertain.",
                isTransient: true),
            ReconcileHandler = (fullname, _, _) => Task.FromResult<CrosspostResult?>(new CrosspostResult(
                $"t3_recovered_{fullname}",
                "https://www.reddit.com/r/relay/comments/recovered"))
        };
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        state.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-1));
        var events = new List<RelayEvent>();
        var engine = context.CreateEngine(config, client, state, () => now);
        engine.EventPublished += (_, relayEvent) => events.Add(relayEvent);

        await engine.RunOnceAsync(CancellationToken.None);

        var persisted = StateFor(state, stateNamespace, candidate.Fullname);
        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(1, client.ReconcileCalls.Count);
        Check.Equal(RelayOutcome.Forwarded, persisted.Outcome);
        Check.Equal("t3_recovered_t3_uncertain", persisted.DestinationFullname);
        Check.True(events.Any(item => item.Message.Contains("Recovered an accepted crosspost", StringComparison.Ordinal)));
    }

    private static async Task TestUncertainTerminalAsync(TestContext context)
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 14, 5, 0, 0, TimeSpan.Zero));
        var candidate = Post("t3_no_blind_retry", "cats", "Uncertain animal", clock.Now);
        var client = new FakeRedditClient((_, _, _) => [candidate])
        {
            CrosspostHandler = (_, _, _) => throw new RedditApiException(
                "NETWORK_TIMEOUT",
                "The submit outcome is unknown.",
                isTransient: true),
            ReconcileHandler = (_, _, _) => Task.FromResult<CrosspostResult?>(null)
        };
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var statePath = Path.Combine(context.Root, "uncertain-terminal-state.json");
        var state = new StateStore(statePath);
        state.MarkSourceInitialized(stateNamespace, "cats", clock.Now.AddMinutes(-1));
        var engine = context.CreateEngine(config, client, state, () => clock.Now, NoDelay);

        await engine.RunOnceAsync(CancellationToken.None);
        var terminal = StateFor(state, stateNamespace, candidate.Fullname);
        Check.Equal(RelayOutcome.TerminalSkip, terminal.Outcome);
        Check.Equal(0, terminal.AttemptCount);
        Check.Equal("UNCERTAIN_NETWORK_TIMEOUT", terminal.ErrorCode);
        Check.Equal<DateTimeOffset?>(null, terminal.NextRetryAt);
        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(1, client.ReconcileCalls.Count);

        clock.Advance(TimeSpan.FromMinutes(30));
        var restarted = context.CreateEngine(
            config,
            client,
            new StateStore(statePath),
            () => clock.Now,
            NoDelay,
            "uncertain-restarted.log");
        await restarted.RunOnceAsync(CancellationToken.None);
        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(1, client.ReconcileCalls.Count);
    }

    private static async Task TestFailedDispatchPacingAsync(TestContext context)
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 14, 5, 10, 0, TimeSpan.Zero));
        var first = Post("t3_pacing_first", "cats", "First dispatch", clock.Now.AddSeconds(-2));
        var second = Post("t3_pacing_second", "cats", "Second dispatch", clock.Now.AddSeconds(-1));
        var attemptTimes = new List<DateTimeOffset>();
        var delays = new List<TimeSpan>();
        var client = new FakeRedditClient((_, _, _) => [first, second])
        {
            CrosspostHandler = (post, _, _) =>
            {
                attemptTimes.Add(clock.Now);
                if (post.Fullname == first.Fullname)
                {
                    throw new RedditApiException(
                        "NETWORK_TIMEOUT",
                        "The first response was uncertain.",
                        isTransient: true);
                }
                return Task.FromResult(new CrosspostResult(
                    "t3_pacing_destination",
                    "https://www.reddit.com/r/relay_animals/comments/pacing"));
            },
            ReconcileHandler = (_, _, _) => Task.FromResult<CrosspostResult?>(null)
        };
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        state.MarkSourceInitialized(stateNamespace, "cats", clock.Now.AddMinutes(-1));
        Task AdvanceDelay(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(duration);
            clock.Advance(duration);
            return Task.CompletedTask;
        }
        var engine = context.CreateEngine(config, client, state, () => clock.Now, AdvanceDelay);

        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(2, client.CrosspostCalls.Count);
        Check.Equal(2, attemptTimes.Count);
        Check.True(
            attemptTimes[1] - attemptTimes[0] >= TimeSpan.FromSeconds(15),
            "The second POST must remain at least 15 seconds behind a failed dispatched POST.");
        Check.True(delays.Sum(duration => duration.TotalSeconds) >= 15);
        Check.Equal(RelayOutcome.TerminalSkip, StateFor(state, stateNamespace, first.Fullname).Outcome);
        Check.Equal(RelayOutcome.Forwarded, StateFor(state, stateNamespace, second.Fullname).Outcome);
    }

    private static async Task TestOneReconciliationAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 5, 15, 0, TimeSpan.Zero);
        var candidate = Post("t3_test_one_uncertain", "cats", "Explicit test", now);
        var client = new FakeRedditClient((_, _, _) => [candidate])
        {
            CrosspostHandler = (_, _, _) => throw new RedditApiException(
                "UPSTREAM_TIMEOUT",
                "The explicit test response was uncertain.",
                isTransient: true),
            ReconcileHandler = (_, _, _) => Task.FromResult<CrosspostResult?>(new CrosspostResult(
                "t3_test_one_recovered",
                "https://www.reddit.com/r/relay_animals/comments/recovered_test"))
        };
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        state.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-1));
        var logPath = Path.Combine(context.Root, "test-one.log");
        var engine = new RelayEngine(
            config,
            client,
            state,
            new LogService(logPath),
            () => now,
            NoDelay);

        var result = await engine.TestOneAsync(CancellationToken.None);

        Check.Equal("t3_test_one_recovered", result.DestinationFullname);
        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(1, client.ReconcileCalls.Count);
        Check.Equal(RelayOutcome.Forwarded, StateFor(state, stateNamespace, candidate.Fullname).Outcome);
        await Check.ThrowsAsync<InvalidOperationException>(
            () => engine.TestOneAsync(CancellationToken.None));
        Check.Equal(1, client.CrosspostCalls.Count);
        var persistedLog = File.ReadAllText(logPath);
        Check.DoesNotContain(candidate.Fullname, persistedLog);
        Check.DoesNotContain(result.DestinationFullname, persistedLog);
        Check.Contains("[POST_ID]", persistedLog);
    }

    private static async Task TestOneBaselineAndRetentionAsync(TestContext context)
    {
        var initial = new DateTimeOffset(2026, 7, 14, 5, 16, 0, TimeSpan.Zero);
        var candidate = Post("t3_test_one_retention", "cats", "Retention test", initial);
        var config = LiveConfig("cats");

        var missingClient = new FakeRedditClient((_, _, _) => [candidate]);
        var missingState = context.CreateState("test-one-missing-baseline.json");
        var missingEngine = context.CreateEngine(
            config,
            missingClient,
            missingState,
            () => initial,
            NoDelay,
            "test-one-missing-baseline.log");
        var missingBaseline = await Check.ThrowsAsync<InvalidOperationException>(
            () => missingEngine.TestOneAsync(CancellationToken.None));
        Check.Contains("Run Poll once with Dry run enabled", missingBaseline.Message);
        Check.Equal(0, missingClient.ListingCalls.Count);
        Check.Equal(0, missingClient.CrosspostCalls.Count);

        var clock = new MutableClock(initial);
        var replayClient = new FakeRedditClient((_, _, _) => [candidate]);
        var replayState = context.CreateState("test-one-expired-replay.json");
        var stateNamespace = StateNamespaces.Create(config, replayClient.IsDemo);
        replayState.MarkSourceInitialized(stateNamespace, "cats", initial.AddMinutes(-1));
        var firstEngine = context.CreateEngine(
            config,
            replayClient,
            replayState,
            () => clock.Now,
            NoDelay,
            "test-one-expired-first.log");

        await firstEngine.TestOneAsync(CancellationToken.None);
        Check.Equal(1, replayClient.CrosspostCalls.Count);

        clock.Advance(TimeSpan.FromHours(49));
        var restartedEngine = context.CreateEngine(
            config,
            replayClient,
            replayState,
            () => clock.Now,
            NoDelay,
            "test-one-expired-restart.log");
        var expiredReplay = await Check.ThrowsAsync<InvalidOperationException>(
            () => restartedEngine.TestOneAsync(CancellationToken.None));
        Check.Contains("No eligible source post", expiredReplay.Message);
        Check.Equal(2, replayClient.ListingCalls.Count);
        Check.Equal(1, replayClient.CrosspostCalls.Count);
        Check.False(replayState.Snapshot().Posts.ContainsKey(
            $"{stateNamespace}:{candidate.Fullname}"));
    }

    private static async Task TestOnePausePersistenceAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 5, 17, 0, TimeSpan.Zero);
        var retryAfter = TimeSpan.FromMinutes(7);
        var candidate = Post("t3_test_one_rate", "cats", "Listing rate limit", now);
        var config = LiveConfig("cats");
        var rateStatePath = Path.Combine(context.Root, "test-one-listing-rate.json");
        var rateState = new StateStore(rateStatePath);
        var rateClient = new FakeRedditClient((_, _, _) => [candidate])
        {
            ListingHandler = (_, _, _) => throw new RedditRateLimitException(retryAfter)
        };
        var stateNamespace = StateNamespaces.Create(config, rateClient.IsDemo);
        rateState.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-1));
        var rateEngine = context.CreateEngine(
            config,
            rateClient,
            rateState,
            () => now,
            NoDelay,
            "test-one-listing-rate.log");

        var listingLimit = await Check.ThrowsAsync<RedditRateLimitException>(
            () => rateEngine.TestOneAsync(CancellationToken.None));
        Check.Equal(retryAfter, listingLimit.RetryAfter);
        Check.Equal<DateTimeOffset?>(now.Add(retryAfter), rateState.GetApiNotBefore(isDemo: false));
        Check.Equal(1, rateClient.ListingCalls.Count);
        Check.Equal(0, rateClient.CrosspostCalls.Count);

        var restartedClient = new FakeRedditClient((_, _, _) => [candidate]);
        var restartedEngine = context.CreateEngine(
            config,
            restartedClient,
            new StateStore(rateStatePath),
            () => now,
            NoDelay,
            "test-one-listing-rate-restart.log");
        var persistedPause = await Check.ThrowsAsync<InvalidOperationException>(
            () => restartedEngine.TestOneAsync(CancellationToken.None));
        Check.Contains("server-directed API pause", persistedPause.Message);
        Check.Equal(0, restartedClient.ListingCalls.Count);
        Check.Equal(0, restartedClient.CrosspostCalls.Count);

        var headroomNow = now.AddHours(1);
        var headroomReset = TimeSpan.FromMinutes(3);
        var headroomState = context.CreateState("test-one-listing-headroom.json");
        FakeRedditClient? headroomClient = null;
        headroomClient = new FakeRedditClient((_, _, _) => [candidate])
        {
            ListingHandler = (_, _, _) =>
            {
                headroomClient!.RateLimits = new RateLimitSnapshot(
                    Used: 99,
                    Remaining: 1,
                    ResetAfter: headroomReset,
                    CapturedAt: headroomNow);
                return Task.FromResult<IReadOnlyList<RelayPost>>([candidate]);
            }
        };
        var headroomNamespace = StateNamespaces.Create(config, headroomClient.IsDemo);
        headroomState.MarkSourceInitialized(
            headroomNamespace,
            "cats",
            headroomNow.AddMinutes(-1));
        var headroomEngine = context.CreateEngine(
            config,
            headroomClient,
            headroomState,
            () => headroomNow,
            NoDelay,
            "test-one-listing-headroom.log");

        var headroomLimit = await Check.ThrowsAsync<RedditRateLimitException>(
            () => headroomEngine.TestOneAsync(CancellationToken.None));
        Check.Equal(headroomReset, headroomLimit.RetryAfter);
        Check.Equal(0, headroomClient.CrosspostCalls.Count);
        Check.Equal<DateTimeOffset?>(
            headroomNow.Add(headroomReset),
            headroomState.GetApiNotBefore(isDemo: false));
    }

    private static async Task TestSuccessfulWriteHeadroomAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 5, 18, 0, TimeSpan.Zero);
        var resetAfter = TimeSpan.FromMinutes(4);
        var first = Post("t3_headroom_first", "cats", "First", now.AddSeconds(-2));
        var second = Post("t3_headroom_second", "cats", "Second", now.AddSeconds(-1));
        FakeRedditClient? client = null;
        client = new FakeRedditClient((_, _, _) => [first, second])
        {
            CrosspostHandler = (post, _, _) =>
            {
                client!.RateLimits = new RateLimitSnapshot(
                    Used: 99,
                    Remaining: 1,
                    ResetAfter: resetAfter,
                    CapturedAt: now);
                return Task.FromResult(new CrosspostResult(
                    $"t3_destination_{post.Id}",
                    $"https://www.reddit.com/r/relay_animals/comments/{post.Id}"));
            }
        };
        var config = LiveConfig("cats");
        var state = context.CreateState("successful-write-headroom.json");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        state.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-1));
        var engine = context.CreateEngine(
            config,
            client,
            state,
            () => now,
            NoDelay,
            "successful-write-headroom.log");

        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(first.Fullname, client.CrosspostCalls[0].Fullname);
        Check.Equal(RelayOutcome.Forwarded, StateFor(state, stateNamespace, first.Fullname).Outcome);
        Check.False(state.Snapshot().Posts.ContainsKey($"{stateNamespace}:{second.Fullname}"));
        Check.Equal<DateTimeOffset?>(now.Add(resetAfter), state.GetApiNotBefore(isDemo: false));
    }

    private static async Task TestSuccessfulReconciliationHeadroomAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 5, 19, 0, TimeSpan.Zero);
        var resetAfter = TimeSpan.FromMinutes(5);
        var pending = Post("t3_headroom_pending", "cats", "Pending", now.AddMinutes(-2));
        var fresh = Post("t3_headroom_fresh", "cats", "Fresh", now.AddSeconds(-1));
        FakeRedditClient? client = null;
        client = new FakeRedditClient((_, _, _) => [fresh])
        {
            ReconcileHandler = (_, _, _) =>
            {
                client!.RateLimits = new RateLimitSnapshot(
                    Used: 99,
                    Remaining: 1,
                    ResetAfter: resetAfter,
                    CapturedAt: now);
                return Task.FromResult<CrosspostResult?>(new CrosspostResult(
                    "t3_headroom_recovered",
                    "https://www.reddit.com/r/relay_animals/comments/headroom_recovered"));
            }
        };
        var config = LiveConfig("cats");
        var state = context.CreateState("successful-reconciliation-headroom.json");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        state.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-3));
        state.Mark(
            stateNamespace,
            pending,
            RelayOutcome.Retry,
            now.AddMinutes(-1),
            errorCode: "UNCERTAIN",
            nextRetryAt: now);
        var engine = context.CreateEngine(
            config,
            client,
            state,
            () => now,
            NoDelay,
            "successful-reconciliation-headroom.log");

        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(1, client.ReconcileCalls.Count);
        Check.Equal(0, client.ListingCalls.Count);
        Check.Equal(0, client.CrosspostCalls.Count);
        Check.Equal(RelayOutcome.Forwarded, StateFor(state, stateNamespace, pending.Fullname).Outcome);
        Check.Equal<DateTimeOffset?>(now.Add(resetAfter), state.GetApiNotBefore(isDemo: false));
    }

    private static async Task TestCrossEnginePacingAsync(TestContext context)
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 14, 5, 19, 30, TimeSpan.Zero));
        var catsPost = Post("t3_cross_engine_cats", "cats", "Cats", clock.Now.AddSeconds(-2));
        var dogsPost = Post("t3_cross_engine_dogs", "dogs", "Dogs", clock.Now.AddSeconds(-1));
        var listingTimes = new List<DateTimeOffset>();
        var submissionTimes = new List<DateTimeOffset>();
        var delays = new List<TimeSpan>();
        var catsClient = new FakeRedditClient((_, _, _) =>
        {
            listingTimes.Add(clock.Now);
            return [catsPost];
        })
        {
            CrosspostHandler = (post, _, _) =>
            {
                submissionTimes.Add(clock.Now);
                return Task.FromResult(new CrosspostResult(
                    $"t3_destination_{post.Id}",
                    $"https://www.reddit.com/r/relay_animals/comments/{post.Id}"));
            }
        };
        var dogsClient = new FakeRedditClient((_, _, _) =>
        {
            listingTimes.Add(clock.Now);
            return [dogsPost];
        })
        {
            CrosspostHandler = (post, _, _) =>
            {
                submissionTimes.Add(clock.Now);
                return Task.FromResult(new CrosspostResult(
                    $"t3_destination_{post.Id}",
                    $"https://www.reddit.com/r/relay_animals/comments/{post.Id}"));
            }
        };
        var catsConfig = LiveConfig("cats");
        var dogsConfig = LiveConfig("dogs");
        var state = context.CreateState("cross-engine-pacing.json");
        state.MarkSourceInitialized(
            StateNamespaces.Create(catsConfig, catsClient.IsDemo),
            "cats",
            clock.Now.AddMinutes(-1));
        state.MarkSourceInitialized(
            StateNamespaces.Create(dogsConfig, dogsClient.IsDemo),
            "dogs",
            clock.Now.AddMinutes(-1));
        Task AdvanceDelay(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(duration);
            clock.Advance(duration);
            return Task.CompletedTask;
        }
        var catsEngine = context.CreateEngine(
            catsConfig,
            catsClient,
            state,
            () => clock.Now,
            AdvanceDelay,
            "cross-engine-cats.log");
        var dogsEngine = context.CreateEngine(
            dogsConfig,
            dogsClient,
            state,
            () => clock.Now,
            AdvanceDelay,
            "cross-engine-dogs.log");

        await catsEngine.RunOnceAsync(CancellationToken.None);
        await dogsEngine.RunOnceAsync(CancellationToken.None);

        Check.Equal(2, listingTimes.Count);
        Check.Equal(2, submissionTimes.Count);
        Check.True(
            listingTimes[1] - listingTimes[0] >= TimeSpan.FromMilliseconds(750),
            "A new engine must honor the persisted 80-QPM listing interval.");
        Check.True(
            submissionTimes[1] - submissionTimes[0] >= TimeSpan.FromSeconds(15),
            "A new engine must honor the persisted 15-second write interval.");
        Check.True(delays.Any(duration => duration >= TimeSpan.FromMilliseconds(750)));
    }

    private static async Task TestReconciliationRateLimitAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 5, 20, 0, TimeSpan.Zero);
        var retryAfter = TimeSpan.FromMinutes(7);
        var candidate = Post("t3_reconcile_limited", "cats", "Limited reconciliation", now);
        var client = new FakeRedditClient((_, _, _) => [candidate])
        {
            ReconcileHandler = (_, _, _) => throw new RedditRateLimitException(retryAfter)
        };
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var statePath = Path.Combine(context.Root, "persisted-rate-pause-state.json");
        var state = new StateStore(statePath);
        state.MarkSourceInitialized(stateNamespace, "cats", now.AddMinutes(-1));
        state.Mark(
            stateNamespace,
            candidate,
            RelayOutcome.Retry,
            now.AddMinutes(-1),
            errorCode: "UNCERTAIN",
            nextRetryAt: now);
        var events = new List<RelayEvent>();
        var engine = context.CreateEngine(config, client, state, () => now, NoDelay);
        engine.EventPublished += (_, relayEvent) => events.Add(relayEvent);

        await engine.RunOnceAsync(CancellationToken.None);
        var rescheduled = StateFor(state, stateNamespace, candidate.Fullname);
        Check.Equal(RelayOutcome.Retry, rescheduled.Outcome);
        Check.Equal(1, rescheduled.AttemptCount);
        Check.Equal<DateTimeOffset?>(now.Add(retryAfter), rescheduled.NextRetryAt);
        Check.Equal(0, client.CrosspostCalls.Count);
        Check.Equal(1, client.ReconcileCalls.Count);
        Check.Equal<DateTimeOffset?>(now.Add(retryAfter), state.GetApiNotBefore(isDemo: false));

        var restartedClient = new FakeRedditClient((_, _, _) => [candidate]);
        var restartedState = new StateStore(statePath);
        var restartedEngine = context.CreateEngine(
            config,
            restartedClient,
            restartedState,
            () => now,
            NoDelay,
            "persisted-rate-pause-restart.log");
        await restartedEngine.RunOnceAsync(CancellationToken.None);
        Check.Equal(0, restartedClient.ListingCalls.Count);
        Check.Equal(0, restartedClient.CrosspostCalls.Count);
        Check.Equal(0, restartedClient.ReconcileCalls.Count);

        var afterPauseClient = new FakeRedditClient((_, _, _) => [candidate]);
        var afterPauseState = new StateStore(statePath);
        var afterPauseEngine = context.CreateEngine(
            config,
            afterPauseClient,
            afterPauseState,
            () => now.Add(retryAfter).AddSeconds(1),
            NoDelay,
            "persisted-rate-pause-expired.log");
        await afterPauseEngine.RunOnceAsync(CancellationToken.None);
        Check.Equal(0, afterPauseClient.CrosspostCalls.Count);
        Check.Equal(1, afterPauseClient.ReconcileCalls.Count);
        Check.Equal(
            RelayOutcome.TerminalSkip,
            StateFor(afterPauseState, stateNamespace, candidate.Fullname).Outcome);
        Check.True(events.Any(item => item.Message.Contains(
            "rate-limited",
            StringComparison.Ordinal)));

        var directPost = Post("t3_direct_rate_limit", "cats", "Direct write limit", now);
        var directClient = new FakeRedditClient((_, _, _) => [directPost])
        {
            CrosspostHandler = (_, _, _) => throw new RedditRateLimitException(retryAfter)
        };
        var directConfig = LiveConfig("cats");
        var directNamespace = StateNamespaces.Create(directConfig, directClient.IsDemo);
        var directPath = Path.Combine(context.Root, "direct-rate-pause-state.json");
        var directState = new StateStore(directPath);
        directState.MarkSourceInitialized(directNamespace, "cats", now.AddMinutes(-1));
        var directEngine = context.CreateEngine(
            directConfig,
            directClient,
            directState,
            () => now,
            NoDelay,
            "direct-rate-pause.log");

        await directEngine.RunOnceAsync(CancellationToken.None);

        var directTerminal = StateFor(directState, directNamespace, directPost.Fullname);
        Check.Equal(RelayOutcome.TerminalSkip, directTerminal.Outcome);
        Check.Equal("RATE_LIMITED_NO_AUTORETRY", directTerminal.ErrorCode);
        Check.Equal(1, directClient.CrosspostCalls.Count);
        Check.Equal<DateTimeOffset?>(now.Add(retryAfter), directState.GetApiNotBefore(isDemo: false));
    }

    private static async Task TestDispatchCancellationAndStartupRecoveryAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 5, 25, 0, TimeSpan.Zero);
        var dispatched = Post("t3_cancel_after_dispatch", "cats", "Cancellation", now);
        using var callerCancellation = new CancellationTokenSource();
        var dispatchTokenCanBeCanceled = true;
        var cancellationClient = new FakeRedditClient((_, _, _) => [dispatched])
        {
            CrosspostHandler = (_, _, token) =>
            {
                dispatchTokenCanBeCanceled = token.CanBeCanceled;
                callerCancellation.Cancel();
                return Task.FromResult(new CrosspostResult(
                    "t3_cancel_accepted",
                    "https://www.reddit.com/r/relay_animals/comments/cancel_accepted"));
            }
        };
        var cancellationConfig = LiveConfig("cats");
        var cancellationNamespace = StateNamespaces.Create(
            cancellationConfig,
            cancellationClient.IsDemo);
        var cancellationState = context.CreateState("dispatch-cancellation-state.json");
        cancellationState.MarkSourceInitialized(
            cancellationNamespace,
            "cats",
            now.AddMinutes(-1));
        var cancellationEngine = context.CreateEngine(
            cancellationConfig,
            cancellationClient,
            cancellationState,
            () => now,
            NoDelay,
            "dispatch-cancellation.log");

        var accepted = await cancellationEngine.TestOneAsync(callerCancellation.Token);

        Check.True(callerCancellation.IsCancellationRequested);
        Check.False(dispatchTokenCanBeCanceled);
        Check.Equal("t3_cancel_accepted", accepted.DestinationFullname);
        Check.Equal(
            RelayOutcome.Forwarded,
            StateFor(cancellationState, cancellationNamespace, dispatched.Fullname).Outcome);

        var pending = Post(
            "t3_seeded_submitting",
            "cats",
            "Seeded submission",
            now.AddMinutes(-1));
        var operationOrder = new List<string>();
        var recoveryClient = new FakeRedditClient((_, _, _) => [pending])
        {
            ListingHandler = (_, _, _) =>
            {
                operationOrder.Add("listing");
                return Task.FromResult<IReadOnlyList<RelayPost>>([pending]);
            },
            ReconcileHandler = (_, _, _) =>
            {
                operationOrder.Add("reconcile");
                return Task.FromResult<CrosspostResult?>(new CrosspostResult(
                    "t3_seeded_recovered",
                    "https://www.reddit.com/r/relay_animals/comments/seeded_recovered"));
            }
        };
        var recoveryConfig = LiveConfig("cats");
        var recoveryNamespace = StateNamespaces.Create(recoveryConfig, recoveryClient.IsDemo);
        var recoveryState = context.CreateState("startup-recovery-state.json");
        recoveryState.MarkSourceInitialized(recoveryNamespace, "cats", now.AddMinutes(-2));
        recoveryState.Mark(
            recoveryNamespace,
            pending,
            RelayOutcome.Submitting,
            now.AddSeconds(-30),
            errorCode: "SUBMITTING");
        var recoveryEngine = context.CreateEngine(
            recoveryConfig,
            recoveryClient,
            recoveryState,
            () => now,
            NoDelay,
            "startup-recovery.log");

        await recoveryEngine.RunOnceAsync(CancellationToken.None);

        Check.Equal("reconcile", operationOrder.First());
        Check.Equal(0, recoveryClient.CrosspostCalls.Count);
        Check.Equal(
            RelayOutcome.Forwarded,
            StateFor(recoveryState, recoveryNamespace, pending.Fullname).Outcome);

        var missed = Post(
            "t3_seeded_submitting_miss",
            "cats",
            "Seeded submission not visible",
            now.AddMinutes(-1));
        var missClient = new FakeRedditClient((_, _, _) => [missed])
        {
            ReconcileHandler = (_, _, _) => Task.FromResult<CrosspostResult?>(null)
        };
        var missConfig = LiveConfig("cats");
        var missNamespace = StateNamespaces.Create(missConfig, missClient.IsDemo);
        var missPath = Path.Combine(context.Root, "startup-recovery-miss-state.json");
        var missState = new StateStore(missPath);
        missState.MarkSourceInitialized(missNamespace, "cats", now.AddMinutes(-2));
        missState.Mark(
            missNamespace,
            missed,
            RelayOutcome.Submitting,
            now.AddSeconds(-30),
            errorCode: "SUBMITTING");
        var missEngine = context.CreateEngine(
            missConfig,
            missClient,
            missState,
            () => now,
            NoDelay,
            "startup-recovery-miss.log");

        await missEngine.RunOnceAsync(CancellationToken.None);

        var missedState = StateFor(missState, missNamespace, missed.Fullname);
        Check.Equal(RelayOutcome.TerminalSkip, missedState.Outcome);
        Check.Equal(
            "UNCONFIRMED_PRIOR_SUBMISSION_REVIEW_REQUIRED",
            missedState.ErrorCode);
        Check.Equal(0, missClient.CrosspostCalls.Count);
        Check.Equal(1, missClient.ReconcileCalls.Count);

        var missRestarted = context.CreateEngine(
            missConfig,
            missClient,
            new StateStore(missPath),
            () => now.AddMinutes(5),
            NoDelay,
            "startup-recovery-miss-restarted.log");
        await missRestarted.RunOnceAsync(CancellationToken.None);
        Check.Equal(0, missClient.CrosspostCalls.Count);
        Check.Equal(1, missClient.ReconcileCalls.Count);
    }

    private static async Task TestBadSourceIsolationAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 5, 28, 0, TimeSpan.Zero);
        var goodPost = Post("t3_good_source", "cats", "Good source post", now);
        var client = new FakeRedditClient((_, _, _) => [])
        {
            ListingHandler = (source, _, _) => source switch
            {
                "forbidden_source" => Task.FromException<IReadOnlyList<RelayPost>>(
                    new RedditPermissionException("This source is private.")),
                "broken_source" => Task.FromException<IReadOnlyList<RelayPost>>(
                    new RedditApiException("BAD_LISTING", "Malformed source response.")),
                _ => Task.FromResult<IReadOnlyList<RelayPost>>([goodPost])
            }
        };
        var config = LiveConfig("forbidden_source", "broken_source", "cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        foreach (var source in config.Sources)
        {
            state.MarkSourceInitialized(stateNamespace, source, now.AddMinutes(-1));
        }
        var events = new List<RelayEvent>();
        var engine = context.CreateEngine(config, client, state, () => now, NoDelay);
        engine.EventPublished += (_, relayEvent) => events.Add(relayEvent);

        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(goodPost.Fullname, client.CrosspostCalls.Single().Fullname);
        Check.SequenceEqual(
            ["forbidden_source", "broken_source", "cats"],
            client.ListingCalls);
        Check.True(events.Any(item => item.Message.Contains("unavailable", StringComparison.Ordinal)));
        Check.True(events.Any(item => item.Message.Contains("was skipped", StringComparison.Ordinal)));
    }

    private static async Task TestRedditApiClientContractsAsync(TestContext context)
    {
        const string accessToken = "contract-access-token";
        const string refreshToken = "contract-refresh-token";
        var refreshCalls = 0;
        var listingUsedBearer = false;
        string? refreshBody = null;
        string? submitBody = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.RequestUri?.Host == "www.reddit.com" &&
                path == "/api/v1/access_token")
            {
                refreshCalls++;
                refreshBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse($$"""{"access_token":"{{accessToken}}","expires_in":3600}""");
            }
            if (path.EndsWith("/r/cats/new", StringComparison.Ordinal))
            {
                listingUsedBearer = request.Headers.Authorization?.Scheme == "Bearer" &&
                                    request.Headers.Authorization.Parameter == accessToken;
                return JsonResponse("""
                    {
                      "data": {
                        "children": [
                          {
                            "data": {
                              "name": "t3_contract_source",
                              "id": "contract_source",
                              "subreddit": "cats",
                              "title": "Contract source",
                              "created_utc": 1784000000,
                              "permalink": "/r/cats/comments/contract_source"
                            }
                          }
                        ]
                      }
                    }
                    """);
            }
            if (path == "/api/submit")
            {
                submitBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse("""
                    {
                      "json": {
                        "errors": [],
                        "data": {
                          "id": "contractdestination",
                          "url": "file:///C:/malicious-api-returned-link"
                        }
                      }
                    }
                    """);
            }
            throw new TestFailureException($"Unexpected HTTP request: {request.Method} {request.RequestUri}");
        });
        using var httpClient = new HttpClient(handler);
        var secrets = new SecretStore(Path.Combine(context.Root, "contract-secrets.bin"));
        secrets.SetRefreshToken(refreshToken);
        var config = LiveConfig("cats");
        await using var client = new RedditApiClient(config, secrets, httpClient);

        var posts = await client.GetNewPostsAsync("cats", 10, CancellationToken.None);
        Check.Equal(1, posts.Count);
        Check.False(posts.Single().IsCrosspostable);
        Check.Equal(1, refreshCalls);
        Check.True(listingUsedBearer);
        Check.Contains("grant_type=refresh_token", refreshBody ?? string.Empty);
        Check.Contains("refresh_token=contract-refresh-token", refreshBody ?? string.Empty);

        var contractResult = await client.CrosspostAsync(
            Post("t3_contract_source", "cats", "Contract source", DateTimeOffset.UtcNow),
            "relay_animals",
            CancellationToken.None);
        Check.Equal(
            "https://www.reddit.com/r/relay_animals/comments/contractdestination",
            contractResult.DestinationUrl);
        Check.Contains("kind=crosspost", submitBody ?? string.Empty);
        Check.Contains("crosspost_fullname=t3_contract_source", submitBody ?? string.Empty);
        Check.Contains("sendreplies=false", submitBody ?? string.Empty);
        Check.DoesNotContain("send_replies", submitBody ?? string.Empty);
        Check.Equal(1, refreshCalls);
    }

    private static Task TestBatchStateUpdatesAsync(TestContext context)
    {
        var baselineAt = new DateTimeOffset(2026, 7, 14, 5, 30, 0, TimeSpan.Zero);
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, isDemo: false);
        var statePath = Path.Combine(context.Root, "batch-state.json");
        var state = new StateStore(statePath);
        var first = Post("t3_batch_first", "cats", "First", baselineAt.AddMinutes(-2));
        var second = Post("t3_batch_second", "cats", "Second", baselineAt.AddMinutes(-1));
        var dog = Post("t3_batch_dog", "dogs", "Dog", baselineAt.AddMinutes(-1));

        state.BaselineSources(
            stateNamespace,
            [
                new SourceBaseline("Cats", [first, second], baselineAt),
                new SourceBaseline("dogs", [dog], baselineAt.AddSeconds(1))
            ]);

        Check.Equal<DateTimeOffset?>(
            baselineAt,
            state.GetSourceInitializedAt(stateNamespace, "cats"));
        Check.Equal<DateTimeOffset?>(
            baselineAt.AddSeconds(1),
            state.GetSourceInitializedAt(stateNamespace, "DOGS"));
        Check.Equal(RelayOutcome.Baseline, StateFor(state, stateNamespace, first.Fullname).Outcome);
        Check.Equal(RelayOutcome.Baseline, StateFor(state, stateNamespace, second.Fullname).Outcome);
        Check.Equal(RelayOutcome.Baseline, StateFor(state, stateNamespace, dog.Fullname).Outcome);
        Check.Equal(3, state.Snapshot().CompletedFingerprints.Count);

        var updatedAt = baselineAt.AddMinutes(1);
        var filtered = Post("t3_batch_filtered", "cats", "Filtered", updatedAt);
        var submitting = Post("t3_batch_submitting", "cats", "Submitting", updatedAt);
        state.MarkBatch(
            stateNamespace,
            [
                new PostStateUpdate(first, RelayOutcome.DryRun, updatedAt),
                new PostStateUpdate(
                    filtered,
                    RelayOutcome.Filtered,
                    updatedAt,
                    ErrorCode: "TEST_FILTER"),
                new PostStateUpdate(submitting, RelayOutcome.Submitting, updatedAt)
            ]);

        var restarted = new StateStore(statePath);
        Check.Equal(RelayOutcome.DryRun, StateFor(restarted, stateNamespace, first.Fullname).Outcome);
        Check.Equal("TEST_FILTER", StateFor(restarted, stateNamespace, filtered.Fullname).ErrorCode);
        Check.Equal(RelayOutcome.Submitting, restarted.GetOutcome(stateNamespace, submitting.Fullname));
        Check.True(restarted.ShouldProcess(stateNamespace, submitting.Fullname, updatedAt));
        Check.Equal(1, restarted.GetPendingSubmissions(stateNamespace).Count);
        Check.Equal(submitting.Fullname, restarted.GetPendingSubmissions(stateNamespace).Single().Fullname);
        Check.Equal(4, restarted.Snapshot().CompletedFingerprints.Count);
        Check.Equal(1, restarted.CountWriteBudgetSinceAcrossNamespaces(
            isDemo: false,
            baselineAt));

        var firstRetryAt = updatedAt.AddMinutes(1);
        restarted.RescheduleRetry(
            stateNamespace,
            submitting,
            updatedAt.AddSeconds(10),
            "RECONCILE_RATE_LIMIT",
            firstRetryAt);
        var rescheduled = StateFor(restarted, stateNamespace, submitting.Fullname);
        Check.Equal(RelayOutcome.Retry, rescheduled.Outcome);
        Check.Equal(0, rescheduled.AttemptCount);
        Check.Equal<DateTimeOffset?>(firstRetryAt, rescheduled.NextRetryAt);

        restarted.Mark(
            stateNamespace,
            submitting,
            RelayOutcome.Retry,
            firstRetryAt,
            errorCode: "POST_TIMEOUT",
            nextRetryAt: firstRetryAt.AddMinutes(1));
        Check.Equal(1, StateFor(restarted, stateNamespace, submitting.Fullname).AttemptCount);
        Check.Equal(1, restarted.CountWriteBudgetSinceAcrossNamespaces(
            isDemo: false,
            baselineAt));
        restarted.RescheduleRetry(
            stateNamespace,
            submitting,
            firstRetryAt.AddSeconds(10),
            "RECONCILE_UNAVAILABLE",
            firstRetryAt.AddMinutes(2));
        Check.Equal(1, StateFor(restarted, stateNamespace, submitting.Fullname).AttemptCount);

        var otherConfig = LiveConfig("cats");
        otherConfig.DestinationSubreddit = "another_relay";
        var otherNamespace = StateNamespaces.Create(otherConfig, isDemo: false);
        restarted.Mark(
            stateNamespace,
            Post("t3_budget_one", "cats", "One", updatedAt),
            RelayOutcome.Forwarded,
            updatedAt);
        restarted.Mark(
            otherNamespace,
            Post("t3_budget_two", "cats", "Two", updatedAt),
            RelayOutcome.Forwarded,
            updatedAt);
        Check.Equal(1, restarted.CountForwardedSince(stateNamespace, baselineAt));
        Check.Equal(1, restarted.CountForwardedSince(otherNamespace, baselineAt));
        Check.Equal(2, restarted.CountForwardedSinceAcrossNamespaces(
            isDemo: false,
            baselineAt));
        restarted.Mark(
            otherNamespace,
            Post("t3_budget_rejected", "cats", "Rejected", updatedAt),
            RelayOutcome.TerminalSkip,
            updatedAt,
            errorCode: "REDDIT_REJECTED");
        Check.Equal(4, restarted.CountWriteBudgetSinceAcrossNamespaces(
            isDemo: false,
            baselineAt));
        Check.Equal(0, restarted.CountForwardedSinceAcrossNamespaces(
            isDemo: true,
            baselineAt));
        Check.Equal(0, restarted.CountWriteBudgetSinceAcrossNamespaces(
            isDemo: true,
            baselineAt));
        return Task.CompletedTask;
    }

    private static async Task TestBoundedBaselineWindowAsync(TestContext context)
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 14, 5, 45, 0, TimeSpan.Zero));
        var initialTime = clock.Now;
        var initialVisible = Post(
            "t3_initial_visible",
            "cats",
            "Initial visible post",
            initialTime.AddMinutes(-1));
        var olderUnseen = Post(
            "t3_older_unseen",
            "cats",
            "Older unseen post",
            initialTime.AddMinutes(-5));
        var exactlyAtBaseline = Post(
            "t3_at_baseline",
            "cats",
            "At baseline boundary",
            initialTime);
        var genuinelyNew = Post(
            "t3_genuinely_new",
            "cats",
            "Genuinely new post",
            initialTime.AddSeconds(1));
        var client = new FakeRedditClient((_, _, pollCount) =>
            pollCount == 1
                ? [initialVisible]
                : [genuinelyNew, exactlyAtBaseline, olderUnseen]);
        var config = LiveConfig("cats");
        var stateNamespace = StateNamespaces.Create(config, client.IsDemo);
        var state = context.CreateState();
        var engine = context.CreateEngine(config, client, state, () => clock.Now, NoDelay);

        await engine.RunOnceAsync(CancellationToken.None);
        Check.Equal<DateTimeOffset?>(
            initialTime,
            state.GetSourceInitializedAt(stateNamespace, "cats"));

        clock.Advance(TimeSpan.FromMinutes(1));
        await engine.RunOnceAsync(CancellationToken.None);

        Check.Equal(1, client.CrosspostCalls.Count);
        Check.Equal(genuinelyNew.Fullname, client.CrosspostCalls.Single().Fullname);
        Check.False(state.Snapshot().Posts.Values.Any(post =>
            post.Fullname.Equals(olderUnseen.Fullname, StringComparison.OrdinalIgnoreCase)));
        Check.False(state.Snapshot().Posts.Values.Any(post =>
            post.Fullname.Equals(exactlyAtBaseline.Fullname, StringComparison.OrdinalIgnoreCase)));

        var ttlNow = initialTime.AddDays(4);
        var lowVolumePost = Post(
            "t3_low_volume_old",
            "cats",
            "Still visible in a low-volume source",
            ttlNow.AddHours(-50));
        var lowVolumeClient = new FakeRedditClient((_, _, _) => [lowVolumePost]);
        var lowVolumeConfig = LiveConfig("cats");
        var lowVolumeNamespace = StateNamespaces.Create(
            lowVolumeConfig,
            lowVolumeClient.IsDemo);
        var lowVolumeState = context.CreateState("low-volume-state.json");
        lowVolumeState.MarkSourceInitialized(
            lowVolumeNamespace,
            "cats",
            ttlNow.AddDays(-4));
        lowVolumeState.Mark(
            lowVolumeNamespace,
            lowVolumePost,
            RelayOutcome.Forwarded,
            ttlNow.AddHours(-49));
        var lowVolumeEngine = context.CreateEngine(
            lowVolumeConfig,
            lowVolumeClient,
            lowVolumeState,
            () => ttlNow,
            NoDelay,
            "low-volume.log");

        await lowVolumeEngine.RunOnceAsync(CancellationToken.None);

        Check.Equal(0, lowVolumeClient.CrosspostCalls.Count);
        Check.True(lowVolumeState.ShouldProcess(
            lowVolumeNamespace,
            lowVolumePost.Fullname,
            ttlNow));
    }

    private static Task TestStateRetentionAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 6, 0, 0, TimeSpan.Zero);
        var statePath = Path.Combine(context.Root, "retention-state.json");
        var state = new StateStore(statePath);
        state.MarkSourceInitialized("live", "Cats", now.AddDays(-10));
        state.Mark(
            "live",
            Post("t3_expired", "cats", "Expired", now.AddHours(-50)),
            RelayOutcome.Forwarded,
            now.AddHours(-48),
            destinationFullname: "t3_old_destination");
        state.Mark(
            "live",
            Post("T3_RECENT", "cats", "Recent", now.AddHours(-47)),
            RelayOutcome.Forwarded,
            now.AddHours(-47),
            destinationFullname: "t3_recent_destination");
        var longRetryPost = Post(
            "t3_long_retry",
            "cats",
            "Long-running operational record",
            now.AddHours(-50));
        state.Mark(
            "live",
            longRetryPost,
            RelayOutcome.Retry,
            now.AddHours(-49),
            errorCode: "LEGACY_RETRY",
            nextRetryAt: now.AddHours(-1));
        state.RescheduleRetry(
            "live",
            longRetryPost,
            now.AddHours(-1),
            "LEGACY_RECONCILIATION",
            now);
        state.Mark(
            "live",
            longRetryPost,
            RelayOutcome.TerminalSkip,
            now,
            errorCode: "MANUAL_REVIEW");

        var restarted = new StateStore(statePath);
        Check.True(restarted.IsSourceInitialized("LIVE", "cats"));
        Check.True(restarted.WasForwarded("LIVE", "t3_recent"));
        Check.Equal(3, restarted.Snapshot().Posts.Count);
        var beforePurge = restarted.Snapshot();
        var expiredFingerprint = beforePurge.CompletedFingerprints
            .Single(pair => pair.Value.UpdatedAt == now.AddHours(-48))
            .Key;

        restarted.PurgeOlderThan(now.AddHours(-48));
        var afterPurge = new StateStore(statePath);
        var snapshot = afterPurge.Snapshot();
        Check.False(snapshot.Posts.ContainsKey("live:t3_expired"));
        Check.False(snapshot.Posts.ContainsKey("live:t3_long_retry"));
        Check.True(snapshot.Posts.ContainsKey("live:t3_recent"));
        Check.True(
            afterPurge.ShouldProcess("live", "t3_expired", now),
            "A post is eligible again only after both its detail and fingerprint expire.");
        Check.False(afterPurge.WasForwarded("live", "t3_expired"));
        Check.False(snapshot.CompletedFingerprints.ContainsKey(expiredFingerprint));
        Check.Equal(1, snapshot.CompletedFingerprints.Count);
        Check.DoesNotContain("t3_expired", File.ReadAllText(statePath));
        Check.DoesNotContain("t3_long_retry", File.ReadAllText(statePath));
        Check.True(afterPurge.IsSourceInitialized("live", "CATS"));

        afterPurge.Mark(
            "live",
            Post("t3_expired", "cats", "Reprocessed after expiry", now),
            RelayOutcome.DryRun,
            now);
        Check.False(afterPurge.ShouldProcess("live", "t3_expired", now));
        Check.Equal(5, afterPurge.Snapshot().SchemaVersion);
        return Task.CompletedTask;
    }

    private static Task TestLegacyStateMigrationAsync(TestContext context)
    {
        var now = new DateTimeOffset(2026, 7, 14, 6, 15, 0, TimeSpan.Zero);
        var legacyPath = Path.Combine(context.Root, "legacy-state.json");
        var seedPath = Path.Combine(context.Root, "fingerprint-seed.json");
        var seed = new StateStore(seedPath);
        var legacyPost = Post(
            "t3_legacy_completed",
            "cats",
            "Legacy completed post",
            now.AddHours(-1));
        seed.Mark(
            "live",
            legacyPost,
            RelayOutcome.Forwarded,
            now.AddHours(-1),
            destinationFullname: "t3_legacy_destination");
        var fingerprintKey = seed.Snapshot().CompletedFingerprints.Single().Key;
        var legacyJson = $$"""
            {
              "SchemaVersion": 2,
              "InitializedSources": {
                "live:cats": "{{now.AddHours(-2):O}}"
              },
              "Posts": {
                "live:t3_legacy_completed": {
                  "Fullname": "t3_legacy_completed",
                  "SourceSubreddit": "cats",
                  "Outcome": 3,
                  "DestinationFullname": "t3_legacy_destination",
                  "AttemptCount": 0,
                  "ErrorCode": null,
                  "FirstSeenAt": "{{now.AddHours(-1):O}}",
                  "UpdatedAt": "{{now.AddHours(-1):O}}",
                  "NextRetryAt": null
                }
              },
              "CompletedFingerprints": {
                "{{fingerprintKey}}": 3
              }
            }
            """;
        File.WriteAllText(legacyPath, legacyJson);

        var migrated = new StateStore(legacyPath);
        var migratedSnapshot = migrated.Snapshot();
        Check.Equal(5, migratedSnapshot.SchemaVersion);
        Check.True(migrated.WasForwarded("live", legacyPost.Fullname));
        Check.Equal(
            now.AddHours(-1),
            migratedSnapshot.CompletedFingerprints[fingerprintKey].UpdatedAt);

        migrated.PurgeOlderThan(now.AddMinutes(-30));
        Check.True(migrated.ShouldProcess("live", legacyPost.Fullname, now));
        Check.Equal(0, migrated.Snapshot().CompletedFingerprints.Count);

        var corruptPath = Path.Combine(context.Root, "corrupt-state.json");
        File.WriteAllText(corruptPath, "{ definitely-not-valid-json");
        var corruptError = Check.Throws<InvalidOperationException>(
            () => _ = new StateStore(corruptPath));
        Check.Contains("state file is unreadable", corruptError.Message);
        File.Delete(corruptPath);
        Check.Equal(0, new StateStore(corruptPath).Snapshot().Posts.Count);
        return Task.CompletedTask;
    }

    private static Task TestAtomicStateCleanupAsync(TestContext context)
    {
        var statePath = Path.Combine(context.Root, "atomic-relay-state.json");
        var orphanPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        var unrelatedPath = $"{statePath}.operator-notes.tmp";
        File.WriteAllText(orphanPath, "raw post id: t3_orphaned_private_identifier");
        File.WriteAllText(unrelatedPath, "not an app-owned atomic temp file");

        _ = new StateStore(statePath);

        Check.False(File.Exists(orphanPath));
        Check.True(
            File.Exists(unrelatedPath),
            "State startup cleanup must only delete exact GUID-named atomic siblings.");

        var directoryDestination = Path.Combine(context.Root, "atomic-write-destination");
        Directory.CreateDirectory(directoryDestination);
        _ = Check.Throws<IOException>(
            () => AtomicFiles.WriteAllText(directoryDestination, "must not survive a failed replace"));
        Check.Equal(
            0,
            Directory.EnumerateFiles(
                context.Root,
                "atomic-write-destination.*.tmp",
                SearchOption.TopDirectoryOnly).Count());
        return Task.CompletedTask;
    }

    private static Task TestSecretAndLogHygieneAsync(TestContext context)
    {
        const string clientSecret = "client-secret-value-9f3a";
        const string refreshToken = "refresh-token-value-4b2e";
        var secretPath = Path.Combine(context.Root, "secrets.bin");
        var secrets = new SecretStore(secretPath);
        secrets.SetClientSecret($"  {clientSecret}  ");
        secrets.SetRefreshToken($" {refreshToken} ");

        Check.Equal(clientSecret, secrets.GetClientSecret());
        Check.Equal(refreshToken, secrets.GetRefreshToken());
        var ciphertextAsText = Encoding.UTF8.GetString(File.ReadAllBytes(secretPath));
        Check.DoesNotContain(clientSecret, ciphertextAsText);
        Check.DoesNotContain(refreshToken, ciphertextAsText);

        secrets.ClearAuthorization();
        Check.Equal<string?>(null, secrets.GetRefreshToken());
        Check.Equal(clientSecret, secrets.GetClientSecret());

        const string bearer = "bearer-secret-123";
        const string access = "access-secret-456";
        const string refresh = "refresh-secret-789";
        const string client = "client-secret-012";
        const string postId = "t3_private_identifier";
        var rawMessage = $"line one\r\nBearer {bearer} access_token={access}; " +
                         $"refresh_token: {refresh}, client_secret = {client}, post={postId}";
        var sanitized = LogService.Sanitize(rawMessage);
        Check.DoesNotContain("\r", sanitized);
        Check.DoesNotContain("\n", sanitized);
        Check.DoesNotContain(bearer, sanitized);
        Check.DoesNotContain(access, sanitized);
        Check.DoesNotContain(refresh, sanitized);
        Check.DoesNotContain(client, sanitized);
        Check.DoesNotContain(postId, sanitized);
        Check.Contains("Bearer [REDACTED]", sanitized);
        Check.Contains("[POST_ID]", sanitized);
        Check.True(LogService.Sanitize(new string('x', 1_000)).Length == 800);

        var logPath = Path.Combine(context.Root, "relay.log");
        var log = new LogService(logPath);
        log.Write(new RelayEvent(
            DateTimeOffset.Parse("2026-07-14T06:00:00Z"),
            RelayEventLevel.Warning,
            rawMessage,
            $"https://example.invalid/?access_token={access}",
            $"https://example.invalid/?refresh_token={refresh}"));
        var persistedLog = File.ReadAllText(logPath);
        Check.DoesNotContain(bearer, persistedLog);
        Check.DoesNotContain(access, persistedLog);
        Check.DoesNotContain(refresh, persistedLog);
        Check.DoesNotContain(client, persistedLog);
        Check.DoesNotContain(postId, persistedLog);
        Check.DoesNotContain("example.invalid", persistedLog);

        var corruptSecretPath = Path.Combine(context.Root, "corrupt-secrets.bin");
        File.WriteAllBytes(corruptSecretPath, [0x01, 0x02, 0x03, 0x04]);
        var corruptSecrets = new SecretStore(corruptSecretPath);
        var corruptSecretError = Check.Throws<InvalidOperationException>(
            corruptSecrets.EnsureReadable);
        Check.Contains("could not be read", corruptSecretError.Message);
        corruptSecrets.ClearAuthorization();
        Check.False(File.Exists(corruptSecretPath));
        const string recoveredRefresh = "recovered-refresh-token";
        corruptSecrets.SetRefreshToken(recoveredRefresh);
        Check.Equal(recoveredRefresh, corruptSecrets.GetRefreshToken());
        Check.DoesNotContain(
            recoveredRefresh,
            Encoding.UTF8.GetString(File.ReadAllBytes(corruptSecretPath)));
        File.WriteAllBytes(corruptSecretPath, [0x05, 0x06, 0x07]);
        corruptSecrets.ResetAll();
        Check.False(File.Exists(corruptSecretPath));
        return Task.CompletedTask;
    }

    private static AppConfig LiveConfig(params string[] sources) => new()
    {
        ClientId = "approved-client-id",
        ContactUsername = "relay_operator",
        DestinationSubreddit = "relay_animals",
        Sources = sources.ToList(),
        PollIntervalSeconds = 30,
        ScanLimitPerSource = 50,
        MaxForwardsPerHour = 30,
        MaxForwardsPerDay = 100,
        DemoMode = false,
        DryRun = false,
        ApiApprovalConfirmed = true,
        PrivateDestinationConfirmed = true,
        ModeratorConfirmed = true
    };

    private static RelayPost Post(
        string fullname,
        string source,
        string title,
        DateTimeOffset createdAt,
        bool nsfw = false,
        bool stickied = false,
        bool crosspostable = true) => new(
            fullname,
            fullname.StartsWith("t3_", StringComparison.OrdinalIgnoreCase) ? fullname[3..] : fullname,
            source,
            title,
            createdAt,
            $"https://www.reddit.com/r/{source}/comments/{fullname}",
            nsfw,
            stickied,
            IsSpoiler: false,
            crosspostable);

    private static PersistedPostState StateFor(StateStore state, string stateNamespace, string fullname)
    {
        var key = $"{stateNamespace}:{fullname}";
        var snapshot = state.Snapshot();
        return snapshot.Posts.TryGetValue(key, out var persisted)
            ? persisted
            : throw new TestFailureException($"Expected state record '{key}' was not found.");
    }

    private static void CheckFilter(
        StateStore state,
        string stateNamespace,
        string fullname,
        string errorCode)
    {
        var persisted = StateFor(state, stateNamespace, fullname);
        Check.Equal(RelayOutcome.Filtered, persisted.Outcome);
        Check.Equal(errorCode, persisted.ErrorCode);
    }

    private static Task NoDelay(TimeSpan _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "CommunityRelay")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "Could not find the workspace root. Run this self-test from a build beneath G:\\Try_out.");
    }

    private static string SafeFileName(string value) => string.Concat(
        value.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-'));
}

internal sealed record TestContext(string Root)
{
    public StateStore CreateState(string fileName = "relay-state.json") =>
        new(Path.Combine(Root, fileName));

    public RelayEngine CreateEngine(
        AppConfig config,
        IRedditClient client,
        StateStore state,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string logFileName = "relay.log") => new(
            config,
            client,
            state,
            new LogService(Path.Combine(Root, logFileName)),
            clock,
            delay);
}

internal sealed class FakeRedditClient : IRedditClient
{
    private readonly Func<string, int, int, IReadOnlyList<RelayPost>> _posts;
    private readonly Dictionary<string, int> _pollCounts = new(StringComparer.OrdinalIgnoreCase);

    public FakeRedditClient(Func<string, int, int, IReadOnlyList<RelayPost>> posts)
    {
        _posts = posts;
    }

    public bool IsDemo => false;
    public RateLimitSnapshot? RateLimits { get; set; } =
        new(Used: 1, Remaining: 99, ResetAfter: TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
    public List<RelayPost> CrosspostCalls { get; } = [];
    public List<string> ListingCalls { get; } = [];
    public List<string> ReconcileCalls { get; } = [];
    public Func<string, int, int, Task<IReadOnlyList<RelayPost>>>? ListingHandler { get; init; }
    public Func<RelayPost, string, CancellationToken, Task<CrosspostResult>>? CrosspostHandler { get; init; }
    public Func<string, string, CancellationToken, Task<CrosspostResult?>>? ReconcileHandler { get; init; }

    public Task<IdentityResult> ValidateAsync(
        string destinationSubreddit,
        CancellationToken cancellationToken) => Task.FromResult(new IdentityResult(
            "relay_operator",
            ModeratesDestination: true,
            IsSubscribedToDestination: true,
            DestinationKind: "private"));

    public Task<IReadOnlyList<RelayPost>> GetNewPostsAsync(
        string sourceSubreddit,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pollCounts.TryGetValue(sourceSubreddit, out var count);
        count++;
        _pollCounts[sourceSubreddit] = count;
        ListingCalls.Add(sourceSubreddit);
        if (ListingHandler is not null)
        {
            return ListingHandler(sourceSubreddit, limit, count);
        }
        return Task.FromResult<IReadOnlyList<RelayPost>>(
            _posts(sourceSubreddit, limit, count).Take(limit).ToList());
    }

    public Task<CrosspostResult> CrosspostAsync(
        RelayPost post,
        string destinationSubreddit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CrosspostCalls.Add(post);
        return CrosspostHandler?.Invoke(post, destinationSubreddit, cancellationToken) ??
               Task.FromResult(new CrosspostResult(
                   $"t3_destination_{post.Id}",
                   $"https://www.reddit.com/r/{destinationSubreddit}/comments/{post.Id}"));
    }

    public Task<CrosspostResult?> FindRecentCrosspostAsync(
        string sourceFullname,
        string destinationSubreddit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReconcileCalls.Add(sourceFullname);
        return ReconcileHandler?.Invoke(sourceFullname, destinationSubreddit, cancellationToken) ??
               Task.FromResult<CrosspostResult?>(null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<
        HttpRequestMessage,
        CancellationToken,
        Task<HttpResponseMessage>> _handler;

    public FakeHttpMessageHandler(Func<
        HttpRequestMessage,
        CancellationToken,
        Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => _handler(request, cancellationToken);
}

internal sealed class MutableClock
{
    public MutableClock(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; private set; }

    public void Advance(TimeSpan amount) => Now += amount;
}

internal static class Check
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new TestFailureException(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected condition to be false.");

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException($"Expected <{expected}> but found <{actual}>.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var expectedList = expected.ToList();
        var actualList = actual.ToList();
        if (!expectedList.SequenceEqual(actualList))
        {
            throw new TestFailureException(
                $"Expected sequence [{string.Join(", ", expectedList)}] but found " +
                $"[{string.Join(", ", actualList)}].");
        }
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
        {
            throw new TestFailureException(
                $"Expected text to contain <{expectedSubstring}> but found <{actual}>.");
        }
    }

    public static void DoesNotContain(string forbiddenSubstring, string actual)
    {
        if (actual.Contains(forbiddenSubstring, StringComparison.Ordinal))
        {
            throw new TestFailureException($"Text unexpectedly contained <{forbiddenSubstring}>.");
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new TestFailureException(
                $"Expected {typeof(TException).Name} but found {exception.GetType().Name}.");
        }
        throw new TestFailureException($"Expected {typeof(TException).Name} to be thrown.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new TestFailureException(
                $"Expected {typeof(TException).Name} but found {exception.GetType().Name}.");
        }
        throw new TestFailureException($"Expected {typeof(TException).Name} to be thrown.");
    }
}

internal sealed class TestFailureException : Exception
{
    public TestFailureException(string message) : base(message)
    {
    }
}
