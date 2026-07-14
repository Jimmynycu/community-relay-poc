using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace CommunityRelay;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path;
    private readonly object _sync = new();
    private RelayStateDocument _document;

    public StateStore(string? path = null)
    {
        _path = path ?? AppPaths.StateFile;
        // The desktop app acquires its single-instance mutex before constructing
        // this store, so no legitimate state writer can own one of these files.
        // Removing exact GUID-named siblings prevents a crashed atomic write from
        // retaining raw Reddit post IDs outside the canonical 48-hour purge path.
        AtomicFiles.DeleteTemporarySiblings(_path);
        _document = Load();
    }

    public bool IsSourceInitialized(string stateNamespace, string source)
    {
        lock (_sync)
        {
            return _document.InitializedSources.ContainsKey(SourceKey(stateNamespace, source));
        }
    }

    public DateTimeOffset? GetApiNotBefore(bool isDemo)
    {
        lock (_sync)
        {
            return _document.ApiNotBefore.TryGetValue(ApiPauseKey(isDemo), out var notBefore)
                ? notBefore
                : null;
        }
    }

    public void ExtendApiPause(bool isDemo, DateTimeOffset notBefore)
    {
        lock (_sync)
        {
            var key = ApiPauseKey(isDemo);
            if (_document.ApiNotBefore.TryGetValue(key, out var existing) &&
                existing >= notBefore)
            {
                return;
            }

            _document.ApiNotBefore[key] = notBefore;
            SaveUnsafe();
        }
    }

    public DateTimeOffset? GetLastListingReadAt(bool isDemo) =>
        GetApiTimestamp(_document => _document.LastListingReadAt, isDemo);

    public void RecordListingReadAt(bool isDemo, DateTimeOffset attemptedAt) =>
        RecordApiTimestamp(_document => _document.LastListingReadAt, isDemo, attemptedAt);

    public DateTimeOffset? GetLastSubmissionAt(bool isDemo) =>
        GetApiTimestamp(_document => _document.LastSubmissionAt, isDemo);

    public void RecordSubmissionAt(bool isDemo, DateTimeOffset attemptedAt) =>
        RecordApiTimestamp(_document => _document.LastSubmissionAt, isDemo, attemptedAt);

    public DateTimeOffset? GetSourceInitializedAt(string stateNamespace, string source)
    {
        lock (_sync)
        {
            return _document.InitializedSources.TryGetValue(
                SourceKey(stateNamespace, source),
                out var initializedAt)
                ? initializedAt
                : null;
        }
    }

    public void MarkSourceInitialized(string stateNamespace, string source, DateTimeOffset now)
    {
        lock (_sync)
        {
            _document.InitializedSources[SourceKey(stateNamespace, source)] = now;
            SaveUnsafe();
        }
    }

    public void BaselineSource(
        string stateNamespace,
        string source,
        IEnumerable<RelayPost> posts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(posts);
        BaselineSources(
            stateNamespace,
            [new SourceBaseline(source, posts.ToList(), now)]);
    }

    public void BaselineSources(
        string stateNamespace,
        IEnumerable<SourceBaseline> baselines)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        var materializedBaselines = baselines
            .Select(baseline =>
            {
                ArgumentNullException.ThrowIfNull(baseline);
                ArgumentNullException.ThrowIfNull(baseline.Posts);
                if (string.IsNullOrWhiteSpace(baseline.Source))
                {
                    throw new ArgumentException("A baseline source name is required.", nameof(baselines));
                }
                return new SourceBaseline(
                    baseline.Source,
                    baseline.Posts.ToList(),
                    baseline.InitializedAt);
            })
            .ToList();
        if (materializedBaselines.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var baseline in materializedBaselines)
            {
                foreach (var post in baseline.Posts)
                {
                    MarkUnsafe(
                        stateNamespace,
                        new PostStateUpdate(
                            post,
                            RelayOutcome.Baseline,
                            baseline.InitializedAt));
                }
                _document.InitializedSources[
                    SourceKey(stateNamespace, baseline.Source)] = baseline.InitializedAt;
            }
            SaveUnsafe();
        }
    }

    public bool ShouldProcess(string stateNamespace, string fullname, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_document.CompletedFingerprints.ContainsKey(FingerprintKey(stateNamespace, fullname)))
            {
                return false;
            }
            if (!_document.Posts.TryGetValue(PostKey(stateNamespace, fullname), out var state))
            {
                return true;
            }
            return state.Outcome switch
            {
                RelayOutcome.Retry => state.NextRetryAt <= now,
                RelayOutcome.Submitting => true,
                _ => false
            };
        }
    }

    public bool WasForwarded(string stateNamespace, string fullname)
    {
        lock (_sync)
        {
            return _document.CompletedFingerprints.TryGetValue(
                       FingerprintKey(stateNamespace, fullname),
                       out var fingerprint) &&
                   fingerprint.Outcome == RelayOutcome.Forwarded;
        }
    }

    public int GetAttemptCount(string stateNamespace, string fullname)
    {
        lock (_sync)
        {
            return _document.Posts.TryGetValue(PostKey(stateNamespace, fullname), out var state)
                ? state.AttemptCount
                : 0;
        }
    }

    public RelayOutcome? GetOutcome(string stateNamespace, string fullname)
    {
        lock (_sync)
        {
            return _document.Posts.TryGetValue(PostKey(stateNamespace, fullname), out var state)
                ? state.Outcome
                : null;
        }
    }

    public IReadOnlyList<PersistedPostState> GetPendingSubmissions(string stateNamespace)
    {
        lock (_sync)
        {
            var prefix = $"{stateNamespace}:";
            return _document.Posts
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                               pair.Value.Outcome == RelayOutcome.Submitting)
                .OrderBy(pair => pair.Value.UpdatedAt)
                .Select(pair => ClonePostState(pair.Value))
                .ToList();
        }
    }

    public IReadOnlyList<PersistedPostState> GetPendingReconciliations(
        string stateNamespace,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            var prefix = $"{stateNamespace}:";
            return _document.Posts
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                               (pair.Value.Outcome == RelayOutcome.Submitting ||
                                (pair.Value.Outcome == RelayOutcome.Retry &&
                                 pair.Value.NextRetryAt <= now)))
                .OrderBy(pair => pair.Value.UpdatedAt)
                .Select(pair => ClonePostState(pair.Value))
                .ToList();
        }
    }

    public void Mark(
        string stateNamespace,
        RelayPost post,
        RelayOutcome outcome,
        DateTimeOffset now,
        string? destinationFullname = null,
        string? errorCode = null,
        DateTimeOffset? nextRetryAt = null)
    {
        MarkBatch(
            stateNamespace,
            [new PostStateUpdate(
                post,
                outcome,
                now,
                destinationFullname,
                errorCode,
                nextRetryAt)]);
    }

    public void MarkBatch(string stateNamespace, IEnumerable<PostStateUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var materializedUpdates = updates
            .Select(update =>
            {
                ArgumentNullException.ThrowIfNull(update);
                ArgumentNullException.ThrowIfNull(update.Post);
                return update;
            })
            .ToList();
        if (materializedUpdates.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var update in materializedUpdates)
            {
                MarkUnsafe(stateNamespace, update);
            }
            SaveUnsafe();
        }
    }

    public void RescheduleRetry(
        string stateNamespace,
        RelayPost post,
        DateTimeOffset now,
        string errorCode,
        DateTimeOffset nextRetryAt)
    {
        ArgumentNullException.ThrowIfNull(post);
        RescheduleRetry(
            stateNamespace,
            post.Fullname,
            now,
            errorCode,
            nextRetryAt);
    }

    public void RescheduleRetry(
        string stateNamespace,
        string fullname,
        DateTimeOffset now,
        string errorCode,
        DateTimeOffset nextRetryAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullname);
        lock (_sync)
        {
            var key = PostKey(stateNamespace, fullname);
            if (!_document.Posts.TryGetValue(key, out var existing) ||
                existing.Outcome is not RelayOutcome.Retry and not RelayOutcome.Submitting)
            {
                throw new InvalidOperationException(
                    "Only an existing retry or submitting record can be rescheduled.");
            }

            _document.Posts[key] = new PersistedPostState
            {
                Fullname = existing.Fullname,
                SourceSubreddit = existing.SourceSubreddit,
                Outcome = RelayOutcome.Retry,
                DestinationFullname = existing.DestinationFullname,
                AttemptCount = existing.AttemptCount,
                ErrorCode = errorCode,
                FirstSeenAt = existing.FirstSeenAt,
                UpdatedAt = now,
                NextRetryAt = nextRetryAt
            };
            _document.CompletedFingerprints.Remove(
                FingerprintKey(stateNamespace, fullname));
            SaveUnsafe();
        }
    }

    public int CountForwardedSince(string stateNamespace, DateTimeOffset since)
    {
        lock (_sync)
        {
            var prefix = $"{stateNamespace}:";
            return _document.Posts
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Count(pair => pair.Value.Outcome == RelayOutcome.Forwarded && pair.Value.UpdatedAt >= since);
        }
    }

    public int CountForwardedSinceAcrossNamespaces(bool isDemo, DateTimeOffset since)
    {
        lock (_sync)
        {
            var mode = isDemo ? "demo" : "live";
            return _document.Posts.Count(pair =>
                NamespaceHasMode(pair.Key, mode) &&
                pair.Value.Outcome == RelayOutcome.Forwarded &&
                pair.Value.UpdatedAt >= since);
        }
    }

    public int CountWriteBudgetSinceAcrossNamespaces(bool isDemo, DateTimeOffset since)
    {
        lock (_sync)
        {
            var mode = isDemo ? "demo" : "live";
            return _document.Posts.Count(pair =>
                NamespaceHasMode(pair.Key, mode) &&
                (pair.Value.Outcome is RelayOutcome.Forwarded or
                    RelayOutcome.Submitting or
                    RelayOutcome.Retry or
                    RelayOutcome.TerminalSkip) &&
                pair.Value.UpdatedAt >= since);
        }
    }

    public void PurgeOlderThan(DateTimeOffset cutoff)
    {
        lock (_sync)
        {
            var expiredPosts = _document.Posts
                .Where(pair => pair.Value.FirstSeenAt <= cutoff)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in expiredPosts)
            {
                _document.Posts.Remove(key);
            }
            var expiredFingerprints = _document.CompletedFingerprints
                .Where(pair => pair.Value.UpdatedAt <= cutoff)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in expiredFingerprints)
            {
                _document.CompletedFingerprints.Remove(key);
            }
            if (expiredPosts.Count > 0 || expiredFingerprints.Count > 0)
            {
                SaveUnsafe();
            }
        }
    }

    public void ResetNamespace(string stateNamespace)
    {
        lock (_sync)
        {
            var prefix = $"{stateNamespace}:";
            foreach (var key in _document.Posts.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _document.Posts.Remove(key);
            }
            foreach (var key in _document.InitializedSources.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _document.InitializedSources.Remove(key);
            }
            foreach (var key in _document.CompletedFingerprints.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _document.CompletedFingerprints.Remove(key);
            }
            SaveUnsafe();
        }
    }

    public RelayStateDocument Snapshot()
    {
        lock (_sync)
        {
            var json = JsonSerializer.Serialize(_document, JsonOptions);
            return JsonSerializer.Deserialize<RelayStateDocument>(json, JsonOptions)!;
        }
    }

    private RelayStateDocument Load()
    {
        if (!File.Exists(_path))
        {
            return new RelayStateDocument();
        }
        try
        {
            var document = JsonSerializer.Deserialize<RelayStateDocument>(
                File.ReadAllText(_path),
                JsonOptions) ?? new RelayStateDocument();
            document.ApiNotBefore = new Dictionary<string, DateTimeOffset>(
                document.ApiNotBefore ?? new Dictionary<string, DateTimeOffset>(),
                StringComparer.OrdinalIgnoreCase);
            document.LastListingReadAt = new Dictionary<string, DateTimeOffset>(
                document.LastListingReadAt ?? new Dictionary<string, DateTimeOffset>(),
                StringComparer.OrdinalIgnoreCase);
            document.LastSubmissionAt = new Dictionary<string, DateTimeOffset>(
                document.LastSubmissionAt ?? new Dictionary<string, DateTimeOffset>(),
                StringComparer.OrdinalIgnoreCase);
            document.InitializedSources = new Dictionary<string, DateTimeOffset>(
                document.InitializedSources ?? new Dictionary<string, DateTimeOffset>(),
                StringComparer.OrdinalIgnoreCase);
            document.Posts = new Dictionary<string, PersistedPostState>(
                document.Posts ?? new Dictionary<string, PersistedPostState>(),
                StringComparer.OrdinalIgnoreCase);
            document.CompletedFingerprints = new Dictionary<string, PersistedFingerprintState>(
                document.CompletedFingerprints ??
                new Dictionary<string, PersistedFingerprintState>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in document.Posts.Where(pair =>
                         pair.Value.Outcome is not RelayOutcome.Retry and not RelayOutcome.Submitting))
            {
                var separator = pair.Key.IndexOf(':');
                var stateNamespace = separator > 0 ? pair.Key[..separator] : "live";
                var fingerprintKey = FingerprintKey(stateNamespace, pair.Value.Fullname);
                var retentionAnchor = pair.Value.FirstSeenAt == default
                    ? pair.Value.UpdatedAt
                    : pair.Value.FirstSeenAt;
                if (!document.CompletedFingerprints.TryGetValue(
                        fingerprintKey,
                        out var fingerprint) ||
                    fingerprint.UpdatedAt == default ||
                    retentionAnchor < fingerprint.UpdatedAt ||
                    fingerprint.Outcome != pair.Value.Outcome)
                {
                    document.CompletedFingerprints[fingerprintKey] = new PersistedFingerprintState
                    {
                        Outcome = pair.Value.Outcome,
                        // Kept under the legacy JSON name for migration compatibility;
                        // this is the first-collection retention anchor, not a sliding TTL.
                        UpdatedAt = fingerprint is null || fingerprint.UpdatedAt == default
                            ? retentionAnchor
                            : fingerprint.UpdatedAt < retentionAnchor
                                ? fingerprint.UpdatedAt
                                : retentionAnchor
                    };
                }
            }
            document.SchemaVersion = 5;
            return document;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException(
                "The relay state file is unreadable. Move or delete it before restarting.",
                exception);
        }
    }

    private void SaveUnsafe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFiles.WriteAllText(_path, JsonSerializer.Serialize(_document, JsonOptions));
    }

    private void MarkUnsafe(string stateNamespace, PostStateUpdate update)
    {
        var key = PostKey(stateNamespace, update.Post.Fullname);
        _document.Posts.TryGetValue(key, out var existing);
        var persisted = new PersistedPostState
        {
            Fullname = update.Post.Fullname,
            SourceSubreddit = update.Post.SourceSubreddit,
            Outcome = update.Outcome,
            DestinationFullname = update.DestinationFullname ?? existing?.DestinationFullname,
            AttemptCount = (existing?.AttemptCount ?? 0) +
                           (update.Outcome == RelayOutcome.Retry ? 1 : 0),
            ErrorCode = update.ErrorCode,
            FirstSeenAt = existing?.FirstSeenAt ?? update.UpdatedAt,
            UpdatedAt = update.UpdatedAt,
            NextRetryAt = update.NextRetryAt
        };
        _document.Posts[key] = persisted;
        if (update.Outcome is not RelayOutcome.Retry and not RelayOutcome.Submitting)
        {
            _document.CompletedFingerprints[
                FingerprintKey(stateNamespace, update.Post.Fullname)] =
                new PersistedFingerprintState
                {
                    Outcome = update.Outcome,
                    // A later state transition must not extend post-ID retention.
                    UpdatedAt = persisted.FirstSeenAt
                };
        }
    }

    private static PersistedPostState ClonePostState(PersistedPostState state) => new()
    {
        Fullname = state.Fullname,
        SourceSubreddit = state.SourceSubreddit,
        Outcome = state.Outcome,
        DestinationFullname = state.DestinationFullname,
        AttemptCount = state.AttemptCount,
        ErrorCode = state.ErrorCode,
        FirstSeenAt = state.FirstSeenAt,
        UpdatedAt = state.UpdatedAt,
        NextRetryAt = state.NextRetryAt
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new PersistedFingerprintStateJsonConverter());
        return options;
    }

    private static string SourceKey(string stateNamespace, string source) =>
        $"{stateNamespace}:{source.ToLowerInvariant()}";

    private static string ApiPauseKey(bool isDemo) => isDemo ? "demo" : "live";

    private DateTimeOffset? GetApiTimestamp(
        Func<RelayStateDocument, Dictionary<string, DateTimeOffset>> selector,
        bool isDemo)
    {
        lock (_sync)
        {
            var timestamps = selector(_document);
            return timestamps.TryGetValue(ApiPauseKey(isDemo), out var timestamp)
                ? timestamp
                : null;
        }
    }

    private void RecordApiTimestamp(
        Func<RelayStateDocument, Dictionary<string, DateTimeOffset>> selector,
        bool isDemo,
        DateTimeOffset attemptedAt)
    {
        lock (_sync)
        {
            var timestamps = selector(_document);
            var key = ApiPauseKey(isDemo);
            if (timestamps.TryGetValue(key, out var existing) && existing >= attemptedAt)
            {
                return;
            }
            timestamps[key] = attemptedAt;
            SaveUnsafe();
        }
    }

    private static bool NamespaceHasMode(string stateKey, string mode)
    {
        var separator = stateKey.IndexOf(':');
        var stateNamespace = separator >= 0 ? stateKey[..separator] : stateKey;
        return stateNamespace.Equals(mode, StringComparison.OrdinalIgnoreCase) ||
               stateNamespace.StartsWith($"{mode}-", StringComparison.OrdinalIgnoreCase);
    }

    private static string PostKey(string stateNamespace, string fullname) =>
        $"{stateNamespace}:{fullname.ToLowerInvariant()}";

    private static string FingerprintKey(string stateNamespace, string fullname)
    {
        var normalized = fullname.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"{stateNamespace}:{hash}";
    }

    private sealed class PersistedFingerprintStateJsonConverter :
        JsonConverter<PersistedFingerprintState>
    {
        public override PersistedFingerprintState Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.Number or JsonTokenType.String)
            {
                return new PersistedFingerprintState
                {
                    Outcome = ReadOutcome(ref reader),
                    UpdatedAt = default
                };
            }
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Invalid completed fingerprint state.");
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var outcomeProperty = root.EnumerateObject().FirstOrDefault(
                property => property.Name.Equals("Outcome", StringComparison.OrdinalIgnoreCase));
            if (outcomeProperty.Equals(default(JsonProperty)))
            {
                throw new JsonException("Completed fingerprint outcome is missing.");
            }

            var outcome = ReadOutcome(outcomeProperty.Value);
            var updatedAtProperty = root.EnumerateObject().FirstOrDefault(
                property => property.Name.Equals("UpdatedAt", StringComparison.OrdinalIgnoreCase));
            var updatedAt = updatedAtProperty.Equals(default(JsonProperty))
                ? default
                : updatedAtProperty.Value.GetDateTimeOffset();
            return new PersistedFingerprintState { Outcome = outcome, UpdatedAt = updatedAt };
        }

        public override void Write(
            Utf8JsonWriter writer,
            PersistedFingerprintState value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("Outcome", (int)value.Outcome);
            writer.WriteString("UpdatedAt", value.UpdatedAt);
            writer.WriteEndObject();
        }

        private static RelayOutcome ReadOutcome(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
            {
                return ToOutcome(numeric);
            }
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<RelayOutcome>(reader.GetString(), true, out var named))
            {
                return named;
            }
            throw new JsonException("Invalid completed fingerprint outcome.");
        }

        private static RelayOutcome ReadOutcome(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
            {
                return ToOutcome(numeric);
            }
            if (element.ValueKind == JsonValueKind.String &&
                Enum.TryParse<RelayOutcome>(element.GetString(), true, out var named))
            {
                return named;
            }
            throw new JsonException("Invalid completed fingerprint outcome.");
        }

        private static RelayOutcome ToOutcome(int numeric) =>
            Enum.IsDefined(typeof(RelayOutcome), numeric)
                ? (RelayOutcome)numeric
                : throw new JsonException("Invalid completed fingerprint outcome.");
    }
}
