---
schemaVersion: 1
workId: 001-audio-core
title: Audio Core
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Audio Core Specification

Prose status: specified

## User Value
A product's `update` can emit pure audio-request values (`SoundId`, `TrackId`,
`AudioEffect`) and fold them into deterministic `AudioEvidence` from a standalone,
BCL-only bottom-layer package — `FS.GG.Audio.Core` — with no dependency on FS.GG.UI /
Rendering, no Skia, and no native audio. The surface is the one that already ships in
`FS.GG.UI.Canvas.Audio`, moved without behavior change to a package that can grow a real
playback host on its own cadence.

## Scope
- SB-001: Extract the existing `FS.GG.UI.Canvas.Audio` surface (types `SoundId`,
  `TrackId`, `AudioEffect`, `AudioEvidence` and the `Audio` module) verbatim into a new
  packable `FS.GG.Audio.Core` project under the `FS.GG.Audio` namespace.
- SB-002: `FS.GG.Audio.Core` depends on `FSharp.Core` only; it builds and tests headless.
- SB-003: The behavior contract (volume clamping, effect constructors, `record`,
  `interpret` ordering) is preserved byte-for-byte and pinned by a ported test suite.

## Non-Goals
- SB-004: No new `AudioEffect` variants (`PlaySfx3D` / `SetBusVolume` / `Duck` are deferred
  to the playback-host work item).
- SB-005: No real playback backend, decoding, mixing, buses, 3D, or effects (the
  `FS.GG.Audio.Host` work item).
- SB-006: No donor-side change in FS.GG.Rendering (the `FS.GG.UI.Canvas` removal + major
  bump is a coordination-layer, cross-repo change, not this work item).
- SB-007: No org-coordination artifacts (ADR-0023, roster/registry rows, skill-ownership
  migration, board epic, publish) — those land through the `FS-GG/.github` protocol.

## User Stories
- US-001 (P1): As a product author, I emit `AudioEffect` values from `update` and fold
  them into `AudioEvidence` using `FS.GG.Audio.Core`, without my project referencing
  Rendering or Skia.
- US-002 (P1): As a framework maintainer, I build and test `FS.GG.Audio.Core` headless on
  a machine with no native audio device and no Skia.
- US-003 (P2): As a downstream consumer of the current `Canvas.Audio`, my code behaves
  identically after re-homing to `FS.GG.Audio.Core` (only the namespace changes).

## Acceptance Scenarios
- AC-001 [US-002] [FR-001]: Given a checkout of `FS.GG.Audio.Core`, when it is built and its tests run with no Skia and no audio device present, then the build succeeds and every test passes headless.
- AC-002 [US-003] [FR-002]: Given a volume value outside `[0.0, 1.0]`, when it is passed to `clampVolume`, `playSfx`, or `setMasterVolume`, then it is clamped to the nearest boundary (`0.0` or `1.0`) and no exception is thrown.
- AC-003 [US-001] [FR-003]: Given a list of `AudioEffect` values in dispatch order, when `interpret` folds them, then `AudioEvidence.Requested` contains those effects oldest-first with volumes already normalized.
- AC-004 [US-001] [FR-004]: Given a single `AudioEffect` and an existing `AudioEvidence`, when `record` is applied, then the effect is appended preserving prior order and the input evidence is unchanged (immutable value semantics).
- AC-005 [US-003] [FR-005]: Given the ported behavior suite carried over from `Canvas.Tests/AudioTests`, when it runs against `FS.GG.Audio.Core`, then every assertion that passed against `FS.GG.UI.Canvas.Audio` passes unchanged.
- AC-006 [US-002] [FR-006]: Given the built `FS.GG.Audio.Core` assembly, when its transitive package dependencies are inspected, then the only dependency is `FSharp.Core` (no `FS.GG.UI.*`, no `SkiaSharp`).
- AC-007 [US-001] [FR-007]: Given the committed `FS.GG.Audio.Core` `.fsi` surface baseline, when the public surface is compared against it, then all public types and the `Audio` module reside under the `FS.GG.Audio` namespace and match the baseline with no drift.

## Functional Requirements
- FR-001: `FS.GG.Audio.Core` MUST build and its test suite MUST pass headless, with no Skia and no native audio device. (covers AC-001)
- FR-002: `clampVolume`, and the volume-bearing constructors `playSfx` and `setMasterVolume`, MUST clamp out-of-range volume/level to `[minVolume, maxVolume]` = `[0.0, 1.0]` at the boundary and MUST NOT throw on out-of-range input. (covers AC-002)
- FR-003: `interpret` MUST fold an `AudioEffect list` into `AudioEvidence.Requested` preserving dispatch order oldest-first, with each effect's volume normalized. (covers AC-003)
- FR-004: `record` MUST append one `AudioEffect` to an `AudioEvidence` preserving prior order, as an immutable value transformation (the input value is not mutated). (covers AC-004)
- FR-005: The extracted surface MUST be a byte-parity behavioral move of `FS.GG.UI.Canvas.Audio`, pinned by a test suite ported from `Canvas.Tests/AudioTests` that passes unchanged. (covers AC-005)
- FR-006: `FS.GG.Audio.Core` MUST declare `FSharp.Core` as its only package dependency and MUST NOT reference any `FS.GG.UI.*` package or `SkiaSharp`. (covers AC-006)
- FR-007: The public surface (`SoundId`, `TrackId`, `AudioEffect`, `AudioEvidence`, module `Audio`) MUST reside under the `FS.GG.Audio` namespace and MUST be declared by a committed `.fsi` signature file with a surface baseline. (covers AC-007)

## Ambiguities
- AMB-001: Namespace/module path for the moved surface — `FS.GG.Audio` (flat, matching the
  current single-namespace `FS.GG.UI.Canvas` shape) vs. `FS.GG.Audio.Core` (matching the
  assembly/package id). Resolve in clarify.
- AMB-002: The current `.fsi` XML-doc comments say "exposed by this FS.GG.UI package."
  Verbatim *behavior* is preserved, but this doc text is now inaccurate. Decide whether
  updating doc-comment wording counts inside "verbatim move" or is an allowed cosmetic
  reconcile. Resolve in clarify.
- AMB-003: Assembly / NuGet package id and initial version — `FS.GG.Audio.Core`
  `0.1.0-preview.1` (mirroring `FS.GG.Game.Core`) assumed; confirm in clarify.

## Public Or Tool-Facing Impact
- Introduces a new public package surface (`FS.GG.Audio.Core`) declared by a `.fsi` with a
  committed surface baseline — a Tier 1, contracted change.
- Establishes the namespace `FS.GG.Audio`. The eventual removal of the same surface from
  `FS.GG.UI.Canvas` (a Canvas ApiCompat major) is tracked separately as a cross-repo
  contract change and is out of scope here (SB-006).

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd clarify --work 001-audio-core`.
