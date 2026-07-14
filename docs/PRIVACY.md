# Privacy and data-handling note

This file documents the POC's behavior; it is not legal advice or a substitute
for a reviewed production privacy policy.

## Local data inventory

By default, an installed app uses:

```text
%LOCALAPPDATA%\CommunityRelayPOC\
```

Tests/build verification set `COMMUNITY_RELAY_DATA_DIR` to a directory beneath
the repository instead. The files are:

| File | Contents | Retention |
|---|---|---|
| `config.json` | Client ID (public), contact username, destination, source names, filters, limits, operator acknowledgements | Until changed/deleted |
| `config.json.invalid-<timestamp>` | A corrupt prior configuration archived during recovery; it may retain the public client ID, contact username, destination, sources, filters, limits, and acknowledgements, but contains no OAuth secret, refresh token, Reddit password, or post IDs | Until manually deleted |
| `secrets.bin` | Optional client secret and OAuth refresh token, encrypted with Windows DPAPI CurrentUser | Until authorization is cleared or file deleted |
| `relay-state.json` | Source post fullnames, source community, outcome/error code, timestamps, attempt state, destination fullname, SHA-256 ID fingerprints, the persisted API pause, and last live listing-read/submission-attempt pacing timestamps | Post-level retention is anchored to first collection; records older than 48 hours are purged at the next app startup or poll. API pacing timestamps remain until superseded or the state file is deleted |
| `logs/community-relay.log` | Timestamped operational messages and sanitized error codes | Rotates at 1 MB; one prior file retained |

The live listing-read and submission-attempt timestamps are shared across relay
engine instances. This means continuous polling, **Test one crosspost**, and a
later app process all honor the same 80-QPM read and 15-second submission pacing
windows rather than resetting them. The persisted server-directed API pause has
the same cross-action/restart behavior.

State saves use exact `relay-state.json.<GUID>.tmp` siblings and an atomic
replace. A failed save removes its temporary file in a best-effort `finally`
cleanup. After the desktop app has acquired its single-instance lock, state
startup also removes any exact GUID-named siblings left by a prior crash before
loading the canonical file. This closes the path by which raw post IDs in an
orphaned state-write file could otherwise outlive normal state cleanup; files
with merely similar operator-created names are not deleted.

## Data deliberately not persisted

The POC does not persist:

- post titles or bodies;
- author names, IDs, profiles, flair, or avatars;
- comments, votes, subscriber lists, or private data;
- image/video/media bytes or external URLs;
- OAuth access tokens;
- Reddit passwords;
- notification-user profiles (the notification layer is not implemented).

Titles and original permalinks exist only in memory for the current poll and UI
activity row. A native-crosspost request sends the source fullname and title to
Reddit. File logs omit activity URLs and sanitize Reddit fullnames. The app does
not download or re-upload the source body or media.

## Deletion and operator controls

- **Clear authorization** removes the encrypted refresh token. An optional
  client secret remains until `secrets.bin` is deleted.
- Delete the app-data directory to remove all local configuration, encrypted
  credentials, logs, IDs, and fingerprints.
- Delete any `config.json.invalid-<timestamp>` files manually if corrupt-config
  recovery created them; replacing or saving `config.json` does not remove these
  archives.
- Program uninstall deliberately preserves this separate app-data directory and
  says so in its confirmation prompt. To remove data too, use **Open app data**
  before uninstalling and delete that directory after the app is closed.
- Removing local state does not remove posts already created on Reddit. Delete
  those native crossposts with Reddit moderator tools.
- A production service must implement Reddit deletion/removal reconciliation
  before retaining more content metadata or serving downstream notifications.

Reddit's [Data API Wiki](https://support.reddithelp.com/hc/en-us/articles/16160319875092-Reddit-Data-API-Wiki)
requires deleted content and deleted-user identifiers to be removed and
recommends routinely clearing stored user content within 48 hours. This POC
uses first-seen-based cleanup on startup and before polling. Because a closed
desktop app cannot run cleanup, old local records can remain until the next
start or operator deletion; production needs server-side deletion enforcement.

## Security boundary

- DPAPI ciphertext is bound to the Windows user profile; copying `secrets.bin`
  to another user should not decrypt it.
- No secret is embedded in the source, installer, command line, config JSON, or
  log.
- The OAuth callback binds to `127.0.0.1` only and verifies a random state value.
- The app uses HTTPS for Reddit OAuth/API calls and a descriptive User-Agent.
- Local administrators or malware running as the same Windows user remain
  outside the POC's threat model.
