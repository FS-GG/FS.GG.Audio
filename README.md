# FS.GG.Audio

Render-independent game-audio component for the FS-GG platform. A product's `update` emits pure
`AudioEffect` values; the layers below turn them into named-bus mixing, fades/ducking, 3D
positioning, and real device output — all reproducible on a deterministic, device-free record path
so audio is testable headless.

## Packages

| Package | What it is | Depends on |
|---|---|---|
| **FS.GG.Audio.Core** | Pure request vocabulary (`AudioEffect`, `Bus`, `SoundId`/`TrackId`) + a record-only interpreter (`AudioEvidence`). BCL-only. | FSharp.Core |
| **FS.GG.Audio.Host** | The `IAudioBackend` device seam + a deterministic Null/record backend and a real OpenAL (Silk.NET) backend that degrades to Null with no device. Optional `IMixingBackend` for mixing/spatial control. | Core, Silk.NET.OpenAL, Silk.NET.OpenAL.Soft.Native † |
| **FS.GG.Audio.Engine** | Mixing/voice layer: named buses (Master/Music/Sfx/Ui/Ambient), linear fades + equal-power cross-fades, side-chain ducking, 3D listener/emitters. Pure deterministic `Engine.step`. | Host †, Core |
| **FS.GG.Audio.Elmish** | Thin Elmish `Cmd` authoring bridge (`Audio.Cmd.playSfx …`) over the host. Never depends on `FS.GG.UI`. | Host †, Core, Elmish |

† **Redistributes the OpenAL Soft native library, which is LGPL-2.0-or-later** — as a separate,
dynamically-linked, replaceable shared library, never linked into an FS.GG.Audio assembly (DEC-001).
Restoring Host, Engine or Elmish puts `libopenal` into your output under `runtimes/<rid>/native/`.
**FS.GG.Audio.Core carries no native code and no OpenAL**, if you need to avoid it entirely. See
**[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)** for what ships, how to replace it, and how to
exclude it.

```text
GAME (pure update: Model -> Model * AudioEffect list)
        │  AudioEffect values
        ▼
FS.GG.Audio.Engine  — buses · fades · ducking · 3D   (deterministic state model)
        │  IAudioBackend / IMixingBackend
        ▼
FS.GG.Audio.Host    — Null/record (default, headless)  |  OpenAL (Silk.NET, degrade-to-Null)
```

## Quick start

```fsharp
open FS.GG.Audio.Core
open FS.GG.Audio.Host
open FS.GG.Audio.Engine

let engine = Engine.create (NullBackend.create () :> IAudioBackend)
Engine.step engine 0.016 [ Audio.playMusic (TrackId "bgm") true
                           Audio.playSfx (SoundId "jump") 0.8 ]
```

See **[`samples/FS.GG.Audio.Sample`](samples/FS.GG.Audio.Sample)** for a headless, deterministic
demo of buses, a fade-in, ducking, and 3D — run it with:

```sh
dotnet run --project samples/FS.GG.Audio.Sample
```

## Determinism & testing

The default backend under test is Null/record — no device is opened, and the recorded evidence /
engine state *is* the proof. The whole suite runs headless in CI (`gate.yml`). The real OpenAL
device backend is exercised only behind an opt-in manual lane, never in the CI assertion path.

## Development

Built spec-first via the FS.GG SDD lifecycle. Authored artifacts live under `work/<id>/` (charter →
spec → clarify → checklist → plan → tasks → evidence); the committed public surface baselines live
under `docs/api-surface/`; workflow feedback reports under `docs/reports/`.

```sh
dotnet build FS.GG.Audio.slnx -c Debug
dotnet test  FS.GG.Audio.slnx -c Debug
```

## Releases

`release.yml` is tag-triggered (`v*`): it verifies (locked restore + headless build/test), publishes
the coherent package set to the org GitHub Packages feed and nuget.org (OIDC Trusted Publishing),
and attaches the `.fsi` API surface, the SDD lifecycle artifacts, and the sample app to the GitHub
Release. All four packages share one version (`<FsGgAudioVersion>` in `Directory.Packages.props`).

## License

MIT — see [LICENSE](LICENSE).

The FS.GG.Audio *source* is MIT. Host, Engine and Elmish additionally **redistribute** OpenAL Soft
(LGPL-2.0-or-later) as a replaceable native library — see
**[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)**. Core does not.
