# FS.GG.Audio.Elmish

The Elmish authoring bridge for the [FS.GG.Audio](https://github.com/FS-GG/FS.GG.Audio) component.
A product's `update` returns ordinary `Elmish.Cmd` values; the Elmish runtime executes them,
playing the underlying `FS.GG.Audio.Core` effects through an
[`FS.GG.Audio.Host`](https://www.nuget.org/packages/FS.GG.Audio.Host) `IAudioBackend`.

The commands dispatch no message — audio is fire-and-forget — so `update` stays a pure
`Model -> Model * Cmd<'msg>`. Depends on [Elmish](https://elmish.github.io/) (MIT); never on
`FS.GG.UI`.

## Install

```sh
dotnet add package FS.GG.Audio.Elmish
```

## Quick start

```fsharp
open FS.GG.Audio.Core
open FS.GG.Audio.Host
open FS.GG.Audio.Elmish

let backend = NullBackend.create () :> IAudioBackend

let update msg model =
    match msg with
    | Jumped -> model, Audio.Cmd.playSfx backend (SoundId "jump") 0.8
    | LevelStarted -> model, Audio.Cmd.playMusic backend (TrackId "bgm") true
    | LevelEnded -> model, Audio.Cmd.stopMusic backend
    | VolumeChanged v -> { model with Volume = v }, Audio.Cmd.setMasterVolume backend v
```

Batch several effects into one command with `Audio.Cmd.ofEffects`:

```fsharp
Audio.Cmd.ofEffects backend [ Audio.playSfx (SoundId "hit") 1.0
                              Audio.duck Music 0.5 250.0 ]
```

## Surface

- `Audio.Cmd.ofEffects backend effects` — play a batch through the backend in dispatch order.
- `Audio.Cmd.playSfx` / `playMusic` / `stopMusic` / `setMasterVolume` — one-effect conveniences
  mirroring the `Core.Audio` smart constructors.

Every command dispatches no message.

## Determinism

Point the commands at `NullBackend` and the Elmish loop stays headless: no device is opened, and
the backend's recorded `AudioEvidence` is the assertion surface.

## License

MIT — see [LICENSE](https://github.com/FS-GG/FS.GG.Audio/blob/main/LICENSE).
