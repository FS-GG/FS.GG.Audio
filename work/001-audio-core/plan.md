---
schemaVersion: 1
workId: 001-audio-core
title: Audio Core
stage: plan
changeTier: tier1
status: planned
sourceSpec: work/001-audio-core/spec.md
sourceClarifications: work/001-audio-core/clarifications.md
sourceChecklist: work/001-audio-core/checklist.md
publicOrToolFacingImpact: true
---

# Audio Core Plan

Prose status: planned

## Source Snapshot
- spec: work/001-audio-core/spec.md sha256:b7a69aafffa610ef2c2feb1f17cc7a98e3875a273dcfa7c42332011b48f2cc8a schemaVersion:1
- clarifications: work/001-audio-core/clarifications.md sha256:fb50acb584264c8cc826dda1232772ceec07831db445eb9e7f2febadfe86794b schemaVersion:1
- checklist: work/001-audio-core/checklist.md sha256:f2ac59bd8d7d4a56e7b326019775b6a281832403f0bc306cb443c5234f37147d schemaVersion:1

## Plan Scope
- Work item 001-audio-core is planned from the current specification, clarification, and checklist facts.
- Requirement count: 7.
- Clarification decision count: 3.
- Checklist result count: 8.

## Technical Context
- F# / `net10.0`, `FSharp.Core 10.1.301`. New packable library `FS.GG.Audio.Core` — the
  BCL-only bottom layer of the FS.GG.Audio component, sibling to `FS.GG.Game.Core`.
- The move is a **verbatim behavioral extraction** of `FS.GG.UI.Canvas.Audio` (donor:
  FS.GG.Rendering `src/Canvas/Audio.fs` + `.fsi`); only the owning package and namespace
  change. No FS.GG.UI, Scene, Canvas, Skia, or native-audio reference.

## Constitution Check
- **III Public Surface:** declare `src/FS.GG.Audio.Core/Audio.fsi` before `Audio.fs`; the
  full signature is fixed in `contracts/audio-core-surface.md`; a surface baseline under
  `docs/api-surface/**` is committed and drift-guarded.
- **V Model–Update–Effect:** the surface is pure values + a record-only interpreter; no
  case carries a device handle/stream/closure; real playback is the deferred host edge.
- **VI Test evidence:** the parity suite (ported from `Canvas.Tests/AudioTests`) is the
  fail-before/pass-after evidence; real fixtures (concrete `AudioEffect` lists), no mocks.

## Design
- **Project:** `src/FS.GG.Audio.Core/FS.GG.Audio.Core.fsproj` (packable), files `Audio.fsi`
  then `Audio.fs`. Version from a new `$(FsGgAudioVersion)` = `0.1.0-preview.1` property.
- **Move:** copy the donor `.fs`/`.fsi` bodies unchanged; rename `namespace FS.GG.UI.Canvas`
  → `FS.GG.Audio.Core`; reconcile doc-comment wording (DEC-002). No logic edits.
- **Dependency wiring:** central package management; the fsproj references `FSharp.Core`
  only; locked restore; deterministic build; warnings-as-errors.
- **Surface baseline:** generate/commit the `FS.GG.Audio.Core` api-surface `.fsi` snapshot;
  wire the surface-drift check like the rest of the FS-GG house style.
- **Repo-shape note (out of this work item's authored source):** whether the component
  lives in a reshaped-in-place repo or a fresh `fsgg-sdd init` repo is a stand-up decision
  tracked on the coordination track; the design above is identical either way.

## Public Surface
- Declared in full at `work/001-audio-core/contracts/audio-core-surface.md` — `SoundId`,
  `TrackId`, `AudioEffect`, `AudioEvidence`, and module `Audio` (12 members), byte-parity
  with the donor.

## Tests
- Port `Canvas.Tests/AudioTests` verbatim into `tests/FS.GG.Audio.Core.Tests` (Expecto),
  re-`open`ing `FS.GG.Audio.Core`; assertions unchanged (FR-005).
- Add explicit boundary cases for `clampVolume` (`nan`, `< 0`, `> 1`) (FR-002), `interpret`
  order preservation (FR-003), and `record` immutability (FR-004) if not already covered.
- A dependency assertion / packaging check that the built assembly references only
  `FSharp.Core` (FR-006).

## Plan Decisions
- PD-001 [AC-001] [FR-001] complete: Build `FS.GG.Audio.Core` + its Expecto test project headless (no Skia, no audio device); CI runs restore/build/test on `net10.0`.
- PD-002 [AC-002] [FR-002] complete: Preserve the total volume-clamp semantics (`nan`→min, out-of-range→boundary, never throws) across `clampVolume`/`playSfx`/`setMasterVolume` by moving the code unchanged.
- PD-003 [AC-003] [FR-003] complete: Preserve `interpret`'s oldest-first ordering and volume normalization; pin with an order-sensitive fixture test.
- PD-004 [AC-004] [FR-004] complete: Preserve `record` as a pure append over the immutable `AudioEvidence` record; pin with an input-not-mutated test.
- PD-005 [AC-005] [FR-005] complete: Port `Canvas.Tests/AudioTests` verbatim as the byte-parity gate; assertions unchanged, only the `open` namespace differs.
- PD-006 [AC-006] [FR-006] complete: Reference `FSharp.Core` only under central package management; add a packaging/dependency assertion that no `FS.GG.UI.*` or `SkiaSharp` leaks in.
- PD-007 [AC-007] [FR-007] complete: Declare the surface via `Audio.fsi` under `namespace FS.GG.Audio.Core` (contracts/audio-core-surface.md) and commit a drift-guarded api-surface baseline.
- PD-008 [DEC-004] acceptedDeferral: The three additive effect variants (PlaySfx3D/SetBusVolume/Duck) remain deferred to the FS.GG.Audio.Host work item and are visible to task generation.
- PD-009 [CR-008] acceptedDeferral: Accepted deferral CR-008 remains visible to task generation.

## Contract Impact
- PC-001 [PD-001] command report: fsgg-sdd plan, work/001-audio-core/plan.md, and command-report JSON are tool-facing and compatibility-preserving.

## Verification Obligations
- VO-001 [PD-001] [PC-001] semanticTest: Run focused command tests, FSI/prelude evidence, and CLI smoke evidence before task generation.

## Migration Posture
- PM-001 [PC-001] diagnoseOnly: Plan schemaVersion 1 is accepted; unsupported plan schemas diagnose before write.

## Generated View Impact
- GV-001 [PD-001] workModel: readiness/001-audio-core/work-model.json refreshes from current plan sources or reports staleGeneratedView.

## Accepted Deferrals
- DEC-004 acceptedDeferral: Deferral remains visible to tasks and evidence.
- CR-008 acceptedDeferral: Deferral remains visible to tasks and evidence.

## Planning Findings
No blocking planning findings recorded.

## Advisory Notes
- Optional Governance pointers remain compatibility facts only.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd tasks --work 001-audio-core`.
