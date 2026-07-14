# Windows installer

`scripts/build-installer.ps1` produces a self-contained Windows x64 installer at:

```text
artifacts\installer\CommunityRelayPOC-Setup.exe
```

The setup executable embeds the complete self-contained app publish plus a
SHA-256 integrity inventory. The build refuses to package either
`Microsoft.NETCore.App` or `Microsoft.WindowsDesktop.App` below security-patched
version 8.0.28. Setup runs without administrator access and defaults to the
current user's `%LOCALAPPDATA%\Programs\CommunityRelayPOC` folder. The graphical
setup allows a different destination and lets the user choose Start Menu and
Desktop shortcuts. Shortcuts are created only when the user runs setup and
selects them.

Every payload file is extracted and hash-checked in a same-volume sibling
staging directory. Setup then swaps the complete staged directory into place.
If activation, ownership validation, or shortcut creation fails, the prior
directory and exact product shortcuts are restored. A nonempty custom target is
accepted only when its JSON ownership marker and install manifest contain the
same product ID, install ID, version, and absolute install root. Reparse points
and junctions in the target ancestry or subtree are rejected.

For this POC, an in-place update is additionally restricted to an existing
payload whose complete tracked path set matches the embedded allowlist and
whose payload file hashes match the setup EXE. This prevents a forged manifest
from labelling arbitrary custom-folder files as product-owned. A future release
with a changed payload needs an authenticated migration design or an
uninstall/reinstall flow.

The installer deliberately does not write an Apps & Features registry entry.
Instead, it installs `Uninstall Community Relay.ps1` and, when Start Menu
shortcuts are selected, an **Uninstall Community Relay POC** shortcut. The
uninstaller deletes only validated files recorded in `install.json`. A manifest
may select only among three exact Community Relay shortcut paths, and uninstall
removes only the validated subset that setup recorded as product-owned. Unknown
files and pre-existing shortcut-name collisions are preserved. The generated
uninstaller also embeds its own exact tracked-file allowlist; editing only
`install.json` cannot expand what uninstall is permitted to delete.

Uninstall always preserves the app's configuration, encrypted Reddit
credentials, relay state, and logs under `%LOCALAPPDATA%\CommunityRelayPOC`.
This is intentional: program and data roots are separate, the uninstaller tells
the user data will be kept, and it never accepts or recursively deletes a data
root. Avoiding a “remove all data” switch protects DPAPI credentials and relay
state from an additional deletion surface. Later manual data removal is a
separate user action, outside this installer.

## Build and verify

Run from PowerShell:

```powershell
.\scripts\build-installer.ps1 -DotNetPath <path-to-patched-dotnet.exe>
```

All NuGet packages, .NET first-run state, temporary files, bundle extraction,
publishes, and verification data are redirected beneath this repository. The
build then invokes `scripts/verify-installer.ps1`, which:

1. runs setup with `--verify` into `artifacts\verification\test-install`;
2. confirms the exact embedded runtime versions and payload hashes;
3. waits for the installed WPF app to reach input-idle with its expected main
   window and workspace-redirected data root;
4. exercises normal Start Menu/Desktop shortcut creation and removal entirely
   under a workspace-contained shell-root override;
5. proves first install will not overwrite a pre-existing exact-name shortcut;
6. proves a malicious manifest cannot delete an unrelated shortcut or classify
   an untracked custom-folder file as installer-owned;
7. tests rejection of unowned nonempty folders and ancestry/subtree junctions;
8. forces a late shortcut failure and proves byte-for-byte rollback; and
9. writes `artifacts\verification\verification-report.json`.

Verification never runs the default per-user installation path.

The generated EXE is not Authenticode-signed. A production distribution must
add organization-controlled code signing after this reproducible build and
rerun signature/integrity verification before release.

Only `CommunityRelayPOC-Setup.exe` and its `.sha256.txt` sidecar are distribution
artifacts. `artifacts\verification\build-metadata.json` and
`verification-report.json` are local QA evidence and intentionally use
workspace-relative paths.

## Unattended options

```text
--install-dir <path>
--silent
--start-menu-shortcut | --no-start-menu-shortcut
--desktop-shortcut | --no-desktop-shortcut
--no-shortcuts
--launch | --no-launch
--verify
```

`--verify` requires an explicit `--install-dir`, implies silent operation,
disables launching, and forcibly disables all shortcuts regardless of option
order.
