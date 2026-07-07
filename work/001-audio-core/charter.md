---
schemaVersion: 1
workId: 001-audio-core
title: FS.GG.Audio.Core — pure audio vocabulary extracted from FS.GG.UI.Canvas
stage: charter
changeTier: tier1
status: chartered
policyPointers:
  - .fsgg/sdd.yml
  - .fsgg/agents.yml
  - .fsgg/policy.yml
  - .fsgg/capabilities.yml
  - .fsgg/tooling.yml
---

# FS.GG.Audio.Core — pure audio vocabulary extracted from FS.GG.UI.Canvas Charter

## Identity
- Work id: `001-audio-core`
- Lifecycle stage: charter
- Status: chartered

`FS.GG.Audio.Core` is the pure, BCL-only audio request vocabulary that a product's
`update` emits — `SoundId`, `TrackId`, the `AudioEffect` DU, `AudioEvidence`, and the
`Audio` module (volume clamping, effect constructors, and the deterministic record-only
`interpret`). It is the foundational package of the new **FS.GG.Audio** framework
component and its new **bottom layer**: extracted verbatim from `FS.GG.UI.Canvas.Audio`
in FS.GG.Rendering, it depends on nothing but `FSharp.Core` and is a sibling to
`FS.GG.Game.Core`. This work item stands up the package and moves the surface; it does
**not** build the real playback host (a later work item) and does **not** itself execute
the donor-side removal in Rendering (a coordination-layer, cross-repo change).

## Principles
- **Verbatim move, not a rewrite.** The extracted surface is byte-for-byte the current
  `FS.GG.UI.Canvas.Audio` behavior; only the owning package/namespace changes. Behavior
  parity is a tested property, not an assertion.
- **Pure edge, effectful host** (constitution V). `FS.GG.Audio.Core` carries no device
  handle, stream, or effectful closure; it is a pure function of values. Real sound is a
  separate host package that consumes these values unchanged.
- **BCL-only bottom layer.** `FSharp.Core` is the only dependency. No Skia, no Scene, no
  Canvas — audio ends up fully render-independent (cleaner than `FS.GG.Game.Render`, which
  reaches up to Scene). The one-way dependency rule is preserved: Core reaches up to
  nothing.
- **Public surface is declared** (constitution III). The `.fsi` is the sole public
  surface with a committed surface baseline; determinism of `interpret` and volume
  clamping at `[0.0, 1.0]` boundaries are contract properties.
- **Deterministic and headless.** Builds and tests with zero native audio and zero Skia;
  the recorded `AudioEvidence` is the primary hardware-free evidence.

## Scope Boundaries
- **In:** create the `FS.GG.Audio.Core` project; move the `Audio` module (`Audio.fs` /
  `Audio.fsi`) and its types verbatim from `FS.GG.UI.Canvas` into it under the
  `FS.GG.Audio` namespace; port the Canvas `AudioTests` as the parity/behavior suite;
  commit the `.fsi` surface baseline; make it build + test headless.
- **Out (separate work items):** `FS.GG.Audio.Host` (the `IAudioBackend` seam + Null /
  OpenAL-via-Silk.NET / miniaudio backends, decoding, buses/3D/EFX) — its own charter.
- **Out (coordination layer, not SDD):** executing the **Canvas ApiCompat major** in
  FS.GG.Rendering (removing `Audio.*`, re-homing the game starter to consume
  `FS.GG.Audio.Core`); publishing to the org feed + nuget.org; the ADR-0023 repo-boundary
  decision; the `registry/repos.yml` roster row, `registry/dependencies.yml` contract +
  coherence rows, `skills.yml` `fs-gg-audio` ownership migration, and the Coordination
  board epic. These land through the cross-repo protocol in `FS-GG/.github`, sequenced
  publish-before-flip (FR-007).

## Policy Pointers
- SDD policy comes from `.fsgg/sdd.yml` and `.fsgg/agents.yml`.
- Honors constitution principles I (specify-before-implement), III (public surface is
  declared), V (Model–Update–Effect / pure edge), and VI (test evidence mandatory).
- Tier 1 change: introduces a public package surface, so signatures, surface baseline,
  tests, and docs land together.
- Governance files are optional compatibility pointers and are not evaluated by this
  command.

## Lifecycle Notes
- Follows the FS.GG.Game extraction playbook (ADR-0022; `.github`
  `docs/reports/2026-07-06-extract-fs-gg-game-component-sdd-driven.md` §7–§8). This work
  item is the build substance behind that epic's "P2 — Stand up" phase, applied to audio.
- Source of the surface being moved: FS.GG.Rendering `src/Canvas/Audio.fsi` (4 KB) +
  `Audio.fs` (2.4 KB); the only donor consumers are `tests/Canvas.Tests/AudioTests.fs` and
  spec `243-audio-effect-surface` — a clean leaf with no `src/` dependents.
- Next lifecycle action: `fsgg-sdd specify --work 001-audio-core`.
