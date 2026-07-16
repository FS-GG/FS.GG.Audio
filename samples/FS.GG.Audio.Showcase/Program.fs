module FS.GG.Audio.Showcase.Program

// Every feature of FS.GG.Audio, driven end to end, narrated as it goes.
//
// It SYNTHESIZES its own assets — a few sine pings and a two-note loop, built as real PCM WAVs in
// memory. So there are no binary files to ship, and nothing here is faked: those bytes go through the
// real `Wav.tryParse`, the real `BufferData` upload, and the real source pool. On a machine with an
// audio device you hear it; on one without, `OpenAlBackend.create` degrades and the narration is the
// whole show (FR-004). Either way the transcript is identical, because the mixer is advanced by a
// FIXED dt and only the sleeping is real — the model does not know what time it is.
//
//   dotnet run --project samples/FS.GG.Audio.Showcase           # real time, audible
//   dotnet run --project samples/FS.GG.Audio.Showcase -- --fast # no sleeping, same transcript

open System
open System.Threading
open FS.GG.Audio.Core
open FS.GG.Audio.Host
open FS.GG.Audio.Engine

module CoreAudio = FS.GG.Audio.Core.Audio
module HostAudio = FS.GG.Audio.Host.Audio
module ElmishAudio = FS.GG.Audio.Elmish.Audio

// ── synthesized assets ────────────────────────────────────────────────────────────────────────
// Mono 16-bit PCM, which is what OpenAL spatializes (a stereo buffer plays centred whatever position
// it is given), and what `bufferFormat` maps to Mono16.

let private rate = 44100

let private wav (seconds: float) (sample: float -> float) : byte[] =
    let frames = int (float rate * seconds)
    let bytes = frames * 2
    use ms = new IO.MemoryStream()
    use w = new IO.BinaryWriter(ms)
    w.Write(Text.Encoding.ASCII.GetBytes "RIFF")
    w.Write(36 + bytes)
    w.Write(Text.Encoding.ASCII.GetBytes "WAVE")
    w.Write(Text.Encoding.ASCII.GetBytes "fmt ")
    w.Write(16)
    w.Write(1s) // PCM — the codec check (#112) refuses anything else
    w.Write(1s) // mono
    w.Write(rate)
    w.Write(rate * 2)
    w.Write(2s)
    w.Write(16s)
    w.Write(Text.Encoding.ASCII.GetBytes "data")
    w.Write(bytes)
    for i in 0 .. frames - 1 do
        let v = sample (float i / float rate)
        w.Write(int16 (32000.0 * (max -1.0 (min 1.0 v))))
    w.Flush()
    ms.ToArray()

/// A plucked sine: pitch, and an exponential decay so overlapping voices stay distinguishable.
let private ping hz seconds decay =
    wav seconds (fun t -> sin (2.0 * Math.PI * hz * t) * exp (-t * decay))

/// Two notes a fifth apart, looping — something to duck under and cross-fade against.
let private loop hz seconds =
    wav seconds (fun t ->
        let a = sin (2.0 * Math.PI * hz * t)
        let b = sin (2.0 * Math.PI * hz * 1.5 * t)
        (a + b) * 0.4 * (0.7 + 0.3 * sin (2.0 * Math.PI * 0.5 * t)))

/// Deliberately NOT PCM: its fmt chunk claims IEEE-float. Used to show the codec refusal (#112).
let private floatWav () =
    let b = ping 440.0 0.2 6.0
    BitConverter.GetBytes 3s |> Array.iteri (fun i x -> b.[20 + i] <- x)
    b

let private sounds =
    dict
        [ "blip", ping 880.0 0.25 9.0
          "thud", ping 110.0 0.40 5.0
          "chime", ping 1320.0 0.35 7.0
          "bad-codec", floatWav ()
          // Not a RIFF file at all — the third distinct failure, and the third distinct fix.
          "not-a-wav", Text.Encoding.ASCII.GetBytes "these bytes were never a wav file, not even close" ]

let private tracks = dict [ "calm", loop 220.0 2.0; "tense", loop 277.0 2.0 ]

