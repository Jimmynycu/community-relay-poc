# Setup and live-proof guide

Last checked against Reddit's official documentation: 2026-07-14.

## Choose the permitted route

There are two execution routes in this repository:

1. **Devvit playtest** (`devvit/`) — the recommended first live proof. Reddit
   hosts the scheduler, authentication, Redis state, and crosspost calls. Reddit
   says developers can test a Devvit app before public App Review, although all
   Devvit rules and other terms still apply.
2. **Windows app** (`src/CommunityRelay/`) — an offline demo immediately, and an
   external Data API client only after Reddit explicitly approves the use case.

Reddit's [Responsible Builder Policy](https://support.reddithelp.com/hc/en-us/articles/42728983564564-Responsible-Builder-Policy)
states that explicit approval is required before API access and directs
developers to Devvit first. The Windows app enforces an operator acknowledgement
for that approval; the checkbox is not a substitute for approval.

## One-time Reddit setup (manual)

These actions change external account/platform state and are intentionally not
automated by this repository.

1. Sign in to an established Reddit account in good standing, or create one
   manually. Do not mix a personal posting identity with a later production app
   identity.
2. Create a destination test community with a unique name. Use **Private** or
   **Restricted** for the POC.
3. In the destination's community settings, enable **Reposts** (the Reddit UI's
   current name for crossposts). Keep NSFW disabled unless the experiment
   genuinely needs it.
4. Join the destination with the same account that will authorize the Windows
   client. The app independently verifies that this account is a moderator,
   subscriber, and that the destination is private/restricted.

Reddit's current [community-settings guide](https://support.reddithelp.com/hc/en-us/articles/15484546290068-Community-settings)
describes the relevant moderator controls.

## Prove the Windows app locally first

1. Install or run Community Relay POC.
2. Leave **Offline Demo mode** and **Dry run** checked.
3. Click **Run offline demo**.
4. Verify the activity feed reports:

   - an existing-ID baseline for `r/cats` and `r/dogs`;
   - a new synthetic ID on the second poll;
   - a dry-run match;
   - no Reddit network call or post.

The first real poll uses the same baseline rule: current posts become seen IDs
and are not backfilled.

## Request external Data API access

Use Reddit's [Data Access request](https://support.reddithelp.com/hc/en-us/requests/new?tf_42139884615700=api_request_type_developer_clone&ticket_form_id=14868593862164).
Review `API_REQUEST_DRAFT.md` before completing the form. Be explicit about:

- the two-source POC and owned destination;
- native crossposts only;
- no scraping or non-OAuth fallback;
- first-run baseline and deduplication;
- the exact call budget and write caps;
- first-seen-based 48-hour cleanup at the next startup/poll, the offline cleanup
  limitation, and no persisted bodies/authors/media;
- the reason a Windows control center is needed in addition to Devvit;
- whether any future product would be commercial.

Do not create multiple applications or access requests for the same use case.

## Configure OAuth only after approval

Follow the application type and credential instructions Reddit provides with
approval. The client supports an installed/public client (blank secret) or a
client for which Reddit issued a secret. Never embed a shared secret in a public
binary.

The registered callback must match exactly:

```text
http://127.0.0.1:53682/callback
```

In the app's Setup tab:

1. Turn off Offline Demo mode but leave Dry run on.
2. Enter the approved client ID, optional issued secret, and contact username.
3. Enter the private/restricted destination and source names.
4. Check the three truthful acknowledgements.
5. Save, then click **Authorize**. The app opens Reddit in the system browser and
   listens only on loopback for five minutes.
6. Click **Test connection**. Do not proceed unless username, moderator access,
   subscription, and destination type all pass.
7. Click **Poll once** with Dry run on. This baselines existing IDs.
8. Turn Dry run off and click **Test one crosspost**. Confirm the destination
   contains exactly one native repost with original attribution.
9. Only then use continuous mode.

The Windows client implements native crossposting through legacy
`/api/submit` crosspost fields. Reddit's current public API reference does not
document those fields. Treat this step as an experimental owner-run validation:
ask Reddit to confirm the approved write method, use only the private/restricted
test destination, and do not assume the request will be accepted. The Devvit
playtest uses Reddit's documented `Post.crosspost` method.

The app requests `identity`, `read`, `submit`, and `mysubreddits`. It never asks
for a Reddit password. A refresh token and optional client secret are encrypted
with Windows DPAPI for the current user.

## Troubleshooting

- **Authorization required** — authorize again; the refresh token may have been
  revoked.
- **Destination permission denied** — confirm the authorizing account moderates
  and has joined the private/restricted destination.
- **Native crosspost rejected** — enable Reposts, review flair/post requirements,
  confirm the source post is crosspostable, and confirm the write method with
  Reddit. The app will not download/re-upload the source body or media or
  silently fall back to a link post.
- **Rate limited** — leave the app stopped until the displayed reset. It honors
  and persists `Retry-After` across one-shot actions/restarts, reads
  `X-Ratelimit-*`, and reserves API headroom. A rate-limited source ID is not
  automatically submitted again.
- **Uncertain write / manual review** — inspect the destination. If one
  reconciliation read cannot confirm the result, that source ID becomes
  terminal and the app will not resubmit it.
- **No historical posts appeared** — expected. The first poll never backfills.
- **Hundreds of sources are slow** — expected for the desktop architecture; see
  `ARCHITECTURE.md`.

Official technical limits and OAuth/User-Agent rules are in Reddit's
[Data API Wiki](https://support.reddithelp.com/hc/en-us/articles/16160319875092-Reddit-Data-API-Wiki).
