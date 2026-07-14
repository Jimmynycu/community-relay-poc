export const ALLOWED_SOURCES = ['cats', 'dogs'] as const;
export const LAST_RESULT_KEY = 'relay:v1:last-result';
export const ATTENTION_INDEX_KEY = 'relay:v1:attention';

const KEY_PREFIX = 'relay:v1';
const FETCH_LIMIT = 25;
const MAX_CROSSPOSTS_PER_RUN = 2;
const SEEN_TTL_MS = 90 * 24 * 60 * 60 * 1000;
const LOCK_TTL_MS = 50 * 1000;

export type AllowedSource = (typeof ALLOWED_SOURCES)[number];

export type RelaySettings = {
  enabled: boolean;
  destinationConfirmation: string;
  sources: AllowedSource[];
  maxPerHour: number;
  maxPerDay: number;
};

export type RelayPost = {
  id: string;
  title: string;
  subredditName: string;
  createdAt: Date;
  nsfw: boolean;
  stickied: boolean;
  removed: boolean;
  spam: boolean;
  quarantined: boolean;
  spoiler: boolean;
  crosspost(options: {
    title: string;
    subredditName: string;
    sendreplies: boolean;
    nsfw: boolean;
    spoiler: boolean;
    runAs: 'APP';
  }): Promise<{ id: string }>;
};

export type RelayStore = {
  get(key: string): Promise<string | undefined>;
  set(
    key: string,
    value: string,
    options?: {
      nx?: boolean;
      xx?: boolean;
      expiration?: Date;
    },
  ): Promise<string>;
  del(...keys: string[]): Promise<void>;
  incrBy(key: string, value: number): Promise<number>;
  expire(key: string, seconds: number): Promise<void>;
  hSet(key: string, fieldValues: Record<string, string>): Promise<number>;
  hGetAll(key: string): Promise<Record<string, string>>;
  hDel(key: string, fields: string[]): Promise<number>;
};

export type RelayRunDependencies = {
  destination: string;
  settings: RelaySettings;
  store: RelayStore;
  fetchNewPosts(source: AllowedSource, limit: number): Promise<RelayPost[]>;
  now?: () => Date;
  randomToken?: () => string;
  log?: (message: string, fields?: Readonly<Record<string, unknown>>) => void;
};

export type RelayRunResult = {
  status:
    | 'disabled'
    | 'destination-mismatch'
    | 'no-sources'
    | 'locked'
    | 'ok';
  destination: string;
  sources: AllowedSource[];
  baselinedSources: AllowedSource[];
  fetched: number;
  eligible: number;
  attempted: number;
  crossposted: number;
  uncertain: number;
  attentionRequired: number;
  skippedUnsafe: number;
  skippedDuplicate: number;
  capped: boolean;
  hourlyCount: number;
  dailyCount: number;
  errors: string[];
  completedAt: string;
};

type SourceBatch = {
  source: AllowedSource;
  posts: RelayPost[];
  baselined: boolean;
};

type SourceCursor = {
  initializedAt: string;
  highWaterCreatedAt: string;
  highWaterPostIds: string[];
};

type AttentionRecord = {
  state:
    | 'reserved'
    | 'dispatching'
    | 'uncertain'
    | 'not-dispatched'
    | 'known-success-persistence-error';
  source: string;
  sourcePostId: string;
  destination: string;
  updatedAt: string;
  detail?: string;
};

export function normalizeSubredditName(value: string): string {
  return value.trim().replace(/^r\//i, '').toLowerCase();
}

export function loadRelaySettings(raw: Readonly<Record<string, unknown>>): RelaySettings {
  const rawSources = Array.isArray(raw.sourceSubreddits)
    ? raw.sourceSubreddits
    : ALLOWED_SOURCES;
  const sources = ALLOWED_SOURCES.filter((source) => rawSources.includes(source));

  return {
    enabled: raw.relayEnabled === true,
    destinationConfirmation:
      typeof raw.destinationConfirmation === 'string'
        ? raw.destinationConfirmation
        : '',
    sources,
    maxPerHour: boundedInteger(raw.maxCrosspostsPerHour, 1, 1, 12),
    maxPerDay: boundedInteger(raw.maxCrosspostsPerDay, 1, 1, 48),
  };
}

export function isIntegerInRange(
  value: unknown,
  minimum: number,
  maximum: number,
): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= minimum &&
    value <= maximum
  );
}

