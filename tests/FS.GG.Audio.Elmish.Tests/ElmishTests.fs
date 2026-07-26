module FS.GG.Audio.Elmish.Tests.ElmishTests

open System.IO
open Expecto
open FS.GG.Audio.Core
open FS.GG.Audio.Host
open FS.GG.Audio.Engine
open FS.GG.Audio.Elmish

// Disambiguate the three `Audio` modules in scope (Core vocabulary, Host drive, this Cmd surface).
module CoreAudio = FS.GG.Audio.Core.Audio
module AudioCmd = FS.GG.Audio.Elmish.Audio.Cmd

let private acc = Accuracy.high

// Walk up from the test binary to the repo root (the directory holding the solution file).
let private repoRoot () =
    let mutable dir = DirectoryInfo(System.AppContext.BaseDirectory)
    while dir <> null && not (File.Exists(Path.Combine(dir.FullName, "FS.GG.Audio.slnx"))) do
        dir <- dir.Parent
    if isNull dir then failwith "repo root (FS.GG.Audio.slnx) not found" else dir.FullName

type private RecordingBackend() =
    let calls = ResizeArray<AudioEffect>()
    member _.Calls = List.ofSeq calls
    interface IAudioBackend with
        member _.Play(e) = calls.Add e
        member _.Dispose() = ()

// A recording backend that implements the optional mixing seam, so an engine over it spatializes
// and drives continuous bus gains — used to prove `ofEngine` carries 3D through the Cmd path.
type private RecordingMixingBackend() =
    let playAts = ResizeArray<SoundId * float * float>()
    member _.PlayAts = List.ofSeq playAts
    interface IMixingBackend with
        member _.SetBusGain(_, _) = ()
        member _.SetListener(_, _, _) = ()
        member _.PlayAt(sound, gain, pan) = playAts.Add(sound, gain, pan)
    interface IAudioBackend with
        member _.Play(_) = ()
        member _.Dispose() = ()

// Execute an Elmish command's effects with a recording dispatch; return the dispatched messages.
let private run (cmd: Elmish.Cmd<int>) : int list =
    let dispatched = ResizeArray<int>()
    for effect in cmd do
        effect (fun m -> dispatched.Add m)
    List.ofSeq dispatched

