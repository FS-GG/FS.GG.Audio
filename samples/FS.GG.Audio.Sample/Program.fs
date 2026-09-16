module FS.GG.Audio.Sample.Program

// A headless, fully deterministic demo of the FS.GG.Audio stack: the pure Core vocabulary, the
// Host backend seam, and the Engine's named buses / fades / ducking / 3D. It opens no audio
// device — a narrating backend prints what the engine realizes each frame, so the same run
// produces the same transcript on any machine (the whole point of the record path).

open FS.GG.Audio.Core
open FS.GG.Audio.Host
open FS.GG.Audio.Engine

module CoreAudio = FS.GG.Audio.Core.Audio

// A backend that narrates what the engine asks it to play. Implements the optional IMixingBackend
// so 3D voices spatialize (a plain IAudioBackend would degrade them to non-positional). It reports
// the same listener-relative position `OpenAlBackend` hands to the device, via the same pure
// mapping — so what this transcript shows is what you would hear.
type private ConsoleBackend() =
    interface IMixingBackend with
        member _.SetBusGain(_, _) = ()
        member _.SetListener(_, _, _) = ()

        member _.PlayAt(sound, gain, pan) =
            let (SoundId s) = sound
            let (px, _, pz) = Spatial.panToPosition pan

            let ear =
                if pan < -0.01 then "LEFT "
                elif pan > 0.01 then "RIGHT"
                else "front"

            printfn
                "        > play %-5s gain=%.3f pan=%+.2f -> %s ear, source at (x=%+.2f, z=%+.2f)"
                s
                gain
                pan
                ear
                px
                pz

    interface IAudioBackend with
        member _.Play(effect) =
            match effect with
            | PlaySfx(SoundId s, v) -> printfn "        > play %-5s gain=%.3f" s v
            | PlayMusic(TrackId t, loop) -> printfn "        > music \"%s\" start (loop=%b)" t loop
            | StopMusic -> printfn "        > music stop"
            | _ -> ()

        member _.Dispose() = ()

[<EntryPoint>]
let main _ =
    printfn "FS.GG.Audio — engine demo (headless, deterministic; no device opened)\n"
    let engine = Engine.create (new ConsoleBackend() :> IAudioBackend)
    Engine.setListener engine 0.0 0.0 0.0

    let frame label dt effects =
        Engine.step engine dt effects
        printfn "  %s" label

        printfn
            "     buses  Master=%.2f  Music=%.2f  Sfx=%.2f   |  music voice=%.2f"
            (engine.BusGain Master)
            (engine.BusGain Music)
            (engine.BusGain Sfx)
            engine.MusicGain

    // Start music silent, THEN install a 1s fade-in (a fade must be installed after the initial
    // SetBusVolume, since setting a bus volume cancels its active fade).
    frame "[t=0.0s] start bgm silent" 0.0 [ CoreAudio.setBusVolume Music 0.0; CoreAudio.playMusic (TrackId "bgm") true ]
    Engine.fadeBus engine Music 1.0 1.0
    frame "[t=0.5s] fade half-way" 0.5 []
    frame "[t=1.0s] fade complete" 0.5 []

    // Fire the same sound from the left, the front, and the right. Distance is equal, so only the
    // pan differs: left and right land on opposite ears, and the centred one stays in front.
    frame
        "[t=1.0s] the same 'boom' from the left, ahead, and the right"
        0.0
        [
            CoreAudio.playSfx3D (SoundId "boom") -3.0 0.0 0.0 1.0
            CoreAudio.playSfx3D (SoundId "boom") 0.0 0.0 -3.0 1.0
            CoreAudio.playSfx3D (SoundId "boom") 3.0 0.0 0.0 1.0
        ]

    // Fire a positional sfx to the right, and duck the music under it for 1s.
    frame
        "[t=1.0s] hard-right explosion + 1s duck on Music"
        0.0
        [
            CoreAudio.playSfx3D (SoundId "boom") 3.0 0.0 0.0 1.0
            CoreAudio.duck Music 0.6 1000.0
        ]

    frame "[t=1.5s] duck at its deepest (music pushed down under the stinger)" 0.5 []
    frame "[t=2.0s] duck released, music restored" 0.5 []

    printfn "\nDone — same input, same transcript, every run."
    0