export async function runRelayOnce(
  dependencies: RelayRunDependencies,
): Promise<RelayRunResult> {
  const now = dependencies.now ?? (() => new Date());
  const randomToken = dependencies.randomToken ?? (() => crypto.randomUUID());
  const log = dependencies.log ?? (() => undefined);
  const startedAt = now();
  const destination = normalizeSubredditName(dependencies.destination);
  const result = newResult(destination, dependencies.settings.sources, startedAt);

  if (!dependencies.settings.enabled) {
    result.status = 'disabled';
    return finish(result, now());
  }

  if (
    normalizeSubredditName(dependencies.settings.destinationConfirmation) !==
    destination
  ) {
    result.status = 'destination-mismatch';
    return finish(result, now());
  }

  const sources = dependencies.settings.sources.filter(
    (source) => normalizeSubredditName(source) !== destination,
  );
  result.sources = sources;
  if (sources.length === 0) {
    result.status = 'no-sources';
    return finish(result, now());
  }

  const lockToken = randomToken();
  const lockAcquired = await dependencies.store.set(lockKey(destination), lockToken, {
    nx: true,
    expiration: new Date(startedAt.getTime() + LOCK_TTL_MS),
  });
  if (!lockAcquired) {
    result.status = 'locked';
    return finish(result, now());
  }

  try {
    const batches = await fetchSourceBatches(
      sources,
      dependencies.store,
      dependencies.fetchNewPosts,
      startedAt,
      result,
      log,
    );
    const candidates = await collectCandidates(
      batches,
      dependencies.store,
      startedAt,
      result,
    );

    const hourKey = hourlyCounterKey(startedAt);
    const dayKey = dailyCounterKey(startedAt);
    let hourlyCount = await readCounter(dependencies.store, hourKey);
    let dailyCount = await readCounter(dependencies.store, dayKey);
    result.hourlyCount = hourlyCount;
    result.dailyCount = dailyCount;

    for (const post of candidates) {
      if (
        result.attempted >= MAX_CROSSPOSTS_PER_RUN ||
        hourlyCount >= dependencies.settings.maxPerHour ||
        dailyCount >= dependencies.settings.maxPerDay
      ) {
        result.capped = true;
        break;
      }

      const claimed = await dependencies.store.set(
        seenKey(post.id),
        JSON.stringify({
          state: 'reserved',
          source: normalizeSubredditName(post.subredditName),
          destination,
          reservedAt: startedAt.toISOString(),
        }),
        { nx: true },
      );
      if (!claimed) {
        result.skippedDuplicate += 1;
        continue;
      }

      const baseAttention: Omit<AttentionRecord, 'state' | 'updatedAt'> = {
        source: normalizeSubredditName(post.subredditName),
        sourcePostId: post.id,
        destination,
      };

      try {
        await writeAttention(dependencies.store, post.id, {
          ...baseAttention,
          state: 'reserved',
          updatedAt: now().toISOString(),
        });

        // Both quota counters are consumed before Reddit is called. A rejected
        // or ambiguous write still counts as an attempt and cannot be retried.
        hourlyCount = await incrementCounter(dependencies.store, hourKey, 3 * 60 * 60);
        result.hourlyCount = hourlyCount;
        dailyCount = await incrementCounter(dependencies.store, dayKey, 3 * 24 * 60 * 60);
        result.dailyCount = dailyCount;

        await writeAttention(dependencies.store, post.id, {
          ...baseAttention,
          state: 'dispatching',
          updatedAt: now().toISOString(),
        });
      } catch (error) {
        const message = safeError(error);
        result.errors.push(
          `${post.subredditName}/${post.id}: pre-dispatch state failure: ${message}`,
        );
        result.attentionRequired += 1;
        await bestEffortSetTerminal(
          dependencies.store,
          seenKey(post.id),
          JSON.stringify({
            state: 'not-dispatched',
            source: normalizeSubredditName(post.subredditName),
            destination,
            stoppedAt: now().toISOString(),
            detail: message,
          }),
          log,
        );
        await bestEffortAttention(
          dependencies.store,
          post.id,
          {
            ...baseAttention,
            state: 'not-dispatched',
            updatedAt: now().toISOString(),
            detail: message,
          },
          log,
        );
        // Redis is part of the at-most-once safety boundary. Do not dispatch
        // this or later posts when reservation/counter persistence is unhealthy.
        break;
      }

      result.attempted += 1;
      let created: { id: string };
      try {
        // This is deliberately the native Reddit crosspost method. The relay
        // never copies a post body, media URL, or author into a new self/link post.
        created = await post.crosspost({
          title: post.title,
          subredditName: destination,
          sendreplies: false,
          nsfw: false,
          spoiler: post.spoiler,
          runAs: 'APP',
        });
      } catch (error) {
        const message = safeError(error);
        result.errors.push(`${post.subredditName}/${post.id}: ${message}`);
        result.uncertain += 1;
        result.attentionRequired += 1;
        await bestEffortSetTerminal(
          dependencies.store,
          seenKey(post.id),
          JSON.stringify({
            state: 'uncertain',
            source: normalizeSubredditName(post.subredditName),
            destination,
            attemptedAt: startedAt.toISOString(),
            detail: message,
          }),
          log,
        );
        await bestEffortAttention(
          dependencies.store,
          post.id,
          {
            ...baseAttention,
            state: 'uncertain',
            updatedAt: now().toISOString(),
            detail: message,
          },
          log,
        );
        log('crosspost-uncertain', {
          source: post.subredditName,
          sourcePostId: post.id,
          error: message,
        });
        continue;
      }

      result.crossposted += 1;
      try {
        const completed = await dependencies.store.set(
          seenKey(post.id),
          JSON.stringify({
            state: 'crossposted',
            source: normalizeSubredditName(post.subredditName),
            destination,
            destinationPostId: created.id,
            completedAt: now().toISOString(),
          }),
          {
            xx: true,
            expiration: new Date(startedAt.getTime() + SEEN_TTL_MS),
          },
        );
        if (!completed) throw new Error('durable reservation disappeared');
        await dependencies.store.hDel(ATTENTION_INDEX_KEY, [post.id]);
      } catch (error) {
        const message = safeError(error);
        result.errors.push(
          `${post.subredditName}/${post.id}: crosspost succeeded but state persistence failed: ${message}`,
        );
        result.attentionRequired += 1;
        await bestEffortAttention(
          dependencies.store,
          post.id,
          {
            ...baseAttention,
            state: 'known-success-persistence-error',
            updatedAt: now().toISOString(),
            detail: `${created.id}: ${message}`,
          },
          log,
        );
      }

      log('crosspost-created', {
        source: post.subredditName,
        sourcePostId: post.id,
        destination,
        destinationPostId: created.id,
      });
    }

    return finish(result, now());
  } finally {
    const currentLock = await dependencies.store.get(lockKey(destination));
    if (currentLock === lockToken) {
      await dependencies.store.del(lockKey(destination));
    }
  }
}

