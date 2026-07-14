import assert from 'node:assert/strict';
import test from 'node:test';
import {
  ATTENTION_INDEX_KEY,
  loadRelaySettings,
  runRelayOnce,
  type RelayPost,
  type RelaySettings,
  type RelayStore,
} from './relay.ts';

type Entry = { value: string; expiration?: Date };
type CrosspostCall = {
  id: string;
  options: Parameters<RelayPost['crosspost']>[0];
};

class FakeStore implements RelayStore {
  readonly entries = new Map<string, Entry>();
  readonly hashes = new Map<string, Map<string, string>>();
  readonly failures = new Set<string>();
  clockMs: number;

  constructor(now = fixedNow) {
    this.clockMs = now.getTime();
  }

  advance(milliseconds: number): void {
    this.clockMs += milliseconds;
  }

  now(): Date {
    return new Date(this.clockMs);
  }

  async get(key: string): Promise<string | undefined> {
    this.maybeFail('get', key);
    this.purgeExpired(key);
    return this.entries.get(key)?.value;
  }

  async set(
    key: string,
    value: string,
    options?: { nx?: boolean; xx?: boolean; expiration?: Date },
  ): Promise<string> {
    this.maybeFail('set', key);
    this.purgeExpired(key);
    const exists = this.entries.has(key);
    if (options?.nx && exists) return '';
    if (options?.xx && !exists) return '';
    this.entries.set(key, { value, expiration: options?.expiration });
    return 'OK';
  }

  async del(...keys: string[]): Promise<void> {
    for (const key of keys) {
      this.maybeFail('del', key);
      this.entries.delete(key);
    }
  }

  async incrBy(key: string, value: number): Promise<number> {
    this.maybeFail('incrBy', key);
    this.purgeExpired(key);
    const next = Number.parseInt(this.entries.get(key)?.value ?? '0', 10) + value;
    this.entries.set(key, { value: String(next) });
    return next;
  }

  async expire(key: string, seconds: number): Promise<void> {
    this.maybeFail('expire', key);
    const entry = this.entries.get(key);
    if (entry) entry.expiration = new Date(this.clockMs + seconds * 1000);
  }

  async hSet(key: string, fieldValues: Record<string, string>): Promise<number> {
    this.maybeFail('hSet', key);
    const hash = this.hashes.get(key) ?? new Map<string, string>();
    let added = 0;
    for (const [field, value] of Object.entries(fieldValues)) {
      if (!hash.has(field)) added += 1;
      hash.set(field, value);
    }
    this.hashes.set(key, hash);
    return added;
  }

  async hGetAll(key: string): Promise<Record<string, string>> {
    this.maybeFail('hGetAll', key);
    return Object.fromEntries(this.hashes.get(key) ?? []);
  }

  async hDel(key: string, fields: string[]): Promise<number> {
    this.maybeFail('hDel', key);
    const hash = this.hashes.get(key);
    if (!hash) return 0;
    let removed = 0;
    for (const field of fields) {
      if (hash.delete(field)) removed += 1;
    }
    return removed;
  }

  private purgeExpired(key: string): void {
    const expiration = this.entries.get(key)?.expiration?.getTime();
    if (expiration !== undefined && expiration <= this.clockMs) {
      this.entries.delete(key);
    }
  }

  private maybeFail(operation: string, key: string): void {
    if (this.failures.has(`${operation}:${key}`) || this.failures.has(operation)) {
      throw new Error(`simulated ${operation} failure for ${key}`);
    }
  }
}

const fixedNow = new Date('2026-07-14T09:30:00.000Z');
const DAY_MS = 24 * 60 * 60 * 1000;

const defaultSettings: RelaySettings = {
  enabled: true,
  destinationConfirmation: 'testdest',
  sources: ['cats', 'dogs'],
  maxPerHour: 6,
  maxPerDay: 24,
};

function atMinutes(minutes: number): Date {
  return new Date(fixedNow.getTime() + minutes * 60 * 1000);
}

function makePost(
  id: string,
  source: 'cats' | 'dogs',
  crossposts: CrosspostCall[],
  overrides: Partial<Omit<RelayPost, 'id' | 'subredditName' | 'crosspost'>> & {
    fail?: boolean;
  } = {},
): RelayPost {
  const { fail = false, ...postOverrides } = overrides;
  return {
    id,
    title: `Title ${id}`,
    subredditName: source,
    createdAt: atMinutes(1),
    nsfw: false,
    stickied: false,
    removed: false,
    spam: false,
    quarantined: false,
    spoiler: false,
    ...postOverrides,
    crosspost: async (options) => {
      crossposts.push({ id, options });
      if (fail) throw new Error('simulated ambiguous Reddit write failure');
      return { id: `t3_destination_${id}` };
    },
  };
}

