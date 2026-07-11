---
schemaVersion: 1
workId: 006-host-engine-sink
title: Engine-backed audio sink for the raw-backend host path
stage: specify
changeTier: tier1
status: specified
publicOrToolFacingImpact: true
---

# Engine-backed audio sink for the raw-backend host path Specification

Prose status: specified

## User Value
A product wiring audio at the **host** boundary (no Elmish) can build its per-frame
`AudioEffect list -> unit` sink with `Engine.createSink backend` and get the Engine's mixing
semantics — bus volume, master scaling, fades, ducking, 3D positioning — by default. Today the
documented one-liner is `Host.Audio.play backend`, which plays straight at the `IAudioBackend`:
`SetBusVolume` and `Duck` are silently discarded and `PlaySfx3D` degrades to non-positional. A
settings screen's volume slider, wired exactly as documented, therefore does nothing — with no
error, no diagnostic, and no type-system objection (`FS-GG/FS.GG.Audio#27`, from
`FS-GG/FS.GG.Rendering#429`).

After this item the obvious wiring is either **correct** (`Engine.createSink`, which mixes) or
**loud** (`Host.Audio.play`, which now names, once, each effect class it structurally cannot
realize). `Host.Audio.play` remains the deliberate fire-and-forget opt-out; it is no longer the
silently-wrong default.

## Scope
- SB-001: Add an Engine-owned sink constructor family returning an `AudioEffect list -> unit` that
  advances an owned `Engine.T` once per call, so a batch realized through it is subject to
  `Engine.step` mixing semantics.
- SB-002: Own the frame clock inside the sink (a monotonic wall clock), so the returned sink matches
  the `AudioEffect list -> unit` shape the host seam already takes and time-based envelopes (fades,
  ducks) advance without the caller threading a `dt`.
- SB-003: Expose a clock-injecting variant so a product that already owns a frame clock — and the
  test suite, which must be deterministic — supplies `dt` itself rather than reading a wall clock.
- SB-004: Make the raw path loud: `Host.Audio.play` emits a one-time diagnostic naming the effects a
  raw backend structurally cannot realize (`SetBusVolume`/`Duck` dropped, `PlaySfx3D` degraded), and
  points at the mixing sink.
- SB-005: Expose the raw-path classification as a pure, public predicate so the diagnostic's meaning
  is testable headlessly rather than being locked inside a side effect.
- SB-006: Document the two host-side paths (`.fsi` + READMEs) and update the committed `.fsi` surface
  baselines.

## Non-Goals
- SB-007: No change to `Engine.step` semantics, to `Core`, or to the `IAudioBackend`/`IMixingBackend`
  seams. The sink is a thin adapter over the existing `Engine.step`.
- SB-008: No behavior change to `Host.Audio.play`'s *playback*: it still folds the batch through the
  backend in dispatch order. It gains a diagnostic, not different playback.
- SB-009: The sink helper does NOT live in `FS.GG.Audio.Host`. `FS.GG.Audio.Engine` already depends
  on `FS.GG.Audio.Host`, so a Host-level engine sink would invert the dependency graph; the sink
  lives in Engine, which is the assembly that owns mixing. See AMB-001.
- SB-010: No timer, thread, or scheduling. The sink steps the engine only when the product calls it.
- SB-011: No Elmish change — `Audio.Cmd.ofEngine` (005) already covers the Elmish path.

## User Stories
- US-001 (P1): As a host-level product author, I build my audio sink with `Engine.createSink backend`
  and my volume slider's `SetBusVolume` actually attenuates subsequent sounds, because the batch is
  realized through the Engine.
- US-002 (P2): As an author who keeps the raw `Host.Audio.play` sink, I am told once, on stderr, that
  the effects I am emitting cannot be realized on that path and which surface does realize them —
  instead of shipping a dead volume slider and hearing it from a user.
- US-003 (P2): As an author who already owns a frame clock (or writes a deterministic test), I supply
  `dt` myself via the clock-injecting sink rather than having a wall clock imposed on me.
- US-004 (P1): As a maintainer, the whole surface is headless-testable with no audio device, and the
  committed `.fsi` baseline declares it.

