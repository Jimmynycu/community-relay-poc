# Architecture and production gaps

## Request flow

1. Devvit invokes `POST /internal/scheduler/poll-sources` every minute.
2. The handler reads installation-scoped settings and uses
   `context.subredditName` as the destination.
3. A Redis `SET NX` lock rejects overlapping scheduler/manual runs.
4. `reddit.getNewPosts` fetches at most 25 newest posts from each configured
   source.
5. A source without a baseline records `initializedAt` and an observation
   high-water, then stops; history is never replayed.
6. On every later poll, only posts newer than both the initialization cutoff and
   previous high-water are candidates. The high-water advances before delivery.
7. The app creates a permanent attempt reservation, indexes it for moderator
   inspection, and consumes UTC hour/day counters before calling Reddit.
8. The source `Post.crosspost` method creates a native crosspost as the Devvit
   app account.
9. A confirmed success records the destination ID. An exception from the write
   is stored as terminal `uncertain` and is never retried automatically.

## Redis keys

Redis is installation-scoped, so each destination has an independent state set.

- `relay:v1:baseline:<source>` — permanent initialization cutoff, high-water
  timestamp, and IDs observed at that timestamp.
- `relay:v1:seen:<t3>` — 90-day baseline/unsafe/completed markers or permanent
  reserved, uncertain, and not-dispatched attempt states. The source high-water,
  not this TTL, is the replay-prevention authority.
- `relay:v1:attention` — stable Redis hash of dispatching, uncertain, and other
  attempts requiring moderator inspection.
- `relay:v1:lock:<destination>` — 50-second overlap lock.
- `relay:v1:count:hour:<UTC-hour>` — expiring hourly counter.
- `relay:v1:count:day:<UTC-day>` — expiring daily counter.
- `relay:v1:last-result` — seven-day moderator status summary.

The source body, media, author profile, and full Reddit response are not stored.

## Delivery semantics

No distributed transaction can atomically combine the Reddit write with Redis.
The POC therefore chooses at-most-once attempts: it permanently reserves the
source ID, writes a discoverable `dispatching` record, and consumes both caps
before calling Reddit. If the call throws, the outcome is terminal `uncertain`.
It remains visible to moderators and is never blindly retried. A crash after
dispatch begins leaves the durable `dispatching` record for the same reason.

The observation high-water advances before delivery. A post blocked by a
per-run/hour/day cap, source-state persistence failure, or an unavailable queue
is dropped rather than deferred. This prevents history replay and duplicate
writes, but it means the POC is not a lossless firehose.

## Deletion reconciliation gap

The app is installed only in the destination, so it does not receive source
communities' `onPostDelete` triggers. Native crossposts preserve Reddit
attribution, but this POC does not independently detect an original post's later
deletion and remove/reconcile the destination crosspost. This remains a release
blocker. A production design needs an approved bounded reconciliation process
that checks stored source/destination ID mappings and honors deletion promptly.

## Why the POC is limited to two sources

The current Devvit setting is an official installation-scoped `multiSelect`,
which is appropriate for a two-source demonstration. A hundreds-source version
needs a different reviewed architecture because:

- settings values are limited in size;
- one server request has a 30-second limit;
- per-minute polling and Reddit API calls must respect platform limits;
- mass automated crossposting can degrade the destination and trigger spam
  enforcement;
- a production feed needs a bounded durable queue, deletion reconciliation,
  monitoring, pause controls, and per-source health/backoff;
- source creators and communities may not expect their content to be aggregated
  at that scale.

A safe next-stage design should separate discovery from delivery, shard source
polls, enqueue only IDs, process a capped queue, reconcile deletions, and expose
an opt-in filtered feed outside the single-subreddit firehose. That design still
requires Reddit approval; it is not authorization to bypass platform limits.

## Live verification still required

Local tests prove the engine behavior against typed fakes and the production
bundle. Only a live playtest can prove that Reddit currently permits both
cross-community `getNewPosts` reads and each requested native crosspost under the
specific source/destination community settings. The repository does not contain
Reddit credentials and has not mutated a Reddit account or community.
