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

## Surface

- `IAudioBackend` — `Play: AudioEffect -> unit`, plus `IDisposable`. Never throws; a backend that
  cannot act degrades to a no-op.
- `IMixingBackend` — optional extension (`SetBusGain`, `SetListener`, `PlayAt`) that
  `FS.GG.Audio.Engine` feature-detects for continuous mixing and 3D. Backends implementing only
  `IAudioBackend` remain valid.
- `NullBackend.create` — the record-only backend; its `Evidence` equals `Core.Audio.interpret` of
  the same batch.
- `OpenAlBackend.create` — opens an OpenAL device, or logs the reason and returns a Null backend.
  The result is always usable, never null, and never throws into game code.
- `AssetResolver` — caller-supplied `SoundId`/`TrackId` → WAV bytes. An unresolved id is a recorded
  no-op, not a throw.
- `Wav.tryParse` — a total, device-free minimal PCM WAV reader; returns `None` on anything it does
  not understand.

## Determinism

`NullBackend` is the default backend under test: no device is opened and the recorded evidence is
the assertion surface. The OpenAL path is exercised only behind an opt-in manual lane, never in the
CI assertion path.

## License

MIT — see [LICENSE](https://github.com/FS-GG/FS.GG.Audio/blob/main/LICENSE).
