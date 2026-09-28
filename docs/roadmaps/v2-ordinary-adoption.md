# C3-AUDIO-01 — Ordinary V2 receiver adoption

Status: activation source prepared and CLI published; credential enrollment remains pending.

FS.GG.Audio is the selected C3 source repository (`FS-GG/FS.GG.Audio`, repository ID
`1292226968`) under the `audio-v1` profile. This change adds only repository-owned receiver
source. It does not change repository settings, the existing Actions environment, secrets, branch
protection, required checks, or generated workspace content, and it cannot reach a credential effect.

## Prepared source

- The secret-free predecessor now runs on protected-main pushes with read-only GitHub permissions
  and no persisted checkout credential. It emits the only activation signal consumed by the
  credential job.
- The credential job is fully shaped but can run only when that exact predecessor receipt says
  `activation=true`. The checked-in policy remains not installed, so the prepared source can emit
  only `activation=false` and the job is skipped before environment or secret access.
- The observer and qualification tools derive from the exact bytes at `.github` commit
  `7e1ac5f4689cbc8cd9b6db93b5ed7898c0c68319`, with the observer boundedly adapted to accept the
  Audio-local protected policy as its current authority and to derive activation from that policy.
  Their SHA-256 digests are `6e63f6f724fb267e77ef02f41b4c58a833e0dc5b08c003f07fd16c89e4f7fd55`
  and `304ce2894983dec1ed5f195bf58b0e2d01bceea68365d4e18296f00c068a3b2e` respectively.
- The selected check population is the four current native required checks plus the additional
  `routine-eligibility` check. Each check is bound to GitHub Actions App `15368` and its exact
  workflow ID, path, event, PR head, run, job, suite, and attempt.
- The policy remains `source-qualified-not-installed`. Coordination CLI `0.1.3` was published by
  protected run `36407941780` and independently read back from release `v0.1.3`. Its served
  `FS.GG.Coordination.Cli.0.1.3.nupkg` asset has SHA-256
  `c505159023f0740c885696ebe24f8e066e197d9cf09cc3e64050d4210bcd0cdb`, exactly matching hosted
  candidate run `36404053953`.
- The `ordinary-v2` environment exists and is restricted to the single `main` branch policy, but
  it contains zero secrets. All three dedicated credential inventory entries remain unprovisioned.
- The Authority target remains the shared `OpenV2` epoch and App `5064713`, installation
  `164553252`, limited to `FS-GG/FS.GG.Coordination.Authority` (`1351660651`) with
  `contents:write` and metadata read. No V1 admission or receiver-state import is used.

## Installation boundary

Do not mark the policy installed or permit an `activation=true` receipt until all of these facts are
available and verified together:

1. all three dedicated ordinary-v2 credentials are enrolled and read back without reusing V1 or
   callable-operation credentials; and
2. the source repository identity and current required-check population are read back again.

Activation is a separate reviewed source change after those prerequisites. It changes policy status,
installed state, served-package evidence, and credential inventory together; merely merging this
prepared source cannot settle work.

## First activated behavior

For the first protected-main merge after activation, the secret-free predecessor will bind the
single merged PR, exact qualified head/tree, Audio repository identity, all four native gates and
`routine-eligibility`, then retain its public receipt. Only a matching receipt with activation true
may reach the credential job and invoke the pinned Audio-capable CLI against the shared Authority
journal. A normal rerun must read back the already-complete result without creating a second
settlement. Failure leaves the receiver disabled or is repaired forward; no migration rollback or
legacy state recovery is part of this adoption.

Telemetry attempt `c3-audio-receiver-20260928` is `not-configured`; no usage total is claimed.