let private resolver: AssetResolver =
    { ResolveSound =
        fun (SoundId id) ->
            match sounds.TryGetValue id with
            | true, b -> Some b
            | _ -> None // "ghost" resolves to nothing — that is the point, see §8
      ResolveTrack =
        fun (TrackId id) ->
            match tracks.TryGetValue id with
            | true, b -> Some b
            | _ -> None }

// ── narration ─────────────────────────────────────────────────────────────────────────────────

let private rule () = printfn "%s" (String('─', 78))

let private section n title =
    printfn ""
    rule ()
    printfn " %d. %s" n title
    rule ()

let private say (s: string) = printfn "    %s" s

let private bar (label: string) (v: float) =
    let filled = int (Math.Round(v * 24.0))
    printfn "      %-8s [%s%s] %.2f" label (String('█', filled)) (String('·', 24 - filled)) v

[<EntryPoint>]
let main argv =
    let fast = argv |> Array.contains "--fast"
    let dt = 1.0 / 60.0

    printfn ""
    printfn "  FS.GG.Audio — showcase"
    printfn "  %s" (if fast then "--fast: no sleeping; the transcript is unchanged." else "real time — turn it up.")

    // ── 1. the pure vocabulary ────────────────────────────────────────────────────────────────
    section 1 "Core — a pure request vocabulary. A product's `update` emits values, never sound."
    let batch =
        [ CoreAudio.playSfx (SoundId "blip") 5.0 // out of range on purpose
          CoreAudio.playMusic (TrackId "calm") true
          CoreAudio.setBusVolume Sfx -2.0 // ditto
          CoreAudio.duck Music 0.6 250.0 ]
    say "A batch built with deliberately out-of-range volumes (5.0 and -2.0):"
    for e in (CoreAudio.interpret batch).Requested do
        say (sprintf "  %A" e)
    say ""
    say "Clamped into [0,1] on the way in — `interpret` is the record-only interpreter, and the"
    say "recorded evidence IS the proof for a headless test. No device involved."

    // ── 2. the backend seam ───────────────────────────────────────────────────────────────────
    section 2 "Host — which backend did we actually get? (#34)"
    let backend = OpenAlBackend.create resolver
    match Backend.kindOf backend with
    | BackendKind.DeviceBacked ->
        say "DeviceBacked — a real OpenAL device opened. You should HEAR the rest of this."
    | BackendKind.RecordOnly(Silence.DeviceUnavailable reason) ->
        say (sprintf "RecordOnly — no device opened, so `create` substituted the Null backend:")
        say (sprintf "  \"%s\"" (reason.Trim()))
        say "That is the deliberate degrade (FR-004): the game runs, silently, and says so."
    | BackendKind.RecordOnly Silence.Requested -> say "RecordOnly — record-only was asked for."
    | BackendKind.Unknown -> say "Unknown — a backend this library did not build."
    say ""
    say (sprintf "Backend.isDeviceBacked = %b — the predicate a CI suite branches on to skip loudly" (Backend.isDeviceBacked backend))
    say "rather than assert against a tape recorder."

    let engine = Engine.create backend
    let hold seconds =
        for _ in 1 .. int (seconds * 60.0) do
            Engine.step engine dt []
            if not fast then Thread.Sleep 16
    let fire effects = Engine.step engine dt effects
    let gains () =
        bar "Master" (engine.BusGain Master)
        bar "Music" (engine.BusGain Music)
        bar "Sfx" (engine.BusGain Sfx)

    // ── 3. buses ──────────────────────────────────────────────────────────────────────────────
    section 3 "Engine — five named buses, each with its own gain"
    fire [ CoreAudio.setBusVolume Music 0.0 ]
    fire [ CoreAudio.playMusic (TrackId "calm") true ]
    say "Music started at zero, so there is nothing to hear yet:"
    gains ()

    // ── 4. fades ──────────────────────────────────────────────────────────────────────────────
    section 4 "Engine — a timed linear fade (`Engine.fadeBus`)"
    say "Fading Music 0 → 1 over 2s. Envelopes advance only as the engine is stepped."
    Engine.fadeBus engine Music 1.0 2.0
    for _ in 1..4 do
        hold 0.5
        gains ()

    // ── 5. one-shots and the voice pool ───────────────────────────────────────────────────────
    section 5 "Engine — one-shots on the Sfx bus, mixed under Master"
    say "Three pings, each scaled by Sfx × Master before it reaches the device:"
    for id in [ "blip"; "chime"; "thud" ] do
        fire [ CoreAudio.playSfx (SoundId id) 0.9 ]
        for v in engine.LastVoices do
            say (sprintf "  %-6s request=%.2f → effective=%.2f  (bus %A)" id v.RequestGain v.EffectiveGain v.Bus)
        hold 0.45
    say ""
    say "Sfx down to 0.25 — the SAME request now realizes quieter:"
    fire [ CoreAudio.setBusVolume Sfx 0.25 ]
    fire [ CoreAudio.playSfx (SoundId "blip") 0.9 ]
    for v in engine.LastVoices do
        say (sprintf "  blip   request=%.2f → effective=%.2f" v.RequestGain v.EffectiveGain)
    hold 0.5
    fire [ CoreAudio.setBusVolume Sfx 1.0 ]

    // ── 6. ducking ────────────────────────────────────────────────────────────────────────────
    section 6 "Engine — side-chain ducking: pull the music down under a stinger"
    say "Duck Music by 0.7 over 1.2s, and fire a thud into the hole it makes."
    fire [ CoreAudio.duck Music 0.7 1200.0; CoreAudio.playSfx (SoundId "thud") 1.0 ]
    for _ in 1..4 do
        hold 0.3
        bar "Music" (engine.BusGain Music)
    say "Auto-restoring: the dip is a triangle, so it recovers without anyone cancelling it."

    // ── 7. 3D ─────────────────────────────────────────────────────────────────────────────────
    section 7 "Engine — 3D: pan follows the ANGLE to the source, and distance attenuates"
    Engine.setListener engine 0.0 0.0 0.0
    say "A chime swept around the listener at a fixed 5m. Pan is the sine of the azimuth,"
    say "so it is a pure direction — it does not vary with distance:"
    for deg in [ -90; -45; 0; 45; 90 ] do
        let a = float deg * Math.PI / 180.0
        fire [ CoreAudio.playSfx3D (SoundId "chime") (5.0 * sin a) 0.0 (-5.0 * cos a) 1.0 ]
        for v in engine.LastVoices do
            let ear = if v.Pan < -0.05 then "left" elif v.Pan > 0.05 then "right" else "ahead"
            say (sprintf "  %+4d°  pan=%+.2f (%-5s)  gain=%.2f  positional=%b" deg v.Pan ear v.EffectiveGain v.Positional)
        hold 0.4
    say ""
    say "Now the same bearing (45° right) at increasing distance — pan is unchanged, gain falls:"
    for d in [ 1.0; 3.0; 9.0 ] do
        fire [ CoreAudio.playSfx3D (SoundId "chime") (d * 0.707) 0.0 (-d * 0.707) 1.0 ]
        for v in engine.LastVoices do
            say (sprintf "  %4.0fm  pan=%+.2f  gain=%.2f" d v.Pan v.EffectiveGain)
        hold 0.4

    // ── 8. cross-fade ─────────────────────────────────────────────────────────────────────────
    section 8 "Engine — equal-power cross-fade between buses"
    say "Ambient takes over from Music over 2s at constant summed power."
    fire [ CoreAudio.setBusVolume Ambient 0.0 ]
    Engine.crossFade engine Music Ambient 2.0
    for _ in 1..4 do
        hold 0.5
        bar "Music" (engine.BusGain Music)
        bar "Ambient" (engine.BusGain Ambient)
        printfn ""

    // ── 9. asset diagnostics ──────────────────────────────────────────────────────────────────
    section 9 "Host — an asset that cannot play says WHY, once, naming the id (#28)"
    say "Three failures, three different fixes — so three different messages. Watch stderr:"
    say ""
    say "  a) an id the resolver does not know:"
    fire [ CoreAudio.playSfx (SoundId "ghost") 1.0 ]
    Console.Error.Flush()
    say "  b) a WAV whose codec is not PCM (its fmt chunk claims IEEE-float):"
    fire [ CoreAudio.playSfx (SoundId "bad-codec") 1.0 ]
    Console.Error.Flush()
    say "  c) bytes that were never a WAV at all:"
    fire [ CoreAudio.playSfx (SoundId "not-a-wav") 1.0 ]
    Console.Error.Flush()
    say ""
    say "Now play all three again — and watch nothing appear:"
    for id in [ "ghost"; "bad-codec"; "not-a-wav" ] do
        fire [ CoreAudio.playSfx (SoundId id) 1.0 ]
    Console.Error.Flush()
    say "Reported once per id, and that is load-bearing rather than polite: a failed resolve is"
    say "deliberately NOT cached, so this leg is re-entered on EVERY play of a cue the product"
    say "retriggers — a bare print here would emit a line a frame, and bury the message."

    // ── 10. the raw path ──────────────────────────────────────────────────────────────────────
    section 10 "Host — the raw path drops what only a mixer can realize, and says so (#27)"
    say "`Host.Audio.play` goes STRAIGHT at the backend: no mixer, no clock. SetBusVolume and Duck"
    say "are dropped and PlaySfx3D degrades. It is a legitimate fire-and-forget path — but a volume"
    say "slider wired to it does nothing, so it warns once per process:"
    say ""
    HostAudio.play backend [ CoreAudio.setBusVolume Music 0.5; CoreAudio.playSfx (SoundId "blip") 0.8 ]
    Console.Error.Flush()
    hold 0.4

    // ── 11. Elmish ────────────────────────────────────────────────────────────────────────────
    section 11 "Elmish — the same effects as a `Cmd`, for a product with an `update`"
    say "`Audio.Cmd.ofEngine engine dt effects` routes a batch through the mixer; `ofEffects` is the"
    say "raw path above. A Cmd is a description — nothing plays until the runtime executes it."
    let cmd: Elmish.Cmd<unit> =
        ElmishAudio.Cmd.ofEngine engine dt [ CoreAudio.playSfx (SoundId "chime") 0.8 ]
    say ""
    say (sprintf "  built a Cmd carrying %d effect(s); executing it now:" (List.length cmd))
    for sub in cmd do
        sub ignore
    for v in engine.LastVoices do
        say (sprintf "  chime  effective=%.2f — realized through the engine, so the mix applied" v.EffectiveGain)
    hold 0.5

    // ── 12. sinks ─────────────────────────────────────────────────────────────────────────────
    section 12 "Engine — sinks: the one-liner a host wires per frame"
    say "`Engine.createSinkOver engine` returns an `AudioEffect list -> unit` that steps the mixer."
    say "Build it ONCE: rebuilt per frame it would carry a fresh engine and no envelope would advance."
    let sink = Engine.createSinkWith (fun () -> dt) engine
    sink [ CoreAudio.playSfx (SoundId "blip") 0.7 ]
    for v in engine.LastVoices do
        say (sprintf "  blip   effective=%.2f — same mix, through the sink" v.EffectiveGain)
    hold 0.4

    // ── 13. device health ─────────────────────────────────────────────────────────────────────
    section 13 "Engine — is anything actually audible right now? (#33)"
    say (sprintf "  Device.IsPersistent(Realize) = %b" (engine.Device.IsPersistent DeviceDiagnostics.Realize))
    say (sprintf "  Device.ConsecutiveFaults(Realize) = %d" (engine.Device.ConsecutiveFaults DeviceDiagnostics.Realize))
    say ""
    say "A device that dies mid-game is reported once as a hiccup, and once more — as PERSISTENT —"
    say "when it has failed long enough that a hiccup is no longer a credible explanation. And"
    say "retracted, once, if it comes back."

    fire [ CoreAudio.stopMusic ]
    hold 0.2
    backend.Dispose()

    printfn ""
    rule ()
    printfn " Same input, same transcript, every run — the mixer is advanced by a fixed dt."
    printfn " %s"
        (if Backend.isDeviceBacked backend then
             "A real device played this."
         else
             "No device here: the narration was the whole show, and that is FR-004 working.")
    rule ()
    printfn ""
    0
