---
schemaVersion: 1
workId: 006-host-engine-sink
title: Engine-backed audio sink for the raw-backend host path
stage: clarify
changeTier: tier1
status: clarified
sourceSpec: work/006-host-engine-sink/spec.md
publicOrToolFacingImpact: true
---

# Engine-backed audio sink for the raw-backend host path Clarifications

## Source Specification
- work/006-host-engine-sink/spec.md

## Clarification Questions
- CQ-001 [AMB:AMB-001] blocking answered: `FS-GG/FS.GG.Audio#27` asks for a "Host-level engine-backed sink helper". Which assembly ships it, given `FS.GG.Audio.Engine` already references `FS.GG.Audio.Host`?
- CQ-002 blocking answered: The sink's type is `AudioEffect list -> unit`, but `Engine.step` needs a `dt`. Where does the frame clock come from?
- CQ-003 blocking answered: Where does the "make the raw path loud" diagnostic live, so that it fires on the silently-wrong wiring and never on a correct one?

## Answers
- CQ-001 → Ship the sink in `FS.GG.Audio.Engine`. Host cannot host it: `FS.GG.Audio.Engine` has a `ProjectReference` to `FS.GG.Audio.Host` (Engine's `IAudioBackend`/`IMixingBackend` come from Host), so an engine-stepping helper inside Host would be a dependency cycle. Engine is also the assembly that *owns* mixing, so the sink is where the semantics are. The requester's intent survives intact: the documented one-liner mixes by default and `Host.Audio.play` stays the explicit fire-and-forget opt-out. The only cost is that a host-level product that wants mixing now also references the `FS.GG.Audio.Engine` package — which it must anyway, since `Engine.T` is the thing that mixes.
- CQ-002 → The sink owns a monotonic wall clock (`System.Diagnostics.Stopwatch`) and derives `dt` from the interval between successive calls, because the host seam's sink type carries no `dt` and a per-frame call is exactly the cadence envelopes need. The first call advances the engine by `0.0` rather than by the interval since construction, so a sink built at startup and first driven seconds later cannot jump an envelope installed in between. A clock-injecting variant (`createSinkWith`) keeps the wall clock out of tests and serves products that already own a frame clock.
- CQ-003 → In `Host.Audio.play`, not in a backend. `Engine.step` never routes `SetBusVolume`/`Duck`/`PlaySfx3D` to `IAudioBackend.Play` (it consumes them and calls `SetBusGain`/`PlayAt` instead), so a diagnostic on the raw drive fires exactly when a product wired the raw path and emitted an effect that path cannot realize — and never when the Engine is driving. Placing it in the OpenAL backend instead would be untestable headlessly (CI has no device and falls back to the Null backend), and placing it in the Null backend would be wrong, since recording those effects as evidence is precisely the Null backend's contract.

## Decisions
- **DEC-001** [CQ-001] [AMB:AMB-001] [FR-001] [FR-002]: The engine-backed sink ships in `FS.GG.Audio.Engine` as `Engine.createSink` / `createSinkOver` / `createSinkWith`, NOT in `FS.GG.Audio.Host`. Rationale: `Engine -> Host` is the existing dependency direction, so the literal request would invert it. Resolves AMB-001.
- **DEC-002** [CQ-002] [FR-002] [FR-003]: A sink owns exactly one long-lived `Engine.T` and a monotonic `Stopwatch`; `dt` is the interval between successive sink calls, and the first call uses `dt = 0.0`. `createSinkWith` injects the clock instead. A sink is therefore a *construction*, named `create…` so that per-frame reconstruction — which would reset the mix every frame — reads as the mistake it is.
- **DEC-003** [CQ-003] [FR-005] [FR-006]: The raw-path diagnostic lives in `Host.Audio.play`, is emitted at most once per process, and is defined by a pure public predicate `Audio.requiresEngine` so its meaning is asserted headlessly rather than being trapped inside a side effect. `Audio.play`'s playback is unchanged.

## Accepted Deferrals
No accepted deferrals recorded.

## Remaining Ambiguity
No blocking ambiguity remains.

## Lifecycle Notes
- Next lifecycle action: `fsgg-sdd checklist --work 006-host-engine-sink`.