[<Tests>]
let tests =
    testList "FS.GG.Audio.Elmish" [

        test "Audio.Cmd.ofEffects plays effects in order and dispatches nothing (FR-001, FR-003)" {
            let fake = new RecordingBackend()
            let effects =
                [ CoreAudio.playSfx (SoundId "a") 0.5
                  CoreAudio.playMusic (TrackId "m") true
                  CoreAudio.stopMusic ]
            let cmd: Elmish.Cmd<int> = AudioCmd.ofEffects (fake :> IAudioBackend) effects
            let dispatched = run cmd
            Expect.equal fake.Calls effects "each effect played through the backend, in order"
            Expect.isEmpty dispatched "fire-and-forget: no message dispatched"
        }

        test "single-effect constructors play exactly the matching Core.Audio effect (FR-002)" {
            let fake = new RecordingBackend()
            let b = fake :> IAudioBackend
            run (AudioCmd.playSfx b (SoundId "x") 2.0) |> ignore
            run (AudioCmd.playMusic b (TrackId "m") true) |> ignore
            run (AudioCmd.stopMusic b) |> ignore
            run (AudioCmd.setMasterVolume b 0.3) |> ignore
            Expect.equal
                fake.Calls
                [ CoreAudio.playSfx (SoundId "x") 2.0
                  CoreAudio.playMusic (TrackId "m") true
                  CoreAudio.stopMusic
                  CoreAudio.setMasterVolume 0.3 ]
                "each Cmd constructor plays its matching Core effect (volumes clamped by Core)"
        }

        test "an empty batch plays nothing and dispatches nothing (FR-003)" {
            let fake = new RecordingBackend()
            let dispatched = run (AudioCmd.ofEffects (fake :> IAudioBackend) [])
            Expect.isEmpty fake.Calls "no effects played"
            Expect.isEmpty dispatched "no message dispatched"
        }

        test "the project declares exactly Elmish + Core/Host/Engine + FSharp.Core, never FS.GG.UI/SkiaSharp (FR-004)" {
            // Assembly.GetReferencedAssemblies is NOT the package dependency contract: the Release
            // optimizer can erase a metadata reference whose type aliases compile away, even while
            // the project and resulting nuspec correctly depend on Elmish. Read the authored project
            // instead. The behavioral tests above separately prove the Cmd surface executes.
            let project =
                Path.Combine(repoRoot (), "src", "FS.GG.Audio.Elmish", "FS.GG.Audio.Elmish.fsproj")
                |> System.Xml.Linq.XDocument.Load
            let includes elementName =
                project.Descendants()
                |> Seq.filter (fun e -> e.Name.LocalName = elementName)
                |> Seq.choose (fun e ->
                    match e.Attribute(System.Xml.Linq.XName.Get "Include") with
                    | null -> None
                    | attr -> Some attr.Value)
                |> Set.ofSeq
            let packages = includes "PackageReference"
            let projects =
                includes "ProjectReference"
                // Project files spell relative paths with Windows separators; normalize before the
                // test runs on Linux, where backslash is an ordinary filename character.
                |> Set.map (fun path -> path.Replace('\\', '/') |> Path.GetFileNameWithoutExtension)

            Expect.equal packages (set [ "Elmish"; "FSharp.Core" ]) "exact external package contract"
            Expect.equal
                projects
                (set [ "FS.GG.Audio.Core"; "FS.GG.Audio.Host"; "FS.GG.Audio.Engine" ])
                "exact sibling project contract"
        }

        test "ofEngine routes the batch through Engine.step so mixing applies (005 FR-001, FR-002)" {
            // A plain (non-mixing) recording backend: the engine degrades to Play, FOLDING the bus
            // gain into the one-shot volume. So a Sfx bus at 0.5 turns request 1.0 into 0.5 — proof
            // that the effects went through the Engine, not straight at the backend.
            let backend = new RecordingBackend()
            let engine = Engine.create (backend :> IAudioBackend)
            let cmd: Elmish.Cmd<int> =
                AudioCmd.ofEngine engine 0.0 [ CoreAudio.setBusVolume Sfx 0.5
                                               CoreAudio.playSfx (SoundId "s") 1.0 ]
            let dispatched = run cmd
            Expect.floatClose acc (engine.BusGain Sfx) 0.5 "SetBusVolume consumed by the engine"
            let v = List.exactlyOne engine.LastVoices
            Expect.floatClose acc v.EffectiveGain 0.5 "effective gain = request x bus x master = 1.0 x 0.5 x 1.0"
            Expect.equal
                backend.Calls
                [ PlaySfx(SoundId "s", 0.5) ]
                "the engine realized one gain-folded Play; SetBusVolume did not reach the raw backend"
            Expect.isEmpty dispatched "fire-and-forget: no message dispatched (005 FR-003)"
        }

        // --- #29: the Elmish raw path drops what only the Engine can realize — say so, once. ---
        //
        // Sequenced: it swaps the process-wide stderr writer, so it must not race a parallel test.
        //
        // This is the ONLY test in this suite that raw-drives an effect requiring the engine, and it
        // has to stay that way. The warn-once latch behind the diagnostic lives in `Host.Audio` and is
        // per-PROCESS (that is the point — a dragged slider would otherwise emit a line a frame), so a
        // second test dropping a SetBusVolume through `ofEffects` would trip it first and this one
        // would observe nothing, ordering-dependently. Hence the raw-vs-engine contrast and the
        // diagnostic are asserted together here rather than split across two tests: they are one event
        // seen from two sides, and splitting them would make the suite flaky rather than thorough.
        testSequenced (
            test "ofEffects vs ofEngine on the same batch: raw drops — and now SAYS so, once — vs mixed (#29, 005 FR-002)" {
                let batch = [ CoreAudio.setBusVolume Sfx 0.5; CoreAudio.playSfx (SoundId "s") 1.0 ]
                let raw = new RecordingBackend()
                let mixed = new RecordingBackend()
                let engine = Engine.create (mixed :> IAudioBackend)
                let diagnostics (s: string) =
                    s.Split([| "reached IAudioBackend directly" |], System.StringSplitOptions.None).Length - 1

                let original = System.Console.Error
                use captured = new System.IO.StringWriter()
                System.Console.SetError captured
                let mutable afterEngine = -1
                try
                    // ENGINE PATH FIRST, and the order is the assertion. The warn-once latch is a
                    // one-way process-wide flag, so once the raw drop below has tripped it NOTHING can
                    // emit again — an `ofEngine` that wrongly routed through `Host.Audio.play` would
                    // still leave exactly one line in the buffer and a "total == 1" check would pass
                    // green over a broken engine path. Driving ofEngine while the latch is still OPEN
                    // is the only window in which a false positive is observable at all. (A vacuous
                    // assertion in a fix about unchecked guarantees would be a poor joke.)
                    run (AudioCmd.ofEngine engine 0.0 batch) |> ignore
                    afterEngine <- diagnostics (captured.ToString())
                    // Raw path, driven TWICE: every effect lands at the backend verbatim — SetBusVolume
                    // is a backend no-op Play and the sfx stays at its raw 1.0 request gain (the #21
                    // footgun). Twice, so warn-ONCE is observable rather than assumed.
                    run (AudioCmd.ofEffects (raw :> IAudioBackend) batch) |> ignore
                    run (AudioCmd.ofEffects (raw :> IAudioBackend) batch) |> ignore
                finally
                    System.Console.SetError original

                let text = captured.ToString()

                // The Engine CONSUMES SetBusVolume and never forwards it to IAudioBackend.Play, so the
                // path that is already correct must never be nagged. A false positive here would train
                // the reader to ignore the line on the path where it is true.
                Expect.equal afterEngine 0 "ofEngine emitted NO diagnostic — checked while the latch was still open"

                // Playback is untouched on BOTH paths — delegating to `Host.Audio.play` changed the
                // silence, not the sound (005 FR-002, and Host's own FR-006 dispatch-order guarantee,
                // which this path now inherits instead of re-implementing).
                Expect.equal
                    raw.Calls
                    (batch @ batch)
                    "ofEffects still plays every effect straight at the backend, unmixed, in dispatch order"
                Expect.equal
                    mixed.Calls
                    [ PlaySfx(SoundId "s", 0.5) ]
                    "ofEngine mixes: bus gain folded in, SetBusVolume consumed"

                // The drop is no longer silent here. #27 made only the HOST path loud; this path kept
                // its own copy of the drive loop and so kept the silence with it (#29).
                Expect.stringContains text "SetBusVolume" "the diagnostic names the effect the raw path drops"
                Expect.stringContains
                    text
                    "Audio.Cmd.ofEngine"
                    "the diagnostic names the destination an ELMISH caller reaches for — pointing one at Engine.createSink names a function it has no reason to call"
                // ...and it is not per-frame spam: the raw batch was played twice, and only the first
                // one spoke.
                Expect.equal (diagnostics text) 1 "warned exactly once, though the raw batch was played twice"
            })

        test "ofEngine carries 3D positioning through the Cmd path on a mixing backend (005 FR-002)" {
            let backend = new RecordingMixingBackend()
            let engine = Engine.create (backend :> IAudioBackend)
            Engine.setListener engine 0.0 0.0 0.0
            run (AudioCmd.ofEngine engine 0.0 [ CoreAudio.playSfx3D (SoundId "s") 3.0 0.0 0.0 1.0 ]) |> ignore
            let v = List.exactlyOne engine.LastVoices
            Expect.isTrue v.Positional "the 3D voice stays positional — ofEffects would have degraded it"
            Expect.floatClose acc v.EffectiveGain (1.0 / 3.0) "inverse-distance attenuation at d=3"
            let _, gain, pan = List.exactlyOne backend.PlayAts
            Expect.floatClose acc pan 1.0 "hard-right emitter pans to +1 through the mixing seam"
            Expect.floatClose acc gain (1.0 / 3.0) "the mixing backend receives the attenuated gain"
        }

        test "commands run headless against a recording backend with a no-op dispatch, no device (FR-005)" {
            let fake = new RecordingBackend()
            let cmd = AudioCmd.playMusic (fake :> IAudioBackend) (TrackId "bgm") true
            // A genuine no-op dispatch, and no audio device is opened anywhere.
            for effect in cmd do
                effect ignore
            Expect.equal
                fake.Calls
                [ CoreAudio.playMusic (TrackId "bgm") true ]
                "recorded the requested effect with no device and a no-op dispatch"
        }

        test "the committed .fsi surface baseline matches the source signature, no drift (FR-006)" {
            let root = repoRoot ()
            let source = Path.Combine(root, "src", "FS.GG.Audio.Elmish", "Elmish.fsi")
            let baseline = Path.Combine(root, "docs", "api-surface", "FS.GG.Audio.Elmish", "Elmish.fsi")
            Expect.isTrue (File.Exists baseline) "committed api-surface baseline exists"
            Expect.equal
                (File.ReadAllText baseline)
                (File.ReadAllText source)
                "public surface matches the committed .fsi baseline"
        }
    ]
