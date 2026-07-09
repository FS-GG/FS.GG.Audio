# FS.GG.Audio.Core

The pure request vocabulary at the bottom of the [FS.GG.Audio](https://github.com/FS-GG/FS.GG.Audio)
component. A product's `update` emits `AudioEffect` values — data only, no device handles, no
effectful closures — and a record-only interpreter folds a batch into `AudioEvidence`. BCL-only:
`FSharp.Core` is the single dependency.

Playing those values through a real device is the job of
[`FS.GG.Audio.Host`](https://www.nuget.org/packages/FS.GG.Audio.Host); mixing them is
[`FS.GG.Audio.Engine`](https://www.nuget.org/packages/FS.GG.Audio.Engine).

## Install

```sh
dotnet add package FS.GG.Audio.Core
```

## Quick start

```fsharp
open FS.GG.Audio.Core

// A pure update emits requests; it never plays sound.
let update model =
    model, [ Audio.playMusic (TrackId "bgm") true
             Audio.playSfx (SoundId "jump") 0.8
             Audio.setBusVolume Sfx 0.5 ]

// The record-only interpreter turns a batch into evidence.
let evidence = Audio.interpret (snd (update ()))
// evidence.Requested : AudioEffect list, in dispatch order
```

## Surface

- `SoundId` / `TrackId` — opaque, product-owned asset ids. The library never owns the id→asset map.
- `Bus` — `Master | Music | Sfx | Ui | Ambient`. `Master` scales every other bus.
- `AudioEffect` — `PlaySfx`, `PlayMusic`, `StopMusic`, `SetMasterVolume`, `PlaySfx3D`,
  `SetBusVolume`, `Duck`.
- `Audio.playSfx` / `playMusic` / `stopMusic` / `setMasterVolume` / `playSfx3D` / `setBusVolume` /
  `duck` — smart constructors that clamp carried gains into `[0.0, 1.0]` at the boundary.
- `Audio.record` / `Audio.interpret` — the pure interpreter producing `AudioEvidence`.

Every function is total: volumes clamp rather than throw (`nan` clamps to `minVolume`), and no
call opens a device or blocks.

## Determinism

The recorded evidence *is* the proof. Because nothing here touches hardware, audio behaviour is
asserted headless — identical inputs produce identical `AudioEvidence`.

## License

MIT — see [LICENSE](https://github.com/FS-GG/FS.GG.Audio/blob/main/LICENSE).
