# Community Relay POC

Community Relay is a Windows proof of concept that watches new posts in selected
Reddit communities and creates **native Reddit crossposts** in a destination
community moderated by the operator. The starter configuration watches `r/cats`
and `r/dogs`.

The app is deliberately bounded:

- It starts in an offline demo and dry-run mode.
- It never backfills old posts on first run.
- It uses native crossposts. The source ID and required crosspost title are sent
  to Reddit, but the app does not download or re-upload post bodies or media.
- It anchors post-ID and fingerprint retention to first collection. Records
  older than 48 hours are purged at app startup and before each poll; if the app
  stays closed, its local file remains until the next start or manual deletion.
  It never persists titles, bodies, authors, media, passwords, or OAuth access
  tokens.
- OAuth secrets and refresh tokens are encrypted with Windows DPAPI for the
  current Windows user.
- Live mode requires the operator to confirm that Reddit approved the Data API
  use case, that the destination is private/restricted for the POC, and that the
  authenticated account moderates it.
- Conservative hourly and daily write caps, a stop button, deduplication,
  persisted server rate-limit pauses, and uncertain-write reconciliation are
  built in. Live-mode listing and submission-attempt pacing timestamps are also
  persisted across relay-engine instances, one-shot actions, and app restarts.
  An unconfirmed write becomes terminal manual review and is never blindly
  submitted again.

## Important July 2026 constraint

Reddit's current Responsible Builder Policy requires approval before accessing
the external Data API. Do not disable Demo mode or make live API calls until
Reddit has approved this use case. The app links to Reddit's official request
form. Commercial use, public distribution, or a notification product requires
separate written permission/contracting from Reddit.

For a live proof before external Data API approval, see `devvit/`: it contains a
Reddit-hosted playtest app that polls `r/cats` and `r/dogs`, deduplicates IDs in
Redis, and performs native crossposts inside a small destination test community.
An authorized private live test on 2026-07-14 created exactly one native
crosspost in `r/Testing_POC`; the app was then uninstalled and left unpublished.
See `devvit/docs/LIVE_TEST_2026-07-14.md` for the evidence and scope.

## Windows app setup

1. Install `CommunityRelayPOC-Setup.exe`.
2. Run the app and select **Run offline demo**. The activity feed should show a
   first-run baseline and then a synthetic new post without contacting Reddit.
3. Create/sign in to a Reddit account manually, create a private or restricted
   destination community, and enable Reposts in its community settings.
4. Request Data API access using the link in the app. Explain that the Windows
   tray/control-center requirement is not supported by Devvit alone.
5. Only after approval, create the OAuth application type Reddit instructs you
   to use. Configure this exact callback:
   `http://127.0.0.1:53682/callback`.
6. In Setup, enter the approved client ID, optional client secret if Reddit
   issued one, contact username, destination, and sources. Save.
7. Check the three live-access acknowledgements, click **Authorize**, then
   **Test connection**.
8. Keep Dry run enabled for the first poll. When the baseline is complete,
   disable Dry run, confirm the warning, and use **Test one crosspost** before
   enabling continuous mode.

The Windows client's native-crosspost request uses Reddit's legacy
`/api/submit` crosspost fields. Those fields are not present in Reddit's current
public Data API reference, so live Windows mode is an **experimental owner-run
test**, not a verified integration. Use it only after Reddit approves the exact
client and confirms the write method. The Devvit implementation uses Reddit's
currently documented native `Post.crosspost` API.

The app never asks for a Reddit password.

## Build from source

Requirements: Windows 10/11 and .NET SDK 8.0.422. The installer build refuses
to package .NET runtimes older than the June 2026 patched 8.0.28 runtime.

```powershell
.\scripts\test-windows.ps1 -DotNetPath (Get-Command dotnet).Source
.\scripts\build-installer.ps1 -DotNetPath (Get-Command dotnet).Source
```

`scripts/test-windows.ps1` restores, builds, and runs the dependency-free
self-tests while redirecting the .NET CLI profile, NuGet caches/packages,
temporary files, bundle extraction, and app-data root beneath this repository.
`scripts/build-installer.ps1` creates a self-contained x64 publish folder, builds
the repository's custom setup executable, and verifies an isolated test install
under this workspace. The POC installer is not Authenticode-signed.

## POC versus the larger business idea

This POC proves two-source discovery, filtering, deduplication, and native
crossposting. It does not claim that 2,000-source real-time fan-in is safe or
feasible. At a 30-second per-source poll, 2,000 sources would require about
4,000 listing reads per minute before writes, far beyond the normal free Data
API allowance. That product needs Reddit's written approval, an approved event
or sharded ingestion architecture, source-community/moderation processes,
deletion propagation, and a hosted notification service.

See `docs/SETUP.md`, `docs/PRIVACY.md`, and `docs/ARCHITECTURE.md`.
