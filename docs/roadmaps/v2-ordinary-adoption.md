# C3-AUDIO-01 — Ordinary V2 receiver adoption

Status: **C3-AUDIO-01 complete for the selected Audio receiver**. Protected-main settlement and
normal whole-run already-complete replay were independently read back on 2026-09-28.

FS.GG.Audio is the selected C3 source repository (`FS-GG/FS.GG.Audio`, repository ID
`1292226968`) under the `audio-v1` profile. This change adds only repository-owned receiver
source and an explicitly selected ordinary-V2 installation. It does not change branch protection,
native required checks, or generated workspace content.

## Prepared source

- The secret-free predecessor now runs on protected-main pushes with read-only GitHub permissions
  and no persisted checkout credential. It emits the only activation signal consumed by the
  credential job.
- The credential job is fully shaped but can run only when that exact predecessor receipt says
  `activation=true`. The checked-in installed policy can produce that result only after binding the
  exact protected-main merge, associated PR head/tree, native checks, producer workflows, and current
  policy/workflow/anchor bytes.
- The observer and qualification tools derive from the exact bytes at `.github` commit
  `7e1ac5f4689cbc8cd9b6db93b5ed7898c0c68319`, with the observer boundedly adapted to accept the
  Audio-local protected policy as its current authority and to derive activation from that policy.
  Their SHA-256 digests are `6e63f6f724fb267e77ef02f41b4c58a833e0dc5b08c003f07fd16c89e4f7fd55`
  and `7eccceae0aad930d3cdbce24767cbb9555693ca7032e660d1d71519a79529454` respectively.
- The selected settlement checks are `Build + test` and `routine-eligibility`; all four native
  required gates are verified separately. Each check is bound to GitHub Actions App `15368` and its exact
  workflow ID, path, event, PR head, run, job, suite, and attempt.
- The policy is `installed`. Coordination CLI `0.1.3` was published by
  protected run `36407941780` and independently read back from release `v0.1.3`. Its served
  `FS.GG.Coordination.Cli.0.1.3.nupkg` asset has SHA-256
  `c505159023f0740c885696ebe24f8e066e197d9cf09cc3e64050d4210bcd0cdb`, exactly matching hosted
  candidate run `36404053953`.
- The `ordinary-v2` environment is restricted to the single `main` branch policy. Protected bridge
  run `36410714353` sealed the existing dedicated custody to Audio's environment key, verified its
  signed exact-run/policy/key/empty-inventory packet, and applied exactly three encrypted values.
  Independent readback at `2026-09-28T10:37:35Z` returned exactly the three expected secret names.
- The Authority target remains the shared `OpenV2` epoch and App `5064713`, installation
  `164553252`, limited to `FS-GG/FS.GG.Coordination.Authority` (`1351660651`) with
  `contents:write` and metadata read. No V1 admission or receiver-state import is used.

## Installed settlement and replay

Activation [PR #327](https://github.com/FS-GG/FS.GG.Audio/pull/327) merged at
`04f07ac2ec7cd9b2a57c76bf2d5e18d27d99e921` after native checks. Three protected attempts
exposed bounded integration mismatches and stopped before an Authority write: NuGet archive byte identity,
shared Authority policy ID, and settlement check population. [PRs #330](https://github.com/FS-GG/FS.GG.Audio/pull/330),
[#331](https://github.com/FS-GG/FS.GG.Audio/pull/331) and
[#332](https://github.com/FS-GG/FS.GG.Audio/pull/332) repaired these in source under the normal gate.

[Protected run `36413290713`](https://github.com/FS-GG/FS.GG.Audio/actions/runs/36413290713) on
main `99207b52298352f16cd383b5d389b0dacb4b49ca` then passed secret-free preflight, installed
the pinned public CLI archive, and returned `SettlementSucceeded` with receipt digest
`9605af1e28ae79518018d03ca4fdfe5c33ab33c268153ec25d2220830fb9fc4e`. Independent Authority
readback found one complete entry for that digest under
`refs/heads/fsgg/v2/journal/operation/9a` at commit
`a51c567cb5f738402d3ee6c76eff96ecae8ac49a`. A normal whole-run rerun (attempt 2) passed both jobs
and returned `SettlementAlreadyComplete` with the identical digest. The journal head remained
`a51c567cb5f738402d3ee6c76eff96ecae8ac49a` with one entry, establishing no second write.

This closes explicit Audio adoption only. It does not imply another repository is activated, a V1
migration was performed, or that an unrelated lifecycle default changed. Future failures use source
repair and protected readback; no legacy rollback obligation was introduced.

Telemetry attempt `c3-audio-receiver-20260928` is `not-configured`; no usage total is claimed.