async function seedCursor(
  store: FakeStore,
  source: 'cats' | 'dogs',
  initializedAt = fixedNow,
  highWater = fixedNow,
  ids: string[] = [],
): Promise<void> {
  await store.set(
    `relay:v1:baseline:${source}`,
    JSON.stringify({
      initializedAt: initializedAt.toISOString(),
      highWaterCreatedAt: highWater.toISOString(),
      highWaterPostIds: ids,
    }),
  );
}

test('first run baselines every source and forwards no existing posts', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  const posts = {
    cats: [makePost('t3_cat_old', 'cats', crossposts, { createdAt: atMinutes(-5) })],
    dogs: [makePost('t3_dog_old', 'dogs', crossposts, { createdAt: atMinutes(-4) })],
  };

  const result = await runRelayOnce({
    destination: 'testdest',
    settings: defaultSettings,
    store,
    fetchNewPosts: async (source) => posts[source],
    now: () => store.now(),
    randomToken: () => 'lock-one',
  });

  assert.equal(result.status, 'ok');
  assert.deepEqual(result.baselinedSources, ['cats', 'dogs']);
  assert.equal(result.attempted, 0);
  assert.equal(result.crossposted, 0);
  assert.deepEqual(crossposts, []);
  const cursor = JSON.parse((await store.get('relay:v1:baseline:cats'))!);
  assert.equal(cursor.initializedAt, fixedNow.toISOString());
  assert.equal(cursor.highWaterCreatedAt, fixedNow.toISOString());
});

test('missing settings remain disabled and never poll Reddit', async () => {
  const store = new FakeStore();
  let fetches = 0;
  const result = await runRelayOnce({
    destination: 'Testing_POC',
    settings: loadRelaySettings({}),
    store,
    fetchNewPosts: async () => {
      fetches += 1;
      return [];
    },
    now: () => store.now(),
  });

  assert.equal(result.status, 'disabled');
  assert.equal(result.attempted, 0);
  assert.equal(fetches, 0);
});

test('bounded baseline plus creation cutoff rejects unseen older history after churn', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  const baseline = Array.from({ length: 25 }, (_, index) =>
    makePost(`t3_old_${index}`, 'cats', crossposts, {
      createdAt: atMinutes(-index - 1),
    }),
  );
  await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => baseline,
    now: () => store.now(),
    randomToken: () => 'baseline-churn',
  });

  const newPost = makePost('t3_new', 'cats', crossposts, { createdAt: atMinutes(1) });
  const unseenHistory = makePost('t3_unseen_history', 'cats', crossposts, {
    createdAt: atMinutes(-100),
  });
  const result = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [newPost, unseenHistory],
    now: () => atMinutes(2),
    randomToken: () => 'after-churn',
  });

  assert.equal(result.crossposted, 1);
  assert.deepEqual(crossposts.map((call) => call.id), ['t3_new']);
  assert.equal(await store.get('relay:v1:seen:t3_unseen_history'), undefined);
});

test('expired baseline dedupe cannot resurrect a low-volume old post', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  const old = makePost('t3_low_volume_old', 'cats', crossposts, {
    createdAt: atMinutes(-2),
  });
  await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [old],
    now: () => store.now(),
    randomToken: () => 'old-baseline',
  });
  store.advance(91 * DAY_MS);
  assert.equal(await store.get('relay:v1:seen:t3_low_volume_old'), undefined);

  const result = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [old],
    now: () => store.now(),
    randomToken: () => 'old-after-expiry',
  });

  assert.equal(result.attempted, 0);
  assert.deepEqual(crossposts, []);
});

