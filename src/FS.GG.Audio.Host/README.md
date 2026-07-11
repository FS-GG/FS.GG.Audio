# FS.GG.Audio.Host

The device seam of the [FS.GG.Audio](https://github.com/FS-GG/FS.GG.Audio) component. It turns the
pure `AudioEffect` values of
[`FS.GG.Audio.Core`](https://www.nuget.org/packages/FS.GG.Audio.Core) into playback through a
narrow `IAudioBackend` interface, with two implementations: a deterministic Null/record backend
(the default, and the one CI runs against) and a real OpenAL backend on Silk.NET that degrades to
Null when no device is present.

Game-facing code holds an `IAudioBackend` and never names a concrete backend type.

## Install

```sh
dotnet add package FS.GG.Audio.Host
```

## Quick start

```fsharp
open FS.GG.Audio.Core
open FS.GG.Audio.Host

// Headless / test: records requests, opens no device.
let backend = NullBackend.create ()
Audio.play (backend :> IAudioBackend) [ Audio.playSfx (SoundId "jump") 0.8 ]
backend.Evidence.Requested // = the batch, volumes normalized

// Real device: the product supplies the id -> PCM mapping.
let resolver =
    { ResolveSound = fun (SoundId id) -> loadWav $"sfx/{id}.wav"
      ResolveTrack = fun (TrackId id) -> loadWav $"music/{id}.wav" }

use device = OpenAlBackend.create resolver // falls back to Null if no device
```

## Which sink? `Audio.play` does not mix

A product wires a per-frame sink of type `AudioEffect list -> unit`. There are two, and the
difference is load-bearing:

```fsharp
// RAW — plays straight at the backend. No mixing.
let audioSink = Audio.play backend

// MIXING — steps an owned FS.GG.Audio.Engine. Bus volume, ducking, fades and 3D all apply.
let audioSink = Engine.createSink backend
```

`Audio.play` is a thin drive over `IAudioBackend`, and a backend has no mixer and no clock. So on the
raw path `SetBusVolume` and `Duck` are **discarded** and `PlaySfx3D` plays **non-positional** — which
means a settings screen's volume slider, wired this way, does nothing at all: the effect is a
well-formed value, the sink accepts it, and no sound changes ([#27]). `Audio.requiresEngine` is the
predicate for exactly those effects, and the first batch carrying one logs a diagnostic saying so.

Reach for `Audio.play` when you want deliberate fire-and-forget playback; reach for
[`FS.GG.Audio.Engine`](https://www.nuget.org/packages/FS.GG.Audio.Engine)'s `Engine.createSink`
whenever bus volume, ducking, fades or 3D matter. (Elmish products have the same choice between
`Audio.Cmd.ofEffects` and `Audio.Cmd.ofEngine`.)

[#27]: https://github.com/FS-GG/FS.GG.Audio/issues/27

## Surface

- `Audio.play` — the raw imperative drive: folds a batch through the backend in dispatch order. No
  mixing (see above).
- `Audio.requiresEngine` — pure predicate, true for exactly the effects the raw path cannot realize
  (`SetBusVolume`, `Duck`, `PlaySfx3D`).
- `IAudioBackend` — `Play: AudioEffect -> unit`, plus `IDisposable`. Never throws; a backend that
  cannot act degrades to a no-op.
- `IMixingBackend` — optional extension (`SetBusGain`, `SetListener`, `PlayAt`) that
  `FS.GG.Audio.Engine` feature-detects for continuous mixing and 3D. Backends implementing only
  `IAudioBackend` remain valid.
- `NullBackend.create` — the record-only backend; its `Evidence` equals `Core.Audio.interpret` of
  the same batch.
- `OpenAlBackend.create` — opens an OpenAL device, or logs the reason and returns a Null backend.
  The result is always usable, never null, and never throws into game code. **That substitution is
  silent unless you ask** — see `Backend` below.
- `Backend.kindOf` / `Backend.isDeviceBacked` — what did I actually get? Answers `DeviceBacked`,
  `RecordOnly of Silence` (`Requested` — you asked for it; or `DeviceUnavailable reason` — it was
  substituted under you), or `Unknown` for a backend this library did not build. No type test, no
  exception handling.
- `AssetResolver` — caller-supplied `SoundId`/`TrackId` → WAV bytes. An unresolved id is a recorded
  no-op, not a throw.
- `Wav.tryParse` — a total, device-free minimal PCM WAV reader; returns `None` on anything it does
  not understand.

## Determinism

`NullBackend` is the default backend under test: no device is opened and the recorded evidence is
the assertion surface.

The OpenAL path, by contrast, is exercised by whatever the machine happens to have. `OpenAlBackend.create`
degrades to `NullBackend` when no device opens (FR-004), so on a headless box a test that drives
playback is asserting against a **recorder** — and passes *because* nothing played. A green tick on a
subject that was never constructed is reporting on nothing.

So a test that needs a real device must **ask, and skip loudly when it has no subject** (#34):

```fsharp
let backend = OpenAlBackend.create resolver
if Backend.isDeviceBacked backend then
    // a device really opened — assert against it
else
    skiptest "no audio device here — reported Ignored rather than Passed"
```

`isDeviceBacked` is `false` for `Unknown` on purpose: a record-only fake is an `IAudioBackend` that
is not `NullBackend.T`, so a "not-Null means device" rule would wave it through as audible — the same
vacuous green, one level up.

## License

MIT — see [LICENSE](https://github.com/FS-GG/FS.GG.Audio/blob/main/LICENSE).
