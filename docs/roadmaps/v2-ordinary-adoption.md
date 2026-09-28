# C3-AUDIO-01 — Ordinary V2 receiver adoption

Status: source prepared and disabled. Installation and activation remain pending.

FS.GG.Audio is the selected C3 source repository (`FS-GG/FS.GG.Audio`, repository ID
`1292226968`) under the `audio-v1` profile. This change adds only repository-owned receiver
source. It does not change an existing production workflow, repository settings, an Actions
environment, secrets, branch protection, required checks, or generated workspace content.

## Prepared source

- The receiver workflow is bound to protected-main pushes but its only job has an unconditional
  false guard. It contains read-only GitHub permissions, persists no checkout credential, and has
  no credential job, environment, secret reference, package download, or settlement command.
- The observer and qualification tools are exact copies of `.github` commit
  `7e1ac5f4689cbc8cd9b6db93b5ed7898c0c68319`. Their SHA-256 digests are
  `6081e1f499a618e3d019bd4508c630c6ee52d2bad018c8ecdda3e24c360e452f` and
  `304ce2894983dec1ed5f195bf58b0e2d01bceea68365d4e18296f00c068a3b2e` respectively.
- The selected check population is the four current native required checks plus the additional
  `routine-eligibility` check. Each check is bound to GitHub Actions App `15368` and its exact
  workflow ID, path, event, PR head, run, job, suite, and attempt.
- The policy remains `source-qualified-not-installed`. The future immutable Coordination CLI
  version and package SHA-256 are deliberately null and unresolved. Credential inventory is empty.
- The Authority target remains the shared `OpenV2` epoch and App `5064713`, installation
  `164553252`, limited to `FS-GG/FS.GG.Coordination.Authority` (`1351660651`) with
  `contents:write` and metadata read. No V1 admission or receiver-state import is used.

## Installation boundary

Do not enable the workflow or add a credential job until all of these facts are available and
verified together:

1. a newly published immutable Coordination CLI supports the `audio-v1` source profile;
2. its exact version and package SHA-256 are recorded in policy and workflow source;
3. a dedicated `ordinary-v2` environment exists with an exact main-only deployment policy;
4. dedicated approved ordinary-v2 credentials are enrolled without reusing V1 or callable-operation
   credentials; and
5. the source repository identity and current required-check population are read back again.

Activation is a separate reviewed source change after those prerequisites. It must add the bounded
credential job and remove the false guard together; merely merging this source cannot settle work.

## First activated behavior

For the first protected-main merge after activation, the secret-free predecessor will bind the
single merged PR, exact qualified head/tree, Audio repository identity, all four native gates and
`routine-eligibility`, then retain its public receipt. Only a matching receipt with activation true
may reach the credential job and invoke the pinned Audio-capable CLI against the shared Authority
journal. A normal rerun must read back the already-complete result without creating a second
settlement. Failure leaves the receiver disabled or is repaired forward; no migration rollback or
legacy state recovery is part of this adoption.

Telemetry attempt `c3-audio-receiver-20260928` is `not-configured`; no usage total is claimed.
