# Safe playtest and deployment

## Boundary notice

Local build and test commands below keep writable tooling state under
`G:\Try_out`. The login, playtest, upload, install, and publish commands
also change external Reddit account/community state. Run them only as the Reddit
account owner and only for a community you own or moderate. The one authorized
live test performed for this POC is recorded in
[LIVE_TEST_2026-07-14.md](LIVE_TEST_2026-07-14.md); the app was uninstalled
afterward and was not published.

## 1. Prepare the destination

Use a Reddit account in good standing and a test community that you moderate.
Devvit playtest communities must have fewer than 200 subscribers. The
destination should permit crossposts and should have rules that clearly explain
the automated feed.

The app slug in `devvit.json` is currently `relay-poc-jimmy`. App slugs are
global; change it to a unique 3–20 character lowercase slug if Reddit reports
that it is unavailable. If you change it, use that same slug in the URLs and
commands below.

Do not test against `r/cats`, `r/dogs`, or another community you do not moderate.
The destination must be your own small playtest community.

## 2. Build locally with workspace-only state

Open PowerShell:

```powershell
Set-Location G:\Try_out
.\scripts\test-devvit.ps1 -NpmPath (Get-Command npm).Source
. .\scripts\use-devvit-workspace.ps1
Set-Location .\devvit
```

The test wrapper restores the caller's environment when it finishes. The
dot-sourced `use-devvit-workspace.ps1` command then redirects the complete
Windows profile/AppData, XDG, temp, npm config/cache/prefix/log, Node, and Devvit
state set for the later authorized login/playtest commands. Keep that PowerShell
window open; close it when finished to restore the prior process environment.
`DEVVIT_ROOT_DIR` keeps the Devvit CLI token and session state under
`G:\Try_out\.build\devvit\devvit-state`.

## 3. Log in and start the playtest

These commands open Reddit authorization and upload/install the app:

```powershell
npx --no-install devvit login
npm run playtest -- YOUR_TEST_SUBREDDIT
```

Use the subreddit name without `r/`. The playtest command rebuilds on changes,
installs the current version, schedules the minute poll, and streams logs. Stop
the watcher with `Ctrl+C`; the most recently installed playtest version remains
installed.

## 4. Configure the installation

The relay defaults to disabled. Open:

```text
https://developers.reddit.com/r/YOUR_TEST_SUBREDDIT/apps/relay-poc-jimmy
```

Set:

1. **Destination subreddit name** — exactly your installed test community name.
2. **Source subreddits** — `r/cats`, `r/dogs`, or both.
3. **Hourly/day caps** — leave the conservative defaults for the first test.
4. **Enable automatic crossposts** — turn on only after checking the other
   fields.

The first enabled poll for each selected source stores the newest 25 IDs as a
baseline. It creates no crossposts from that history. A later new eligible post
can be forwarded on the next minute poll.

Use the subreddit moderator menu actions:

- **Community Relay: status** shows the last run.
- **Community Relay: run now** invokes the same guarded path as the scheduler;
  it does not bypass enablement, destination checks, dedupe, or caps.

If status lists source post IDs requiring moderator inspection, check the
destination manually. A `dispatching` or `uncertain` record may represent a
crosspost that Reddit accepted before the response failed. The app will not
automatically retry it.

Expected first-run log fields include `source-baselined`, `cats`/`dogs`, and a
count. A successful later write logs `crosspost-created` with source and
destination post IDs, but not copied title/body/media content.

## 5. Check failure cases before considering release

- Clear or alter the destination confirmation: result must be
  `destination-mismatch` and perform no reads/writes to Reddit posts.
- Disable the relay: result must be `disabled`.
- Re-run immediately: already-seen post IDs must not crosspost twice.
- Confirm NSFW and stickied source posts do not appear.
- Set the hourly cap to `1` and confirm no second write is attempted. The
  observation high-water advances, so the blocked item is deliberately dropped
  rather than replayed when the next hour starts.
- Disable crossposting in the test destination and confirm the failure is logged
  as uncertain and is not retried, even after ten minutes or a process restart.

## 6. Private upload and publication

For a private owner-only upload:

```powershell
npx --no-install devvit upload
```

For wider distribution, publication submits the source and app for Reddit
review:

```powershell
npx --no-install devvit publish
```

Do not publish a high-volume aggregator merely because the code passes. Before
publication, obtain confirmation from Reddit Developer Platform reviewers that
the intended automated crosspost volume and source scope are acceptable. Reddit
can reject or remove an app under the current Devvit Rules and spam policy.

## Troubleshooting

- **App name unavailable:** edit `name` in `devvit.json` to a unique valid slug.
- **No writes and `destination-mismatch`:** the confirmation must match the
  installed subreddit (case and an optional `r/` prefix are normalized).
- **No writes and baseline message:** expected on the first enabled run for that
  source.
- **Crosspost rejected:** verify the source post is crosspostable, the
  destination allows that post type, and the app is installed in the
  destination.
- **`locked`:** another scheduler/manual invocation is still running; wait for
  the next minute.
- **`Cap reached`:** this POC intentionally throttles. Do not raise volume by
  bypassing the hard runtime ceilings. Capped observations are dropped because
  this POC has no durable queue.
- **Moderator inspection required:** inspect the listed source ID and the
  destination before taking any manual action. There is intentionally no
  automatic retry or “clear and retry” action for ambiguous writes.
