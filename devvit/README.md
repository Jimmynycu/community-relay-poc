# Community Relay — Devvit playtest backend

This directory contains a Reddit-native proof of concept that watches the newest
posts in `r/cats` and `r/dogs` once per minute and creates **native Reddit
crossposts** in the community where the app is installed.

It is intentionally a conservative playtest, not a claim that mirroring hundreds
of communities at high volume is approved or production-safe.

## What is implemented

- A declarative one-minute Devvit scheduler in `devvit.json`.
- Installation-scoped settings for enable/disable, source selection, destination
  confirmation, and hourly/daily caps.
- The installed community (`context.subredditName`) is the only destination.
- A required destination confirmation prevents a configuration typo from writing
  anywhere else.
- First poll per source creates a baseline and never forwards existing history.
- A per-source `initializedAt` cutoff and observation high-water, so bounded
  baselines, listing churn, and expired legacy dedupe keys cannot replay history.
- Redis dedupe, a short overlap lock, and permanent pre-dispatch reservations.
  An ambiguous Reddit write is terminal and requires moderator inspection; it is
  never retried automatically.
- SFW-only filtering. NSFW, stickied, removed, spam, and quarantined source posts
  are never forwarded.
- At most two write attempts per scheduler invocation, one per UTC hour by
  default, and one per UTC day by default. Reservations and both counters are
  persisted before dispatch. Settings can never raise these above 12/hour and
  48/day.
- Moderator menu actions for an on-demand safe run and a compact status report.
- Unit tests covering the main safety invariants.

The implementation calls `Post.crosspost(...)`. It never calls `submitPost`,
copies a post body, downloads media, or republishes an author's content as a new
self/link post.

## Local verification

All writable Node/npm/Devvit state can be kept below this directory:

```powershell
Set-Location G:\Try_out
.\scripts\test-devvit.ps1 -NpmPath (Get-Command npm).Source
```

The wrapper redirects the Windows profile/AppData locations, XDG directories,
temporary paths, npm cache/prefix/config/logs, Node cache/history, and Devvit
CLI state beneath `.build\devvit`, restores the caller's environment, and runs
`npm ci`, schema validation, strict TypeScript checking, 16 unit tests, the
production bundle, and `npm audit`.
The checked-in dependencies remain pinned to Devvit `0.13.7`, the version used
for the successful private test on 2026-07-14.
`package.json` overrides the CLI's transitive `tmp` dependency to patched
`0.2.7`; the complete dependency tree passes `npm audit` as built.

## Live test and release

Follow [PLAYTEST.md](docs/PLAYTEST.md). Those steps log in to Reddit, upload an
app, and install it into a subreddit, so they change external Reddit state and
must be performed deliberately by the Reddit account owner.

The authorized private live test completed successfully on 2026-07-14: one
new `r/cats` post became one native crosspost in `r/Testing_POC`, after which
the app was uninstalled. See [LIVE_TEST_2026-07-14.md](docs/LIVE_TEST_2026-07-14.md).

## Important limits

- Devvit server requests have a 30-second maximum. Polling hundreds or thousands
  of sources serially cannot fit in one scheduled request.
- The scheduler is near-real-time, not a stream: normal resolution is one minute
  and execution can be delayed.
- The observation cursor advances before delivery. Posts blocked by caps or an
  internal safety failure are deliberately dropped, not queued or retried. This
  POC prioritizes at-most-once behavior over completeness.
- A source community or the destination can disallow crossposts. Reddit may also
  reject individual writes or rate-limit the app.
- Mass automated aggregation can be treated as spam. A broader deployment needs
  Reddit review and explicit confirmation that the use case complies with the
  current Devvit Rules, Developer Terms, content-owner expectations, and spam
  policy.
- The requested push-notification/filtering product is a separate layer and is
  not implemented by this backend.
- Source-community deletion events are not available to an app installed only in
  the destination. This POC does not reconcile deletion of an original source
  post with its destination crosspost; production release requires an approved
  deletion-reconciliation design.

See [ARCHITECTURE.md](docs/ARCHITECTURE.md) for the state model and production
gaps.

## Official references

- [Devvit scheduler](https://developers.reddit.com/docs/capabilities/server/scheduler)
- [Reddit API](https://developers.reddit.com/docs/capabilities/server/reddit-api)
- [Settings and secrets](https://developers.reddit.com/docs/capabilities/server/settings-and-secrets)
- [Redis](https://developers.reddit.com/docs/capabilities/server/redis)
- [Devvit CLI](https://developers.reddit.com/docs/guides/tools/devvit_cli)
- [Devvit Rules](https://developers.reddit.com/docs/devvit_rules)
- [Reddit spam policy](https://support.reddithelp.com/hc/en-us/articles/360043504051-Spam)