test('later run only creates native SFW non-stickied crossposts', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  await seedCursor(store, 'dogs');
  await store.set('relay:v1:seen:t3_duplicate', 'already-seen');

  const safe = makePost('t3_safe', 'cats', crossposts, {
    createdAt: atMinutes(1),
    spoiler: true,
  });
  const nsfw = makePost('t3_nsfw', 'cats', crossposts, {
    createdAt: atMinutes(2),
    nsfw: true,
  });
  const sticky = makePost('t3_sticky', 'dogs', crossposts, {
    createdAt: atMinutes(2),
    stickied: true,
  });
  const removed = makePost('t3_removed', 'dogs', crossposts, {
    createdAt: atMinutes(3),
    removed: true,
  });
  const duplicate = makePost('t3_duplicate', 'cats', crossposts, {
    createdAt: atMinutes(3),
  });

  const result = await runRelayOnce({
    destination: 'r/TestDest',
    settings: defaultSettings,
    store,
    fetchNewPosts: async (source) =>
      source === 'cats' ? [duplicate, nsfw, safe] : [removed, sticky],
    now: () => atMinutes(4),
    randomToken: () => 'safe-filter',
  });

  assert.equal(result.crossposted, 1);
  assert.equal(result.skippedUnsafe, 3);
  assert.equal(result.skippedDuplicate, 1);
  assert.deepEqual(crossposts[0], {
    id: 't3_safe',
    options: {
      title: 'Title t3_safe',
      subredditName: 'testdest',
      sendreplies: false,
      nsfw: false,
      spoiler: true,
      runAs: 'APP',
    },
  });
});

test('ambiguous write is counted before dispatch and remains terminal across restart', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  const post = makePost('t3_uncertain', 'cats', crossposts, { fail: true });

  const first = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [post],
    now: () => atMinutes(2),
    randomToken: () => 'uncertain-first',
  });

  assert.equal(first.attempted, 1);
  assert.equal(first.crossposted, 0);
  assert.equal(first.uncertain, 1);
  assert.equal(first.hourlyCount, 1);
  assert.equal(first.dailyCount, 1);
  assert.equal(JSON.parse((await store.get('relay:v1:seen:t3_uncertain'))!).state, 'uncertain');
  assert.equal(
    JSON.parse((await store.hGetAll(ATTENTION_INDEX_KEY)).t3_uncertain!).state,
    'uncertain',
  );

  store.advance(11 * 60 * 1000);
  const second = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [post],
    now: () => store.now(),
    randomToken: () => 'uncertain-restart',
  });
  assert.equal(second.attempted, 0);
  assert.equal(crossposts.length, 1);
});

test('high-water prevents a duplicate after a completed seen record expires', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  const post = makePost('t3_completed_then_expired', 'cats', crossposts);
  await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [post],
    now: () => atMinutes(2),
    randomToken: () => 'completed-first',
  });
  store.advance(91 * DAY_MS);
  assert.equal(await store.get('relay:v1:seen:t3_completed_then_expired'), undefined);

  const restarted = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [post],
    now: () => store.now(),
    randomToken: () => 'completed-restart',
  });
  assert.equal(restarted.attempted, 0);
  assert.equal(crossposts.length, 1);
});

test('per-run cap counts attempts and drops excess observed posts', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  const posts = [1, 2, 3].map((minute) =>
    makePost(`t3_run_${minute}`, 'cats', crossposts, { createdAt: atMinutes(minute) }),
  );
  const first = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => posts,
    now: () => atMinutes(4),
    randomToken: () => 'run-cap-first',
  });
  const second = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => posts,
    now: () => atMinutes(5),
    randomToken: () => 'run-cap-second',
  });

  assert.equal(first.attempted, 2);
  assert.equal(first.crossposted, 2);
  assert.equal(first.capped, true);
  assert.equal(second.attempted, 0);
  assert.equal(crossposts.length, 2);
});

test('hourly cap blocks dispatch and high-water prevents deferred replay', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  await store.set('relay:v1:count:hour:2026-07-14T09', '6');
  await store.set('relay:v1:count:day:2026-07-14', '6');
  const post = makePost('t3_hour_capped', 'cats', crossposts);
  const first = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [post],
    now: () => fixedNow,
    randomToken: () => 'hour-cap',
  });
  const nextHour = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [post],
    now: () => new Date('2026-07-14T10:30:00.000Z'),
    randomToken: () => 'hour-after-cap',
  });

  assert.equal(first.capped, true);
  assert.equal(first.attempted, 0);
  assert.equal(nextHour.attempted, 0);
  assert.deepEqual(crossposts, []);
});

test('daily cap blocks dispatch independently of hourly count', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  await store.set('relay:v1:count:hour:2026-07-14T09', '0');
  await store.set('relay:v1:count:day:2026-07-14', '24');
  const result = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [makePost('t3_day_capped', 'cats', crossposts)],
    now: () => fixedNow,
    randomToken: () => 'day-cap',
  });
  assert.equal(result.capped, true);
  assert.equal(result.attempted, 0);
  assert.deepEqual(crossposts, []);
});