async function fetchSourceBatches(
  sources: AllowedSource[],
  store: RelayStore,
  fetchNewPosts: RelayRunDependencies['fetchNewPosts'],
  now: Date,
  result: RelayRunResult,
  log: NonNullable<RelayRunDependencies['log']>,
): Promise<SourceBatch[]> {
  const settled = await Promise.allSettled(
    sources.map(async (source): Promise<SourceBatch> => {
      const [baseline, posts] = await Promise.all([
        store.get(baselineKey(source)),
        fetchNewPosts(source, FETCH_LIMIT),
      ]);
      result.fetched += posts.length;

      const cursor = parseSourceCursor(baseline);
      if (cursor === undefined) {
        await Promise.all(
          posts.map((post) =>
            store.set(seenKey(post.id), `baseline:${source}`, {
              nx: true,
              expiration: new Date(now.getTime() + SEEN_TTL_MS),
            }),
          ),
        );
        await store.set(
          baselineKey(source),
          JSON.stringify(createBaselineCursor(posts, now)),
        );
        result.baselinedSources.push(source);
        log(baseline === undefined ? 'source-baselined' : 'source-rebaselined', {
          source,
          count: posts.length,
        });
        return { source, posts: [], baselined: true };
      }

      // Determine candidates against the previous cursor, then persist the
      // advanced observation high-water before any Reddit write can occur.
      // This intentionally favors at-most-once delivery over lossless backlog.
      const newlyObserved = posts.filter((post) => isAfterCursor(post, cursor));
      await store.set(
        baselineKey(source),
        JSON.stringify(advanceCursor(cursor, posts)),
      );
      return { source, posts: newlyObserved, baselined: false };
    }),
  );

  const batches: SourceBatch[] = [];
  for (let index = 0; index < settled.length; index += 1) {
    const outcome = settled[index];
    const source = sources[index];
    if (outcome?.status === 'fulfilled') {
      batches.push(outcome.value);
    } else if (source !== undefined) {
      const message = safeError(outcome?.reason);
      result.errors.push(`${source}: ${message}`);
      log('source-poll-error', { source, error: message });
    }
  }
  return batches;
}

