# Draft: Reddit Data Access request

Review and personalize this draft before submitting it. Do not claim features,
approval, company status, or traffic that is not true.

## Role and use case

Independent developer building a non-commercial proof of concept for a
moderator-owned topic aggregation community.

## Benefit to Redditors

The POC helps a user discover posts across a small, explicitly configured set of
related communities through Reddit-native crossposts in a private/restricted test
community, preserving original attribution and links to the source community.
The client sends the source fullname and required title but does not download or
re-upload source bodies or media. The operator has pause controls, filters, and
strict write caps.

## Exact POC behavior

- Sources initially: `r/cats` and `r/dogs`.
- Destination: one private/restricted community moderated by the authorizing
  account.
- Read action: authenticated `/new` listings.
- Write action: native crossposts only; the source fullname and title are sent as
  required, with no body/media download, re-upload, or fallback scraping.
- Initial state: record existing IDs without backfilling.
- Filters: source, title include/exclude, NSFW off by default, sticky skip, and
  crosspostability.
- Limits: listing reads paced to 80 QPM maximum; default six crossposts/hour and
  20/day; 15 seconds minimum between writes; server limits always override.
  Last live listing-read and submission-attempt timestamps are persisted and
  shared across continuous polling, one-shot tests, new relay engines, and app
  restarts so those pacing windows cannot be reset by changing actions.
- Failure behavior: uncertain-write reconciliation followed by terminal manual
  review with no automatic resubmission, a persisted `Retry-After` pause, and
  immediate stop for lost authorization or destination permission.

## Why Devvit alone is not the full requested client

A Devvit playtest is included and will be used to prove the on-platform
crossposting mechanic first. The broader prototype explores a Windows operator
control center and, later, per-user notification preferences across selected
sources. Devvit does not by itself provide the requested installable Windows
control surface or a general external push-client preference system. No external
Data API traffic will occur before approval.

## Data handling

- No post bodies, comments, authors, user profiles, media, votes, or private data
  are persisted.
- Post IDs, outcomes, and one-way SHA-256 fingerprints use the first collection
  time as a non-sliding retention anchor. Entries older than 48 hours are purged
  at app startup and before each poll.
- If the desktop app remains closed, its local state file remains until the next
  start or operator deletion; this limitation will be disclosed and replaced by
  server-enforced deletion before any production notification service.
- A per-source initialization timestamp prevents old posts from being treated as
  new after post-level state expires.
- The local state also retains the global live API not-before time and the last
  live listing-read/submission-attempt times needed to preserve pacing across
  one-shot actions and process restarts.
- Refresh token and any issued secret are Windows-DPAPI encrypted.
- Logs contain sanitized codes, not tokens or content.
- A production notification layer would not launch until deletion propagation,
  a full privacy policy, user consent, and any required commercial contract are
  approved.

## Expected scale

Initial playtest: two sources, one operator, one private/restricted destination,
under ten reads/minute including headroom, and at most the configured write caps.
No claim or request is made here for a 2,000-source firehose. Any expansion would
be separately reviewed with Reddit.

## Transparency

User-Agent format:

```text
windows:community-relay-poc:v0.1.0 (by /u/REPLACE_WITH_USERNAME)
```

The app has one purpose and will not register duplicate clients, evade limits,
vote, message users, infer sensitive traits, train AI models, or monetize Reddit
data without express written approval.

## Source-code URL

`REPLACE_WITH_REPOSITORY_URL_OR_ATTACH_REQUESTED_SOURCE`