## Acceptance Scenarios
- AC-001 [US-001] [FR-001]: Given a sink from `Engine.createSink backend` over a recording backend, when the batch `[ setBusVolume Sfx 0.5; playSfx s 1.0 ]` is realized through it, then the backend records `PlaySfx(s, 0.5)` (bus gain folded in, `SetBusVolume` consumed by the engine) — whereas the same batch through `Host.Audio.play` records `SetBusVolume(Sfx,0.5)` then `PlaySfx(s,1.0)` at raw, unattenuated gain.
- AC-002 [US-001] [FR-002]: Given a sink over a caller-owned engine, when the sink is called across successive frames, then the same `Engine.T` is advanced each call (mix state persists) rather than a fresh engine being created per call.
- AC-003 [US-003] [FR-003]: Given `Engine.createSinkWith` supplied an explicit `dt` source, when the sink is called, then the engine is advanced by exactly that `dt`, so a `Duck` installed on one call has measurably recovered by a later call with no wall-clock dependence.
- AC-004 [US-001] [FR-004]: Given a sink created over a backend that implements `IMixingBackend`, when a `PlaySfx3D` batch is realized, then the voice stays positional (pan and distance attenuation applied), where the raw `Host.Audio.play` path would have degraded it to non-positional.
- AC-005 [US-002] [FR-005]: Given the pure predicate `Host.Audio.requiresEngine`, when it is applied to `SetBusVolume`, `Duck` and `PlaySfx3D`, then it returns true; and applied to `PlaySfx`, `PlayMusic`, `StopMusic` and `SetMasterVolume`, then it returns false.
- AC-006 [US-002] [FR-006]: Given a batch containing `SetBusVolume` driven through `Host.Audio.play`, when the batch is played, then a diagnostic naming the unrealizable effect and pointing at the mixing sink is written to stderr exactly once per process, and playback is otherwise unchanged (every effect still reaches the backend in dispatch order).
- AC-007 [US-004] [FR-007]: Given the test suite, when it runs headless with no audio device, then it exercises the sink, the clock injection, the 3D path and the diagnostic, and passes.
- AC-008 [US-004] [FR-008]: Given the committed `.fsi` surface baselines, when the public surface is compared after this change, then `docs/api-surface/FS.GG.Audio.Engine/Engine.fsi` and `docs/api-surface/FS.GG.Audio.Host/Host.fsi` match their sources with no drift.

## Functional Requirements
- FR-001: `FS.GG.Audio.Engine` MUST expose `Engine.createSink : IAudioBackend -> (AudioEffect list -> unit)`, returning a sink that owns an `Engine.T` and realizes each batch through `Engine.step`, so bus gain, master scaling, ducking and 3D positioning apply. (covers AC-001)
- FR-002: A sink MUST advance one long-lived `Engine.T` across calls, and `Engine.createSinkOver : T -> (AudioEffect list -> unit)` MUST let the caller own that engine so `fadeBus`/`crossFade`/`setListener` stay reachable. (covers AC-002)
- FR-003: `Engine.createSinkWith : (unit -> float) -> T -> (AudioEffect list -> unit)` MUST advance the engine by the `dt` its supplied clock returns, so realization is deterministic and wall-clock-free when the caller wants it. (covers AC-003)
- FR-004: Effects realized through a sink MUST be subject to `Engine.step`'s mixing semantics, including keeping `PlaySfx3D` positional on an `IMixingBackend`. (covers AC-004)
- FR-005: `FS.GG.Audio.Host` MUST expose a pure, total predicate `Audio.requiresEngine : AudioEffect -> bool` that is true exactly for the effects a raw backend structurally cannot realize (`SetBusVolume`, `Duck`, `PlaySfx3D`). (covers AC-005)
- FR-006: `Host.Audio.play` MUST emit a one-time (per-process) stderr diagnostic when a batch contains an effect satisfying `requiresEngine`, naming the effect class and the mixing sink that realizes it, while leaving dispatch-order playback unchanged. (covers AC-006)
- FR-007: The test suite MUST run headless with no audio device and cover the sink, the injected clock, the mixing/3D behavior and the one-time diagnostic. (covers AC-007)
- FR-008: The public surface MUST be declared by the committed `.fsi` files and their `docs/api-surface/` baselines updated with no drift. (covers AC-008)

## Ambiguities
- AMB-001: `FS-GG/FS.GG.Audio#27` asks for a "**Host**-level engine-backed sink helper". Taken
  literally that is impossible: `FS.GG.Audio.Engine` references `FS.GG.Audio.Host`, so a sink in
  Host that steps an Engine would invert the dependency graph. Resolved in clarify (DEC-001): the
  sink ships in `FS.GG.Audio.Engine`, which preserves the requester's intent (the documented
  one-liner mixes by default; `Audio.play` stays the explicit opt-out) at the cost of one extra
  package reference for a product that wants mixing.

## Public Or Tool-Facing Impact
- Tier 1, additive. Adds `Engine.createSink`, `Engine.createSinkOver` and `Engine.createSinkWith` to
  the `FS.GG.Audio.Engine` package surface, and `Audio.requiresEngine` to `FS.GG.Audio.Host`. No
  existing signature changes. `Host.Audio.play` gains a one-time stderr diagnostic — an observable
  behavior addition, not a playback change. Both committed `.fsi` baselines are updated.

## Lifecycle Notes
- Realizes option 1 (engine-backed sink; the requester's preferred shape) and option 2 (make the raw
  drop loud) of `FS-GG/FS.GG.Audio#27`. Next lifecycle action:
  `fsgg-sdd clarify --work 006-host-engine-sink`.
