module FS.GG.Audio.Engine.Tests.EngineTests

open System.IO
open Expecto
open FS.GG.Audio.Core
open FS.GG.Audio.Host
open FS.GG.Audio.Engine

module CoreAudio = FS.GG.Audio.Core.Audio

// A recording backend that implements the optional mixing seam (so the engine spatializes and
// drives continuous bus gains). Captures every realized call for headless assertions.
type private RecordingMixingBackend() =
    let plays = ResizeArray<AudioEffect>()
    let playAts = ResizeArray<SoundId * float * float>()
    let busGains = ResizeArray<Bus * float>()
    let listeners = ResizeArray<float * float * float>()
    member _.Plays = List.ofSeq plays
    member _.PlayAts = List.ofSeq playAts
    member _.BusGains = List.ofSeq busGains
    member _.Listeners = List.ofSeq listeners
    interface IMixingBackend with
        member _.SetBusGain(bus, gain) = busGains.Add(bus, gain)
        member _.SetListener(x, y, z) = listeners.Add(x, y, z)
        member _.PlayAt(sound, gain, pan) = playAts.Add(sound, gain, pan)
    interface IAudioBackend with
        member _.Play(e) = plays.Add e
        member _.Dispose() = ()

// A backend that throws on every call — the engine must swallow it (FR-008). Kept throwing forever,
// it is also a device that has been yanked: the persistent fault of #33.
type private ThrowingBackend() =
    interface IAudioBackend with
        member _.Play(_) = failwith "device on fire"
        member _.Dispose() = ()

// A backend that throws on its first `failures` plays and then works — the transient hiccup the
// degrade exists for (#33). It must never be reported as a device that has died.
type private FlakyBackend(failures: int) =
    let mutable remaining = failures
    let plays = ResizeArray<AudioEffect>()
    member _.Plays = List.ofSeq plays
    interface IAudioBackend with
        member _.Play(e) =
            if remaining > 0 then
                remaining <- remaining - 1
                failwith "device hiccup"
            else
                plays.Add e
        member _.Dispose() = ()

let private mixing () = new RecordingMixingBackend()
let private acc = Accuracy.high

// Walk up from the test binary to the repo root (the directory holding the solution file).
let private repoRoot () =
    let mutable dir = DirectoryInfo(System.AppContext.BaseDirectory)
    while dir <> null && not (File.Exists(Path.Combine(dir.FullName, "FS.GG.Audio.slnx"))) do
        dir <- dir.Parent
    if isNull dir then failwith "repo root (FS.GG.Audio.slnx) not found" else dir.FullName