async function collectCandidates(
  batches: SourceBatch[],
  store: RelayStore,
  now: Date,
  result: RelayRunResult,
): Promise<RelayPost[]> {
  const candidates: RelayPost[] = [];
  const ids = new Set<string>();

  for (const batch of batches) {
    if (batch.baselined) continue;
    for (const post of batch.posts) {
      if (ids.has(post.id)) continue;
      ids.add(post.id);

      if (isUnsafe(post)) {
        const recorded = await store.set(seenKey(post.id), 'skipped:unsafe', {
          nx: true,
          expiration: new Date(now.getTime() + SEEN_TTL_MS),
        });
        if (recorded) result.skippedUnsafe += 1;
        else result.skippedDuplicate += 1;
        continue;
      }

      result.eligible += 1;
      candidates.push(post);
    }
  }

  candidates.sort((left, right) => {
    const leftTime = left.createdAt.getTime();
    const rightTime = right.createdAt.getTime();
    return leftTime - rightTime || left.id.localeCompare(right.id);
  });
  return candidates;
}

function parseSourceCursor(value: string | undefined): SourceCursor | undefined {
  if (!value) return undefined;
  try {
    const parsed = JSON.parse(value) as Partial<SourceCursor>;
    const initializedAt = Date.parse(parsed.initializedAt ?? '');
    const highWaterCreatedAt = Date.parse(parsed.highWaterCreatedAt ?? '');
    if (
      !Number.isFinite(initializedAt) ||
      !Number.isFinite(highWaterCreatedAt) ||
      highWaterCreatedAt < initializedAt ||
      !Array.isArray(parsed.highWaterPostIds) ||
      !parsed.highWaterPostIds.every((id) => typeof id === 'string')
    ) {
      return undefined;
    }
    return {
      initializedAt: new Date(initializedAt).toISOString(),
      highWaterCreatedAt: new Date(highWaterCreatedAt).toISOString(),
      highWaterPostIds: [...new Set(parsed.highWaterPostIds)],
    };
  } catch {
    return undefined;
  }
}

function createBaselineCursor(posts: RelayPost[], now: Date): SourceCursor {
  let highWater = now.getTime();
  let highWaterPostIds: string[] = [];
  for (const post of posts) {
    const createdAt = post.createdAt.getTime();
    if (!Number.isFinite(createdAt)) continue;
    if (createdAt > highWater) {
      highWater = createdAt;
      highWaterPostIds = [post.id];
    } else if (createdAt === highWater) {
      highWaterPostIds.push(post.id);
    }
  }
  return {
    initializedAt: now.toISOString(),
    highWaterCreatedAt: new Date(highWater).toISOString(),
    highWaterPostIds: [...new Set(highWaterPostIds)],
  };
}

function isAfterCursor(post: RelayPost, cursor: SourceCursor): boolean {
  const createdAt = post.createdAt.getTime();
  const initializedAt = Date.parse(cursor.initializedAt);
  const highWater = Date.parse(cursor.highWaterCreatedAt);
  if (!Number.isFinite(createdAt) || createdAt <= initializedAt) return false;
  if (createdAt > highWater) return true;
  return createdAt === highWater && !cursor.highWaterPostIds.includes(post.id);
}

