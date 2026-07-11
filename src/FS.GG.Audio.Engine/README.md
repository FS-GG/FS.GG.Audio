# FS.GG.Audio.Engine

The mixing/voice layer of the [FS.GG.Audio](https://github.com/FS-GG/FS.GG.Audio) component: named
buses with independent gain, linear fades and equal-power cross-fades, side-chain ducking, and 3D
listener/emitter positioning — all as a pure, deterministic state model advanced once per frame by
`Engine.step`.

The engine opens no device of its own. It realizes through the
[`FS.GG.Audio.Host`](https://www.nuget.org/packages/FS.GG.Audio.Host) seam, feature-detecting
`IMixingBackend` for continuous mixing and spatial control; against a plain `IAudioBackend` it
degrades gracefully, folding bus/fade/duck gains into one-shot volumes and collapsing 3D voices to
non-positional ones.

## Install

```sh
dotnet add package FS.GG.Audio.Engine
```

## Quick start

```fsharp
open FS.GG.Audio.Core
open FS.GG.Audio.Host
open FS.GG.Audio.Engine

// NullBackend is not an IMixingBackend, so 3D voices degrade to non-positional here.
// Supply an IMixingBackend (e.g. the OpenAL backend) to have them spatialize.
let engine = Engine.create (NullBackend.create () :> IAudioBackend)
Engine.setListener engine 0.0 0.0 0.0

Engine.step engine 0.016 [ Audio.playMusic (TrackId "bgm") true
                           Audio.duck Music 0.5 250.0        // side-chain duck for 250 ms
                           Audio.playSfx3D (SoundId "step") 3.0 0.0 0.0 1.0 ]

Engine.fadeBus engine Sfx 0.2 1.5         // linear fade over 1.5 s
Engine.crossFade engine Music Ambient 2.0 // equal-power, constant summed power

engine.LastVoices // the voices realized on the most recent step
```

## The sink: mixing by default

A host-level product wires a per-frame sink of type `AudioEffect list -> unit`. Building it with
`FS.GG.Audio.Host`'s `Audio.play backend` plays **straight at the device**: no mixer, no clock, so
`SetBusVolume`/`Duck` are silently discarded and `PlaySfx3D` plays non-positional — a volume slider
wired that way does nothing ([#27]). `Engine.createSink` returns a sink of that same type which steps
an owned engine instead, so the mixing semantics apply:

```fsharp
use backend = OpenAlBackend.create resolver
let audioSink = Engine.createSink backend   // AudioEffect list -> unit, and it MIXES

audioSink [ Audio.setBusVolume Sfx 0.25     // the slider now actually attenuates...
            Audio.playSfx (SoundId "click") 1.0 ]   // ...this, to 0.25
```

Create the sink **once** and reuse it: it owns the engine and the clock, so one rebuilt per frame
would start from a fresh mix every frame and no envelope would ever advance. `dt` is the interval
between successive calls, so drive it once a frame and fades and ducks progress on their own.

- `Engine.createSinkOver engine` — same, over an engine you own, so `fadeBus`/`crossFade`/
  `setListener` (which are engine calls, not effects) stay reachable.
- `Engine.createSinkWith dt engine` — you supply `dt`. Use it when the product already owns a frame
  clock, and in tests, where a wall clock is not deterministic.

[#27]: https://github.com/FS-GG/FS.GG.Audio/issues/27

## Surface

- `Engine.createSink` / `createSinkOver` / `createSinkWith` — build a mixing
  `AudioEffect list -> unit` sink (see above).
- `Engine.create` / `createWith` — build over an `IAudioBackend`; buses start at unity gain.
  `createWith` takes an explicit `SpatialConfig` (`RefDistance`, `Rolloff`, `MaxDistance`);
  `Engine.defaultSpatial` is `1`, `1`, uncapped.
- `Engine.step engine dt effects` — advance `dt` seconds and apply a batch, in order: complete
  elapsed fade/duck envelopes, apply the batch, resolve effective gains, realize through the
  backend. Total; never throws.
- `Engine.fadeBus` / `Engine.crossFade` / `Engine.setListener` — timed envelopes and listener pose.
- `T.BusGain` / `MusicGain` / `Listener` / `LastVoices` — the observable state, exposed for
  headless assertions.
- `Voice` — `EffectiveGain` = request gain × bus gain × Master gain × distance attenuation, clamped
  to `[0,1]`; `Pan` in `[-1,1]`; `Positional = false` when the voice degraded.

`Master` is not folded into `BusGain` — it is applied per voice, in `EffectiveGain`.

## Determinism

Identical inputs produce identical realized backend calls. Paired with the `NullBackend` record
path, that makes buses, fades, ducking, and 3D assertable headless, with no device in the loop.

## License

MIT — see [LICENSE](https://github.com/FS-GG/FS.GG.Audio/blob/main/LICENSE).