test('source equal to destination is removed before polling', async () => {
  const store = new FakeStore();
  let fetches = 0;
  const result = await runRelayOnce({
    destination: 'cats',
    settings: {
      ...defaultSettings,
      destinationConfirmation: 'cats',
      sources: ['cats'],
    },
    store,
    fetchNewPosts: async () => {
      fetches += 1;
      return [];
    },
    now: () => fixedNow,
  });
  assert.equal(result.status, 'no-sources');
  assert.equal(fetches, 0);
});

test('one source failure does not prevent another source from relaying', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  await seedCursor(store, 'dogs');
  const result = await runRelayOnce({
    destination: 'testdest',
    settings: defaultSettings,
    store,
    fetchNewPosts: async (source) => {
      if (source === 'cats') throw new Error('cats unavailable');
      return [makePost('t3_dog_new', 'dogs', crossposts)];
    },
    now: () => atMinutes(2),
    randomToken: () => 'partial-source',
  });
  assert.equal(result.crossposted, 1);
  assert.equal(result.errors.length, 1);
  assert.deepEqual(crossposts.map((call) => call.id), ['t3_dog_new']);
});

test('counter persistence failure prevents Reddit dispatch and remains terminal', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  store.failures.add('incrBy:relay:v1:count:day:2026-07-14');
  const post = makePost('t3_counter_failure', 'cats', crossposts);
  const result = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [post],
    now: () => fixedNow,
    randomToken: () => 'counter-failure',
  });
  assert.equal(result.attempted, 0);
  assert.equal(result.attentionRequired, 1);
  assert.equal(result.hourlyCount, 1);
  assert.deepEqual(crossposts, []);
  assert.equal(
    JSON.parse((await store.get('relay:v1:seen:t3_counter_failure'))!).state,
    'not-dispatched',
  );
  assert.equal(
    JSON.parse((await store.hGetAll(ATTENTION_INDEX_KEY)).t3_counter_failure!).state,
    'not-dispatched',
  );
});

test('cursor persistence failure rejects the source before Reddit dispatch', async () => {
  const store = new FakeStore();
  const crossposts: CrosspostCall[] = [];
  await seedCursor(store, 'cats');
  store.failures.add('set:relay:v1:baseline:cats');
  const result = await runRelayOnce({
    destination: 'testdest',
    settings: { ...defaultSettings, sources: ['cats'] },
    store,
    fetchNewPosts: async () => [makePost('t3_cursor_failure', 'cats', crossposts)],
    now: () => atMinutes(2),
    randomToken: () => 'cursor-failure',
  });
  assert.equal(result.attempted, 0);
  assert.equal(result.errors.length, 1);
  assert.deepEqual(crossposts, []);
});

test('destination mismatch and overlapping invocation both avoid polling', async () => {
  const mismatchStore = new FakeStore();
  let mismatchFetches = 0;
  const mismatch = await runRelayOnce({
    destination: 'owned-community',
    settings: { ...defaultSettings, destinationConfirmation: 'different-community' },
    store: mismatchStore,
    fetchNewPosts: async () => {
      mismatchFetches += 1;
      return [];
    },
    now: () => fixedNow,
  });
  assert.equal(mismatch.status, 'destination-mismatch');
  assert.equal(mismatchFetches, 0);

  const lockedStore = new FakeStore();
  await lockedStore.set('relay:v1:lock:testdest', 'other-run');
  let lockedFetches = 0;
  const locked = await runRelayOnce({
    destination: 'testdest',
    settings: defaultSettings,
    store: lockedStore,
    fetchNewPosts: async () => {
      lockedFetches += 1;
      return [];
    },
    now: () => fixedNow,
    randomToken: () => 'overlap',
  });
  assert.equal(locked.status, 'locked');
  assert.equal(lockedFetches, 0);
});

test('settings parser allowlists sources and restores conservative invalid caps', () => {
  assert.deepEqual(
    loadRelaySettings({
      relayEnabled: true,
      destinationConfirmation: 'r/TestDest',
      sourceSubreddits: ['cats', 'not-allowed', 'dogs'],
      maxCrosspostsPerHour: 999,
      maxCrosspostsPerDay: 0,
    }),
    {
      enabled: true,
      destinationConfirmation: 'r/TestDest',
      sources: ['cats', 'dogs'],
      maxPerHour: 1,
      maxPerDay: 1,
    },
  );

  assert.deepEqual(loadRelaySettings({}), {
    enabled: false,
    destinationConfirmation: '',
    sources: ['cats', 'dogs'],
    maxPerHour: 1,
    maxPerDay: 1,
  });
});
