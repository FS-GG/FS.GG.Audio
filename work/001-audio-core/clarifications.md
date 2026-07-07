---
schemaVersion: 1
workId: 001-audio-core
stage: clarify
sourceSpec: work/001-audio-core/spec.md
---

# Clarifications

## Source Specification
- work/001-audio-core/spec.md

## Clarification Questions
- **CQ-001** (AMB-001): What namespace and package id does the moved surface take — flat `FS.GG.Audio`, or `FS.GG.Audio.Core` matching the package id?
- **CQ-002** (AMB-002): Does updating the `.fsi` XML-doc wording that currently says "this FS.GG.UI package" count as a breach of "verbatim move," or is it an allowed cosmetic reconcile?
- **CQ-003** (AMB-003): What are the initial package id and version, and on which version axis?

## Answers
- CQ-001 → Use `FS.GG.Audio.Core` as both the namespace and the package id, by the `FS.GG.Game.Core` precedent (its `.fsi` files declare `namespace FS.GG.Game.Core`, namespace = package id — not a flat `FS.GG.Game`). Resolves AMB-001.
- CQ-002 → Doc-comment wording is not behavior; reconciling "FS.GG.UI package" → "FS.GG.Audio.Core" is an allowed cosmetic part of the move. "Verbatim" binds the *behavior* and the *signature shape*, not stale prose. Resolves AMB-002.
- CQ-003 → `FS.GG.Audio.Core`, initial version `0.1.0-preview.1`, on a new `$(FsGgAudioVersion)` MSBuild property — its own axis, mirroring `$(FsGgGameVersion)` for `FS.GG.Game.Core` (a bottom-layer package released independently of `$(FsGgUiVersion)`). Resolves AMB-003.

## Decisions
- **DEC-001** [CQ-001] [AMB:AMB-001] [FR-007]: The moved surface lives under `namespace FS.GG.Audio.Core`; the packable project and NuGet id are both `FS.GG.Audio.Core`.
- **DEC-002** [CQ-002] [AMB:AMB-002] [FR-005]: `.fsi` doc-comment text is reconciled to name `FS.GG.Audio.Core`; behavior and signatures stay byte-parity, so this does not violate the verbatim-move principle.
- **DEC-003** [CQ-003] [AMB:AMB-003] [FR-006]: Ship as `FS.GG.Audio.Core` `0.1.0-preview.1` on a new `$(FsGgAudioVersion)` axis, independent of `$(FsGgUiVersion)`.

## Accepted Deferrals
- **DEC-004**: The three additive `AudioEffect` variants (`PlaySfx3D`, `SetBusVolume`, `Duck`) named in the audio design doc are deferred to the `FS.GG.Audio.Host` work item that implements them — recorded here, not dropped.

## Remaining Ambiguity
- None. AMB-001, AMB-002, and AMB-003 are all resolved above.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 001-audio-core`.
