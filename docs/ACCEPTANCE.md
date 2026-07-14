# POC acceptance evidence

Completion is evaluated against the original Windows POC goal and Reddit's
current approval/review requirements. Local evidence proves software behavior;
it does not prove that Reddit approved this use case or accepted a live write.

| Requirement | Evidence | Current status |
|---|---|---|
| Installable Windows app | Self-contained x64 setup, clean in-workspace install, runtime/hash checks, and installed UI launch | Verified: expanded installer report is `verified: true`; exact final evidence is recorded below |
| Immediate safe use | Offline demo test baselines synthetic history, then detects a new synthetic post without Reddit traffic | Verified in the 25/25 Windows self-test suite; final installed UI launch is part of installer verification |
| `r/cats` + `r/dogs` discovery | Defaults plus first-run/two-source engine coverage | Verified locally; no live Reddit read was made |
| Native forwarding | Windows contract test emits only a native crosspost request; Devvit calls documented `Post.crosspost`; neither has a copy/link fallback | Verified against fakes and production bundle; live Windows endpoint remains experimental and live Reddit proof is not performed |
| No historical flood | Baseline, bounded-window, creation-cutoff, and 49-hour low-volume replay tests | Verified locally |
| Duplicate safety | One desktop instance, preserved write-ahead state, first-seen fingerprints, reconciliation, terminal ambiguous outcomes, and no automatic re-POST after dispatch | Verified across repeated discovery, engine replacement, and restart tests. This is an at-most-once **attempt** design while state is preserved, not an exactly-once guarantee after manual state deletion or external mutation |
| Filters | Source, include/exclude title keywords, NSFW, sticky, and fail-closed crosspostable gates | Verified locally |
| Near-real-time POC | Two-source sweep plus configured delay; no stream or guaranteed push semantics | Implemented locally; live latency unverified |
| Safe credentials and links | No password; DPAPI refresh token/optional secret; sanitized logs; API-returned destination URL ignored in favor of a canonical Reddit URL | Verified locally on Windows |
| Rate and spam controls | Persisted 80-QPM listing pacing, 15-second write spacing, rate-header/`Retry-After` pauses, hourly/daily caps, and stop/close quiescence | Verified across one-shot actions, engine instances, and restart tests |
| Policy-gated access | Live checkboxes, private/restricted destination checks, API request draft, and Devvit-first path | The approval checkbox is operator self-attestation only; the app cannot verify Reddit approval and this repository does not claim policy compliance |
| Live Reddit destination | User-created private/restricted subreddit with Reposts enabled and one owner-authorized playtest/test crosspost | Not performed: this needs external account/community changes by the owner and Reddit's applicable approval/review |
| Devvit POC | Config schema, strict types, 15 relay tests, production bundle, and dependency audit | Verified locally: 15/15 and `npm audit` reports 0 vulnerabilities; upload/playtest not performed |
| Production deletion handling | Reconcile deleted source posts with destination crossposts and downstream notifications | Not implemented; explicit production/public-release blocker |
| Hundreds/2,000 roadmap | Quantified source-count latency and approved event/sharded production direction | Documented in `ARCHITECTURE.md`; 2,000-source real-time polling is not claimed |
| Notification layer | Hosted/mobile filter and push architecture | Design only; outside this forwarding POC |

## Reproducible local checks

Both wrappers redirect writable profiles, caches, packages, temporary files,
tool state, and app data beneath `G:\Try_out` and reject reparse-point escapes.

```powershell
.\scripts\test-windows.ps1 -DotNetPath <path-to-dotnet-8.0.422.exe>
.\scripts\test-devvit.ps1 -NpmPath <path-to-npm.cmd>
```

Latest pre-package results on 2026-07-14:

- Windows Release build: 0 warnings, 0 errors; 25/25 checks passed.
- Devvit: schema valid, strict typecheck passed, 15/15 checks passed,
  production bundle built, and `npm audit` found 0 vulnerabilities.
- No Reddit account, subreddit, developer app, upload, install, read, or post was
  created or changed during these checks.

## Final installer evidence

- Setup: `artifacts\installer\CommunityRelayPOC-Setup.exe`
- Size: `141,652,333` bytes
- SHA-256: `F5ECCE7A7B830F613E69A87DA4323830BDBCD96688D595E8F208C1258BF4FA95`
- Signature: `NotSigned` (known POC distribution limitation)
- Version/RID: `0.1.0` / `win-x64`
- Embedded app and setup runtimes: `Microsoft.NETCore.App 8.0.28` and
  `Microsoft.WindowsDesktop.App 8.0.28`
- Payload/tracked files: `463` / `466`
- Expanded verifier: passed payload hashes, installed WPF input-idle/main
  window, data-preserving uninstall, redirected shortcut create/remove,
  exact-name collision rejection, real-user-shortcut nonmutation, malicious and
  forged manifest rejection, unowned-target rejection, ancestry/subtree reparse
  rejection, late-failure byte-for-byte rollback, and valid uninstall
- QA report: `artifacts\verification\verification-report.json`