[<Tests>]
let tests =
    testList "FS.GG.Audio.Engine" [

        test "buses: effective gain = request x bus x master, each clamped (FR-001)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.step engine 0.0
                [ CoreAudio.setBusVolume Sfx 0.5
                  CoreAudio.setMasterVolume 0.5
                  CoreAudio.playSfx (SoundId "a") 0.8 ]
            Expect.floatClose acc (engine.BusGain Sfx) 0.5 "Sfx bus gain set"
            Expect.floatClose acc (engine.BusGain Master) 0.5 "Master bus gain set"
            let v = List.exactlyOne engine.LastVoices
            Expect.floatClose acc v.EffectiveGain (0.8 * 0.5 * 0.5) "effective = request x bus x master"
        }

        test "routing: sfx on Sfx bus, music on the single Music voice, StopMusic clears it (FR-002)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.step engine 0.0 [ CoreAudio.playSfx (SoundId "s") 1.0 ]
            Expect.equal (List.exactlyOne engine.LastVoices).Bus Sfx "sfx routes to the Sfx bus"
            Engine.step engine 0.0 [ CoreAudio.playMusic (TrackId "m") true ]
            Expect.floatClose acc engine.MusicGain 1.0 "music voice present at unity"
            Engine.step engine 0.0 [ CoreAudio.stopMusic ]
            Expect.floatClose acc engine.MusicGain 0.0 "StopMusic clears the music voice"
            Expect.contains backend.Plays (PlayMusic(TrackId "m", true)) "music start realized through the backend"
        }

        test "fadeBus reaches target exactly when deltas sum to the duration (FR-003)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.fadeBus engine Music 0.0 1.0            // fade Music 1.0 -> 0.0 over 1s (linear)
            Engine.step engine 0.5 []
            Expect.floatClose acc (engine.BusGain Music) 0.5 "half-way through a linear fade"
            Engine.step engine 0.5 []
            Expect.floatClose acc (engine.BusGain Music) 0.0 "target reached exactly at the duration"
        }

        test "crossFade is equal-power: constant summed power across the window (FR-003)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.crossFade engine Music Sfx 1.0
            Engine.step engine 0.5 []
            let m, s = engine.BusGain Music, engine.BusGain Sfx
            Expect.floatClose acc (m * m + s * s) 1.0 "equal-power: gains^2 sum to 1 mid-fade"
            Engine.step engine 0.5 []
            Expect.floatClose acc (engine.BusGain Music) 0.0 "from-bus ends silent"
            Expect.floatClose acc (engine.BusGain Sfx) 1.0 "to-bus ends at unity"
        }

        test "Duck attenuates by amount over the attack then auto-restores (FR-004)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.step engine 0.0 [ CoreAudio.duck Music 0.5 1000.0 ]   // 0.5 duck over 1000ms
            Engine.step engine 0.5 []
            Expect.floatClose acc (engine.BusGain Music) 0.5 "maximally ducked at the midpoint (1 - amount)"
            Engine.step engine 0.5 []
            Expect.floatClose acc (engine.BusGain Music) 1.0 "restored after the attack window"
        }

        test "3D: inverse-distance attenuation and x-pan on a mixing backend (FR-005)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.setListener engine 0.0 0.0 0.0
            Engine.step engine 0.0 [ CoreAudio.playSfx3D (SoundId "s") 3.0 0.0 0.0 1.0 ]
            let v = List.exactlyOne engine.LastVoices
            Expect.isTrue v.Positional "3D voice is positional on a mixing backend"
            Expect.floatClose acc v.EffectiveGain (1.0 / 3.0) "att = ref/(ref+rolloff*(d-ref)) = 1/3 at d=3"
            // Exactly on the +x axis, so the azimuth is 90 degrees and sin is 1 — hard right without
            // any clamping being involved. This assertion passed under the old `dx / RefDistance` law
            // too, which is precisely why that law survived: every pan test in this suite put the
            // source on an axis, the one geometry where both formulas agree.
            Expect.floatClose acc v.Pan 1.0 "a source due right of the listener is panned hard right"
        }

        // The geometry the suite never tried, and the only one that tells the two laws apart.
        test "3D: pan follows the ANGLE to the source, not its lateral offset (FR-005)" {
            let engine = Engine.create (mixing () :> IAudioBackend)
            Engine.setListener engine 0.0 0.0 0.0
            let panAt x z =
                Engine.step engine 0.0 [ CoreAudio.playSfx3D (SoundId "s") x 0.0 z 1.0 ]
                (List.exactlyOne engine.LastVoices).Pan

            // 2m right but 50m ahead: ~2 degrees off centre. The old law read the 2m of lateral
            // offset, divided by RefDistance (1.0), clamped, and panned this HARD RIGHT — as
            // aggressively as a source beside the listener's head.
            let nearlyAhead = panAt 2.0 -50.0
            Expect.isLessThan (abs nearlyAhead) 0.05 "a source 2 degrees off centre is very nearly centred"

            // Same lateral offset, no depth: genuinely due right, genuinely hard right. Same dx as
            // above — so under the old law these two were indistinguishable, and that was the bug.
            Expect.floatClose acc (panAt 2.0 0.0) 1.0 "the same 2m offset with no depth IS hard right"

            // 45 degrees ahead-right: sin 45 = 0.7071. A real continuum, not a switch.
            Expect.floatClose acc (panAt 5.0 -5.0) (sqrt 2.0 / 2.0) "45 degrees off centre pans to sin 45"
            Expect.floatClose acc (panAt -5.0 -5.0) (-(sqrt 2.0) / 2.0) "and mirrors to the left"

            // Pure direction: distance changes attenuation, never pan. The old law had |pan| growing
            // with lateral distance until it saturated.
            Expect.floatClose acc (panAt 1.0 -1.0) (panAt 100.0 -100.0) "same bearing, 100x the distance, same pan"
        }

        test "3D: pan is monotonic in azimuth and total on nonsense positions (FR-005)" {
            let engine = Engine.create (mixing () :> IAudioBackend)
            Engine.setListener engine 0.0 0.0 0.0
            let panAt x z =
                Engine.step engine 0.0 [ CoreAudio.playSfx3D (SoundId "s") x 0.0 z 1.0 ]
                (List.exactlyOne engine.LastVoices).Pan

            // Sweep from straight ahead round to due right at a fixed 10m: pan must rise STRICTLY.
            // Strict is the whole assertion. The old law saturated at +1 as soon as the lateral
            // offset passed RefDistance (1m) — i.e. from ~6 degrees onward — and then plateaued, so a
            // non-strict `<=` here would pass on the very shape this is meant to reject: a binary
            // left/right switch wearing a monotonic curve's clothes. Every distinct bearing must
            // produce a distinct pan.
            let bearings = [ 0.0 .. 5.0 .. 90.0 ] |> List.map (fun deg -> deg * System.Math.PI / 180.0)
            let pans = bearings |> List.map (fun a -> panAt (10.0 * sin a) (-10.0 * cos a))
            for (a, b) in List.pairwise pans do
                Expect.isLessThan a b "pan rises strictly as the source swings toward the ear — no plateau"
            Expect.isGreaterThan (List.last pans - List.head pans) 0.9 "and it spans the range rather than saturating early"

            // Total on nonsense (Principle VI). `nan` fails BOTH comparisons in a naive clamp, so it
            // would come back as a nan Pan and break the [-1,1] the .fsi promises. Centred is the
            // defined answer, and the one Host.Spatial.panToPosition already gives.
            for bad in [ nan; infinity; -infinity ] do
                let p = panAt bad 0.0
                Expect.isFalse (System.Double.IsNaN p) (sprintf "pan is never nan (x=%f)" bad)
                Expect.isTrue (abs p <= 1.0) (sprintf "pan stays in [-1,1] (x=%f)" bad)
            let p = panAt nan nan
            Expect.equal p 0.0 "a wholly non-finite position is centred, not nan"
        }

        test "3D degrades to a non-positional voice without a mixing backend (FR-005, FR-008)" {
            let backend = NullBackend.create ()          // IAudioBackend only, no mixing seam
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.setListener engine 0.0 0.0 0.0
            Engine.step engine 0.0 [ CoreAudio.playSfx3D (SoundId "s") 9.0 0.0 0.0 1.0 ]
            let v = List.exactlyOne engine.LastVoices
            Expect.isFalse v.Positional "no mixing seam -> non-positional"
            Expect.floatClose acc v.EffectiveGain 1.0 "degraded voice plays at the bus-scaled gain (att = 1)"
        }

        test "Core threads the additive variants and clamps them (FR-006)" {
            let ev =
                CoreAudio.interpret
                    [ CoreAudio.playSfx3D (SoundId "s") 1.0 2.0 0.0 9.0   // volume 9.0 -> 1.0
                      CoreAudio.setBusVolume Sfx 2.0                       // level 2.0 -> 1.0
                      CoreAudio.duck Music 2.0 -5.0 ]                      // amount 2.0 -> 1.0, ms -5 -> 0
            Expect.equal
                ev.Requested
                [ PlaySfx3D(SoundId "s", 1.0, 2.0, 0.0, 1.0)
                  SetBusVolume(Sfx, 1.0)
                  Duck(Music, 1.0, 0.0) ]
                "additive cases recorded in order with clamped magnitudes"
            // The original four cases are unchanged (non-breaking).
            Expect.equal
                (CoreAudio.interpret [ CoreAudio.stopMusic ]).Requested
                [ StopMusic ]
                "original cases still record identically"
        }

        test "deterministic: identical inputs produce identical realized calls (FR-007)" {
            let run () =
                let backend = mixing ()
                let engine = Engine.create (backend :> IAudioBackend)
                Engine.setListener engine 1.0 0.0 0.0
                Engine.step engine 0.5
                    [ CoreAudio.setBusVolume Sfx 0.7
                      CoreAudio.playSfx3D (SoundId "s") 4.0 0.0 0.0 0.9
                      CoreAudio.playMusic (TrackId "m") true ]
                Engine.step engine 0.5 [ CoreAudio.duck Music 0.4 500.0 ]
                backend.PlayAts, backend.BusGains, backend.Plays, backend.Listeners
            Expect.equal (run ()) (run ()) "two independent runs realize identical calls"
        }

        test "a throwing backend is swallowed — step never throws (FR-008)" {
            let engine = Engine.create (new ThrowingBackend() :> IAudioBackend)
            Engine.step engine 0.016 [ CoreAudio.playSfx (SoundId "s") 1.0; CoreAudio.stopMusic ]
            Expect.isTrue true "step completed without an exception escaping into game code"
        }

        // --- #33: swallowing the throw is right; swallowing it SILENTLY, forever, is not. These
        // drive the real production failure leg — a backend throwing inside Engine.step's realize
        // pass — and assert on what reaches the diagnostic channel. ---

        test "a persistently throwing backend is named, once, instead of silence forever (#33)" {
            let lines = ResizeArray<string>()
            let device = DeviceDiagnostics.T(lines.Add, 3)
            let engine =
                Engine.createWithDiagnostics device Engine.defaultSpatial (new ThrowingBackend() :> IAudioBackend)
            let frame = [ CoreAudio.playSfx (SoundId "s") 1.0 ]

            // The first faulting frame degrades to silence AND says so. It does not yet claim the
            // device is dead: one throw really might be a hiccup.
            Engine.step engine 0.016 frame
            Expect.equal lines.Count 1 "the first device fault is named"
            Expect.stringContains lines.[0] "SILENCE" "the first line names the consequence"
            Expect.isFalse (device.IsPersistent DeviceDiagnostics.Realize) "one fault is not yet a dead device"

            // It keeps faulting. At the threshold the run is escalated — once.
            for _ in 1..2 do Engine.step engine 0.016 frame
            Expect.equal lines.Count 2 "the run is escalated to a persistent fault"
            Expect.stringContains lines.[1] "PERSISTENT" "the second line says the device is gone"
            Expect.stringContains lines.[1] "OpenAlBackend.create" "and names the only way back"
            Expect.isTrue (device.IsPersistent DeviceDiagnostics.Realize) "the leg is latched persistent"

            // The defect this closes: 200 more dropped frames used to be 200 frames of unexplained
            // quiet. They are still silent — but they are explained, and they do not spam.
            for _ in 1..200 do Engine.step engine 0.016 frame
            Expect.equal lines.Count 2 "warn-once: 200 further faulting frames emit nothing new"
        }

        test "a transient hiccup degrades to silence and is never called a dead device (#33, FR-008)" {
            let lines = ResizeArray<string>()
            let device = DeviceDiagnostics.T(lines.Add, 3)
            let backend = new FlakyBackend(1)
            let engine = Engine.createWithDiagnostics device Engine.defaultSpatial (backend :> IAudioBackend)
            let frame = [ CoreAudio.playSfx (SoundId "s") 1.0 ]

            for _ in 1..10 do Engine.step engine 0.016 frame

            Expect.equal lines.Count 1 "the hiccup is named once, and never escalated"
            Expect.isFalse (device.IsPersistent DeviceDiagnostics.Realize) "a device that recovered is not gone"
            Expect.equal (device.ConsecutiveFaults DeviceDiagnostics.Realize) 0 "the run reset when it recovered"
            Expect.equal backend.Plays.Length 9 "and the 9 frames after the hiccup actually played"
        }

        test "a healthy backend says nothing at all (#33)" {
            let lines = ResizeArray<string>()
            let device = DeviceDiagnostics.T(lines.Add, 3)
            let engine = Engine.createWithDiagnostics device Engine.defaultSpatial (mixing () :> IAudioBackend)
            for _ in 1..100 do Engine.step engine 0.016 [ CoreAudio.playSfx (SoundId "s") 1.0 ]
            Expect.equal lines.Count 0 "a working device is not a diagnostic"
        }

        test "a dead device is escalated even when most frames carry no audio (#33)" {
            // The regression this pins: a real game does NOT play a sound every frame. Against a plain
            // backend a silent frame makes no device call at all, so if such a frame counted as a
            // success it would end the fault run — and a permanently dead device would never build a
            // run, never escalate, and go straight back to being unexplained silence. A frame that
            // never spoke to the device must leave the run alone.
            let lines = ResizeArray<string>()
            let device = DeviceDiagnostics.T(lines.Add, 60)
            let engine =
                Engine.createWithDiagnostics device Engine.defaultSpatial (new ThrowingBackend() :> IAudioBackend)

            for i in 1..600 do
                let frame = if i % 3 = 0 then [ CoreAudio.playSfx (SoundId "step") 1.0 ] else []
                Engine.step engine 0.016 frame

            Expect.isTrue (device.IsPersistent DeviceDiagnostics.Realize) "the dead device IS escalated"
            Expect.equal lines.Count 2 "and named exactly twice: the first fault, then the persistent one"
            Expect.stringContains lines.[1] "PERSISTENT" "the escalation happened"
        }

        test "a device that comes back is not still reported dead (#33)" {
            // A headset reconnects, a driver restarts. `IsPersistent` is a live predicate, not a
            // latch — a product driving "audio is broken" UI off it must see the device recover — and
            // the persistent-fault line, now untrue, is retracted rather than left standing.
            let lines = ResizeArray<string>()
            let device = DeviceDiagnostics.T(lines.Add, 3)
            let backend = new FlakyBackend(10)
            let engine = Engine.createWithDiagnostics device Engine.defaultSpatial (backend :> IAudioBackend)
            let frame = [ CoreAudio.playSfx (SoundId "s") 1.0 ]

            for _ in 1..10 do Engine.step engine 0.016 frame
            Expect.isTrue (device.IsPersistent DeviceDiagnostics.Realize) "10 straight faults: it is gone"

            // It comes back.
            for _ in 1..30 do Engine.step engine 0.016 frame
            Expect.isFalse (device.IsPersistent DeviceDiagnostics.Realize) "a device that recovered is not dead"
            Expect.equal (device.ConsecutiveFaults DeviceDiagnostics.Realize) 0 "the run is over"
            Expect.equal lines.Count 3 "first fault, persistent, and the retraction — once each"
            Expect.stringContains lines.[2] "answering" "the retraction says the device came back"
            Expect.equal backend.Plays.Length 30 "and it is genuinely playing again"
        }

        // --- #27: the sink — a mixing `AudioEffect list -> unit`, the shape a host product wires. ---

        test "createSink mixes where the raw Host.Audio.play sink of the same type drops (#27)" {
            // The bug, stated as a test: the SAME batch through the two same-typed sinks. This is a
            // volume slider (SetBusVolume) followed by a sound.
            let batch = [ CoreAudio.setBusVolume Sfx 0.5; CoreAudio.playSfx (SoundId "s") 1.0 ]

            let raw = mixing ()
            Audio.play (raw :> IAudioBackend) batch
            Expect.equal
                raw.Plays
                [ SetBusVolume(Sfx, 0.5); PlaySfx(SoundId "s", 1.0) ]
                "raw: SetBusVolume reaches a backend that discards it, and the sfx plays at FULL gain — the slider did nothing"

            let mixed = mixing ()
            let sink = Engine.createSink (mixed :> IAudioBackend)
            sink batch
            Expect.isEmpty
                (mixed.Plays |> List.filter (fun e -> e = SetBusVolume(Sfx, 0.5)))
                "mixed: the engine consumes SetBusVolume rather than forwarding it to the backend"
            let (sound, gain, _) = List.exactlyOne mixed.PlayAts
            Expect.equal sound (SoundId "s") "the sfx is realized"
            Expect.floatClose acc gain 0.5 "mixed: the bus gain is folded into the voice — the slider WORKS"
        }

        test "a sink advances one long-lived engine across frames (#27)" {
            // The footgun the `create` naming guards against: state must persist across calls, so a
            // bus set on one frame still attenuates a sound played on a later one.
            let backend = mixing ()
            let sink = Engine.createSink (backend :> IAudioBackend)
            sink [ CoreAudio.setBusVolume Sfx 0.25 ]
            sink [ CoreAudio.playSfx (SoundId "s") 1.0 ]
            let (_, gain, _) = List.exactlyOne backend.PlayAts
            Expect.floatClose acc gain 0.25 "the bus gain set on an earlier frame still applies"
        }

        test "createSinkOver keeps the engine reachable, so fades still drive the sink (#27)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            let sink = Engine.createSinkWith (fun () -> 0.5) engine   // deterministic: 0.5s per call
            // fadeBus is an engine call, not an effect — createSinkOver/With is what keeps it usable.
            Engine.fadeBus engine Music 0.0 1.0
            sink []
            Expect.floatClose acc (engine.BusGain Music) 0.5 "half-way through a 1s fade after one 0.5s frame"
            sink []
            Expect.floatClose acc (engine.BusGain Music) 0.0 "the fade completes as the sink is driven"
        }

        test "createSinkWith advances the engine by the dt it is given, wall-clock-free (#27)" {
            // Envelopes must advance and RECOVER on the injected clock alone — no Stopwatch, no sleep.
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            let mutable dt = 0.0
            let sink = Engine.createSinkWith (fun () -> dt) engine

            sink [ CoreAudio.duck Music 0.5 1000.0 ]   // a 1s duck, 50% deep
            Expect.floatClose acc (engine.BusGain Music) 1.0 "the duck starts at unity"
            dt <- 0.5
            sink []
            Expect.floatClose acc (engine.BusGain Music) 0.5 "at the midpoint the duck is at its deepest"
            dt <- 0.5
            sink []
            Expect.floatClose acc (engine.BusGain Music) 1.0 "the duck auto-restores once elapsed"
        }

        test "a sink keeps a 3D voice positional, where the raw path degrades it (#27)" {
            let backend = mixing ()
            let engine = Engine.create (backend :> IAudioBackend)
            let sink = Engine.createSinkWith (fun () -> 0.0) engine
            Engine.setListener engine 0.0 0.0 0.0
            // 3m out on +x — beyond RefDistance (1m), where the inverse-distance model is a no-op —
            // so this asserts real attenuation rather than the reference distance's unity gain.
            sink [ CoreAudio.playSfx3D (SoundId "s") 3.0 0.0 0.0 1.0 ]
            let v = List.exactlyOne engine.LastVoices
            Expect.isTrue v.Positional "the voice stays positional through the sink"
            Expect.floatClose acc v.Pan 1.0 "a source on the +x axis pans hard right"
            Expect.floatClose acc v.EffectiveGain (1.0 / 3.0) "inverse-distance attenuation at 3x the reference distance"
            // And it reached the device through the spatial seam, not as a flat non-positional play.
            Expect.equal (List.length backend.PlayAts) 1 "realized through IMixingBackend.PlayAt"
        }

        test "committed .fsi baselines match the sources, no drift (FR-009)" {
            let root = repoRoot ()
            let check pkg name =
                let src = Path.Combine(root, "src", pkg, name)
                let baseline = Path.Combine(root, "docs", "api-surface", pkg, name)
                Expect.isTrue (File.Exists baseline) (sprintf "%s baseline committed" pkg)
                Expect.equal (File.ReadAllText baseline) (File.ReadAllText src) (sprintf "%s surface matches baseline" pkg)
            check "FS.GG.Audio.Engine" "Engine.fsi"
            check "FS.GG.Audio.Core" "Audio.fsi"        // additive Core surface bump
            check "FS.GG.Audio.Host" "Host.fsi"         // the raw-path diagnostic surface (#27)
            // Elmish has had a committed baseline all along and was the one package this never
            // checked, so its baseline could drift with the gate green — the exact hole the other
            // three are here to close. A per-package list is what let one go missing; the packages are
            // not going to stop being four.
            check "FS.GG.Audio.Elmish" "Elmish.fsi"
        }
    ]
