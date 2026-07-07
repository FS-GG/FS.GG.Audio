# Contract — `FS.GG.Audio.Core` public surface

Status: planned (Tier 1, contracted). This is the signatures-first declaration
(constitution III) for work item `001-audio-core`. The `.fs` implementation and the
committed `docs/api-surface/**` surface baseline MUST match this signature.

## Provenance
- Moved verbatim (behavior + signature shape) from FS.GG.Rendering `src/Canvas/Audio.fsi`
  (`namespace FS.GG.UI.Canvas`). Per **DEC-001** the namespace becomes `FS.GG.Audio.Core`;
  per **DEC-002** the doc-comment wording is reconciled to name the new package. No type,
  case, member, arity, or signature changes (FR-005 byte-parity).

## Declared signature (`src/FS.GG.Audio.Core/Audio.fsi`)

```fsharp
namespace FS.GG.Audio.Core

/// Public contract type exposed by this FS.GG.Audio.Core package.
/// Opaque, product-owned identifier naming a sound effect. The framework does not own the
/// id -> asset mapping (kept out of the library, like per-game stat mapping in symbology); a
/// product resolves it to a real asset in its own host layer.
type SoundId = SoundId of string

/// Public contract type exposed by this FS.GG.Audio.Core package.
/// Opaque, product-owned identifier naming a music track.
type TrackId = TrackId of string

/// Public contract type exposed by this FS.GG.Audio.Core package.
/// A requested sound action, expressed as a pure value from a product's `update`. Data only —
/// no case carries a device handle, stream, or effectful closure. Volume/level are normalized
/// gains in `[0.0, 1.0]`; out-of-range values are clamped at the boundary, never thrown on.
type AudioEffect =
    | PlaySfx of sound: SoundId * volume: float
    | PlayMusic of track: TrackId * loop: bool
    | StopMusic
    | SetMasterVolume of level: float

/// Public contract type exposed by this FS.GG.Audio.Core package.
/// Ordered evidence of what a product requested, produced by the record-only interpreter. This
/// is the primary, hardware-free evidence for the headless path: the recorded requests ARE the
/// evidence (no real sound output is involved).
type AudioEvidence =
    { /// Requested effects in dispatch order, oldest first, with volumes normalized.
      Requested: AudioEffect list }

/// Public contract module exposed by this FS.GG.Audio.Core package.
/// The audio request vocabulary plus a pure record-only interpreter. A product's `update` emits
/// `AudioEffect` values (it never plays sound); the interpreter folds a batch into `AudioEvidence`.
/// A real audio-output backend is deferred (FS.GG.Audio.Host) and will consume the same values
/// without changing this surface.
[<RequireQualifiedAccess>]
module Audio =

    /// Lower bound of the normalized volume range.
    val minVolume: float

    /// Upper bound of the normalized volume range.
    val maxVolume: float

    /// Clamp a requested volume into `[minVolume, maxVolume]`. Total; never throws. `nan` clamps to
    /// `minVolume`.
    val clampVolume: level: float -> float

    /// Smart constructor for a sound-effect request; clamps the carried volume at the boundary.
    val playSfx: sound: SoundId -> volume: float -> AudioEffect

    /// Smart constructor for a music request.
    val playMusic: track: TrackId -> loop: bool -> AudioEffect

    /// Request that the current music track stop. A no-op at the interpreter if nothing is playing.
    val stopMusic: AudioEffect

    /// Smart constructor for a master-volume request; clamps the carried level at the boundary.
    val setMasterVolume: level: float -> AudioEffect

    /// Evidence with no requests recorded yet.
    val emptyEvidence: AudioEvidence

    /// Record-only interpreter over a single requested effect: append it to the evidence (pure,
    /// total). Carried volumes are normalized so recorded evidence is always in range.
    val record: effect: AudioEffect -> evidence: AudioEvidence -> AudioEvidence

    /// Record-only interpreter over a batch, preserving dispatch order. This is the headless-safe
    /// host boundary: no device access, never blocks, never throws. Returns the accumulated evidence.
    val interpret: effects: AudioEffect list -> AudioEvidence
```

## Behavioral invariants (the contract the port must preserve)
- **[FR-002]** `clampVolume nan = minVolume`; values `< 0.0 → 0.0`, `> 1.0 → 1.0`; `playSfx`
  and `setMasterVolume` carry clamped volumes. Total, never throws.
- **[FR-003]** `interpret` preserves dispatch order oldest-first; carried volumes normalized.
- **[FR-004]** `record` is a pure append; the input `AudioEvidence` value is not mutated.
- **[FR-006]** Package depends on `FSharp.Core` only — no `FS.GG.UI.*`, no `SkiaSharp`.
- **[FR-007]** All symbols under `namespace FS.GG.Audio.Core`, declared by this `.fsi` + a
  committed surface baseline.

## Packaging
- **[DEC-003]** Project/NuGet id `FS.GG.Audio.Core`; version `0.1.0-preview.1` sourced from a
  new `$(FsGgAudioVersion)` MSBuild property (its own axis, independent of `$(FsGgUiVersion)`
  and `$(FsGgGameVersion)`).
- `net10.0`, `FSharp.Core 10.1.301`, central package management + locked restore, deterministic
  build, warnings-as-errors — the FS-GG house style, matching `FS.GG.Game.Core`.