function advanceCursor(cursor: SourceCursor, posts: RelayPost[]): SourceCursor {
  let highWater = Date.parse(cursor.highWaterCreatedAt);
  let highWaterPostIds = new Set(cursor.highWaterPostIds);
  for (const post of posts) {
    const createdAt = post.createdAt.getTime();
    if (!Number.isFinite(createdAt)) continue;
    if (createdAt > highWater) {
      highWater = createdAt;
      highWaterPostIds = new Set([post.id]);
    } else if (createdAt === highWater) {
      highWaterPostIds.add(post.id);
    }
  }
  return {
    initializedAt: cursor.initializedAt,
    highWaterCreatedAt: new Date(highWater).toISOString(),
    highWaterPostIds: [...highWaterPostIds],
  };
}

function isUnsafe(post: RelayPost): boolean {
  return (
    post.nsfw ||
    post.stickied ||
    post.removed ||
    post.spam ||
    post.quarantined
  );
}

function newResult(
  destination: string,
  sources: AllowedSource[],
  now: Date,
): RelayRunResult {
  return {
    status: 'ok',
    destination,
    sources: [...sources],
    baselinedSources: [],
    fetched: 0,
    eligible: 0,
    attempted: 0,
    crossposted: 0,
    uncertain: 0,
    attentionRequired: 0,
    skippedUnsafe: 0,
    skippedDuplicate: 0,
    capped: false,
    hourlyCount: 0,
    dailyCount: 0,
    errors: [],
    completedAt: now.toISOString(),
  };
}

function finish(result: RelayRunResult, now: Date): RelayRunResult {
  result.completedAt = now.toISOString();
  return result;
}

function boundedInteger(
  value: unknown,
  fallback: number,
  minimum: number,
  maximum: number,
): number {
  return isIntegerInRange(value, minimum, maximum) ? value : fallback;
}

function safeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error ?? 'unknown error');
}

function baselineKey(source: AllowedSource): string {
  return `${KEY_PREFIX}:baseline:${source}`;
}

function seenKey(postId: string): string {
  return `${KEY_PREFIX}:seen:${postId}`;
}

function lockKey(destination: string): string {
  return `${KEY_PREFIX}:lock:${destination}`;
}

function hourlyCounterKey(now: Date): string {
  return `${KEY_PREFIX}:count:hour:${now.toISOString().slice(0, 13)}`;
}

function dailyCounterKey(now: Date): string {
  return `${KEY_PREFIX}:count:day:${now.toISOString().slice(0, 10)}`;
}

async function readCounter(store: RelayStore, key: string): Promise<number> {
  const value = Number.parseInt((await store.get(key)) ?? '0', 10);
  return Number.isFinite(value) && value > 0 ? value : 0;
}

async function incrementCounter(
  store: RelayStore,
  key: string,
  expiresInSeconds: number,
): Promise<number> {
  const value = await store.incrBy(key, 1);
  await store.expire(key, expiresInSeconds);
  return value;
}

async function writeAttention(
  store: RelayStore,
  postId: string,
  record: AttentionRecord,
): Promise<void> {
  await store.hSet(ATTENTION_INDEX_KEY, {
    [postId]: JSON.stringify(record),
  });
}

async function bestEffortAttention(
  store: RelayStore,
  postId: string,
  record: AttentionRecord,
  log: NonNullable<RelayRunDependencies['log']>,
): Promise<void> {
  try {
    await writeAttention(store, postId, record);
  } catch (error) {
    log('attention-record-error', {
      sourcePostId: postId,
      error: safeError(error),
    });
  }
}

async function bestEffortSetTerminal(
  store: RelayStore,
  key: string,
  value: string,
  log: NonNullable<RelayRunDependencies['log']>,
): Promise<void> {
  try {
    const updated = await store.set(key, value, { xx: true });
    if (!updated) throw new Error('durable reservation disappeared');
  } catch (error) {
    log('terminal-state-error', { key, error: safeError(error) });
  }
}
