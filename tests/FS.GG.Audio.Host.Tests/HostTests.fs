module FS.GG.Audio.Host.Tests.HostTests

open Expecto
open FS.GG.Audio.Core
open FS.GG.Audio.Host

// Disambiguate the two `Audio` modules (Core's vocabulary vs the host's imperative drive).
module CoreAudio = FS.GG.Audio.Core.Audio
module HostAudio = FS.GG.Audio.Host.Audio

// Recording fake backend (DEC-002 / FR-008): asserts the effect->Play mapping and order without a
// real device. This is how the host is exercised headless.
type private RecordingBackend() =
    let calls = ResizeArray<AudioEffect>()
    member _.Calls = List.ofSeq calls
    interface IAudioBackend with
        member _.Play(e) = calls.Add e
        member _.Dispose() = ()

// A tiny valid mono/16-bit PCM WAV (2 sample frames) for the WAV-parser test.
let private sampleWav () : byte[] =
    use ms = new System.IO.MemoryStream()
    use w = new System.IO.BinaryWriter(ms)
    let data = [| 0uy; 0uy; 1uy; 0uy |]
    let channels = 1s
    let rate = 44100
    let bits = 16s
    let byteRate = rate * int channels * int bits / 8
    let blockAlign = int16 (int channels * int bits / 8)
    w.Write(System.Text.Encoding.ASCII.GetBytes "RIFF")
    w.Write(36 + data.Length)
    w.Write(System.Text.Encoding.ASCII.GetBytes "WAVE")
    w.Write(System.Text.Encoding.ASCII.GetBytes "fmt ")
    w.Write(16)
    w.Write(1s) // PCM
    w.Write(channels)
    w.Write(rate)
    w.Write(byteRate)
    w.Write(blockAlign)
    w.Write(bits)
    w.Write(System.Text.Encoding.ASCII.GetBytes "data")
    w.Write(data.Length)
    w.Write(data)
    w.Flush()
    ms.ToArray()

// Counting fake of the device operations a VoicePool drives, so its reclaim/steal logic runs with
// no OpenAL device (#20). `Gen` hands out ascending handles; `IsStopped` consults a controllable
// set; `Stop`/`Delete` just record the handles they were called with.
type private VoicePoolFake() =
    let mutable nextId = 0u
    let genCalls = ResizeArray<uint>()
    let stopCalls = ResizeArray<uint>()
    let deleteCalls = ResizeArray<uint>()
    let stopped = System.Collections.Generic.HashSet<uint>()
    member _.GenCount = genCalls.Count
    member _.StopCalls = List.ofSeq stopCalls
    member _.DeleteCalls = List.ofSeq deleteCalls
    member _.MarkStopped(h: uint) = stopped.Add h |> ignore
    member _.Ops : VoicePool.Ops =
        { Gen = (fun () -> nextId <- nextId + 1u; genCalls.Add nextId; nextId)
          IsStopped = (fun h -> stopped.Contains h)
          Stop = (fun h -> stopCalls.Add h)
          Delete = (fun h -> deleteCalls.Add h) }

[<Tests>]
let tests =
    testList "FS.GG.Audio.Host" [

        test "Audio.play drives the backend once per effect in dispatch order (FR-006)" {
            let fake = new RecordingBackend()
            let effects =
                [ CoreAudio.playSfx (SoundId "a") 0.5
                  CoreAudio.playMusic (TrackId "m") true
                  CoreAudio.stopMusic ]
            HostAudio.play (fake :> IAudioBackend) effects
            Expect.equal fake.Calls effects "each effect drives Play once, oldest-first"
        }

        // --- #27: the raw path drops what only the Engine can realize — say so, once. ---

        test "requiresEngine is true for exactly the effects a raw backend cannot realize (#27)" {
            // The three the raw path loses: bus volume and duck are dropped, 3D degrades.
            Expect.isTrue (HostAudio.requiresEngine (CoreAudio.setBusVolume Music 0.25)) "SetBusVolume is dropped raw"
            Expect.isTrue (HostAudio.requiresEngine (CoreAudio.duck Music 0.5 200.0)) "Duck is dropped raw"
            Expect.isTrue (HostAudio.requiresEngine (CoreAudio.playSfx3D (SoundId "s") 1.0 0.0 0.0 1.0)) "PlaySfx3D degrades raw"
            // The four it realizes faithfully.
            Expect.isFalse (HostAudio.requiresEngine (CoreAudio.playSfx (SoundId "s") 0.5)) "PlaySfx plays raw"
            Expect.isFalse (HostAudio.requiresEngine (CoreAudio.playMusic (TrackId "m") true)) "PlayMusic plays raw"
            Expect.isFalse (HostAudio.requiresEngine CoreAudio.stopMusic) "StopMusic plays raw"
            Expect.isFalse (HostAudio.requiresEngine (CoreAudio.setMasterVolume 0.5)) "SetMasterVolume is honoured raw"
        }

        // Sequenced: it swaps the process-wide stderr writer, so it must not race a parallel test.
        // The warning latch is per-process and this is the ONLY test in this suite that plays an
        // effect requiring the engine, so the first trip of the latch is observed here.
        testSequenced (
            test "Audio.play names the silent drop once, and still plays the batch (#27)" {
                let fake = new RecordingBackend()
                let effects = [ CoreAudio.setBusVolume Music 0.25; CoreAudio.playSfx (SoundId "s") 1.0 ]

                let original = System.Console.Error
                use captured = new System.IO.StringWriter()
                System.Console.SetError captured
                try
                    HostAudio.play (fake :> IAudioBackend) effects
                    HostAudio.play (fake :> IAudioBackend) effects
                finally
                    System.Console.SetError original

                let text = captured.ToString()
                let occurrences =
                    text.Split([| "Audio.play drives the backend directly" |], System.StringSplitOptions.None).Length - 1

                // The drop is no longer silent...
                Expect.stringContains text "SetBusVolume" "the diagnostic names the effect that was dropped"
                Expect.stringContains text "Engine.createSink" "the diagnostic names the surface that realizes it"
                // ...but it is not per-frame spam either: a dragged slider emits one of these a frame.
                Expect.equal occurrences 1 "warned once per process, not once per batch"
                // And the diagnostic is all that changed: playback is untouched (FR-006/SB-008).
                Expect.equal fake.Calls (effects @ effects) "every effect still reaches the backend in dispatch order"
            })

        test "Null backend records evidence identical to Core.Audio.interpret (FR-002)" {
            let effects =
                [ CoreAudio.playSfx (SoundId "a") 9.0 // out of range -> normalized
                  CoreAudio.playMusic (TrackId "m") false
                  CoreAudio.setMasterVolume -1.0 ]
            let nb = NullBackend.create ()
            HostAudio.play (nb :> IAudioBackend) effects
            Expect.equal nb.Evidence (CoreAudio.interpret effects) "Null backend evidence == interpret"
        }

        test "Null backend opens no device and never throws (FR-002)" {
            let nb = NullBackend.create () :> IAudioBackend
            HostAudio.play nb [ CoreAudio.stopMusic; CoreAudio.playSfx (SoundId "x") 0.2 ]
            nb.Dispose()
            Expect.isTrue true "no exception thrown on a machine with no device"
        }

        test "OpenAlBackend.create degrades to a usable backend with no device (FR-004)" {
            // Headless/CI has no OpenAL device: create MUST NOT throw and MUST return a usable
            // IAudioBackend (the Null fallback); playing through it is a safe no-op.
            let resolver =
                { ResolveSound = (fun _ -> None)
                  ResolveTrack = (fun _ -> None) }
            let backend = OpenAlBackend.create resolver
            HostAudio.play backend [ CoreAudio.playSfx (SoundId "s") 0.5; CoreAudio.stopMusic ]
            backend.Dispose()
            Expect.isTrue true "create degraded without throwing and play was safe"
        }

        // The bundled OpenAL backend must be an IMixingBackend, because that is the only interface
        // FS.GG.Audio.Engine spatializes through: a backend that implements IAudioBackend alone
        // silently drops every pan. Vacuous on headless CI, where create degrades to Null (and Null
        // must stay a plain IAudioBackend, so the Engine's degrade path is what runs there).
        test "the OpenAL backend implements IMixingBackend when a device is present (#11)" {
            let resolver =
                { ResolveSound = (fun _ -> None)
                  ResolveTrack = (fun _ -> None) }
            let backend = OpenAlBackend.create resolver
            match backend with
            | :? NullBackend.T -> Expect.isFalse (backend :? IMixingBackend) "the Null fallback stays non-mixing"
            | _ -> Expect.isTrue (backend :? IMixingBackend) "a real device backend spatializes"
            backend.Dispose()
        }

        test "Spatial.panToPosition puts a centred voice in front of the listener (#11)" {
            let (x, y, z) = Spatial.panToPosition 0.0
            Expect.floatClose Accuracy.high x 0.0 "centred: no lateral offset"
            Expect.floatClose Accuracy.high y 0.0 "planar: no vertical offset"
            Expect.floatClose Accuracy.high z -1.0 "dead ahead, not inside the listener's head"
        }

        test "Spatial.panToPosition separates hard left from hard right (#11)" {
            let (lx, _, lz) = Spatial.panToPosition -1.0
            let (rx, _, rz) = Spatial.panToPosition 1.0
            Expect.floatClose Accuracy.high lx -1.0 "pan=-1 sits on the listener's left"
            Expect.floatClose Accuracy.high rx 1.0 "pan=+1 sits on the listener's right"
            Expect.floatClose Accuracy.high lz 0.0 "hard pan is fully lateral"
            Expect.floatClose Accuracy.high rz 0.0 "hard pan is fully lateral"
            Expect.isLessThan lx rx "left and right are distinguishable, not collapsed"
        }

        test "Spatial.panToPosition is unit-length for every pan, so gain is never re-attenuated (#11)" {
            // Engine already folded distance attenuation into the voice's gain. The source must land
            // on the unit circle: at OpenAL's default reference distance the distance model is a
            // no-op, so the gain we pass is the gain that plays.
            for i in -20 .. 20 do
                let pan = float i / 10.0 // spans [-2, 2]: past the ends too, to cover the clamp
                let (x, y, z) = Spatial.panToPosition pan
                Expect.floatClose Accuracy.high (sqrt (x * x + y * y + z * z)) 1.0 $"unit length at pan={pan}"
        }

        test "Spatial.panToPosition clamps out-of-range pan and centres nan (#11)" {
            Expect.equal (Spatial.panToPosition -5.0) (Spatial.panToPosition -1.0) "below -1 clamps to hard left"
            Expect.equal (Spatial.panToPosition 5.0) (Spatial.panToPosition 1.0) "above +1 clamps to hard right"
            // Total on bad input (Principle VI): nan is a defined centre, not a nan position that
            // would silently mute the source on the device.
            Expect.equal (Spatial.panToPosition nan) (Spatial.panToPosition 0.0) "nan -> centred"
        }

        test "Wav.tryParse reads a minimal PCM WAV (FR-005)" {
            match Wav.tryParse (sampleWav ()) with
            | Some pcm ->
                Expect.equal pcm.Channels 1 "mono"
                Expect.equal pcm.BitsPerSample 16 "16-bit"
                Expect.equal pcm.SampleRate 44100 "44.1 kHz"
                Expect.equal pcm.Data.Length 4 "2 frames * 16-bit mono = 4 bytes"
            | None -> failtest "expected a parsed WAV"
        }

        test "Wav.tryParse returns None on malformed input, never throws (FR-005)" {
            Expect.isNone (Wav.tryParse [| 1uy; 2uy; 3uy |]) "too short -> None"
            Expect.isNone
                (Wav.tryParse (System.Text.Encoding.ASCII.GetBytes "NOTAWAVEFILE...."))
                "bad header -> None"
        }

        // --- #20: the WAV-cache and voice-reclaim seams, unit-tested without a device. ---

        test "BufferCache uploads once per id and reuses the handle thereafter (#20)" {
            // The old backend re-parsed + re-uploaded an asset on every play; the cache uploads once.
            let cache = BufferCache.T<SoundId>()
            let mutable creates = 0
            let create () = creates <- creates + 1; Some 42u
            let first = cache.GetOrAdd(SoundId "s", create)
            let second = cache.GetOrAdd(SoundId "s", create)
            Expect.equal first (Some 42u) "first miss uploads and returns the handle"
            Expect.equal second (Some 42u) "second play returns the same cached handle"
            Expect.equal creates 1 "the asset is decoded/uploaded once, not per play"
            Expect.equal cache.Count 1 "one distinct handle is held"
        }

        test "BufferCache keys handles by id and lists every one for disposal (#20)" {
            let cache = BufferCache.T<SoundId>()
            cache.GetOrAdd(SoundId "a", fun () -> Some 1u) |> ignore
            cache.GetOrAdd(SoundId "b", fun () -> Some 2u) |> ignore
            // A hit must not re-create, even when the create thunk would return a different handle.
            cache.GetOrAdd(SoundId "a", fun () -> Some 99u) |> ignore
            Expect.equal cache.Count 2 "two distinct ids -> two handles"
            Expect.equal (Set.ofArray cache.Handles) (Set.ofList [ 1u; 2u ]) "every uploaded handle is listed for deletion"
        }

        test "BufferCache does not cache a failed upload, so a later resolve still populates (#20)" {
            let cache = BufferCache.T<SoundId>()
            let mutable resolvable = false
            let create () = if resolvable then Some 7u else None
            Expect.equal (cache.GetOrAdd(SoundId "s", create)) None "an unresolved/unparseable asset yields None"
            Expect.equal cache.Count 0 "a failed upload is not cached"
            resolvable <- true
            Expect.equal (cache.GetOrAdd(SoundId "s", create)) (Some 7u) "a later successful resolve populates the entry"
            Expect.equal cache.Count 1 "now cached"
        }

        test "VoicePool reclaims finished voices and reuses their handles instead of leaking (#20)" {
            // This is the leak in #20: one-shots were allocated and never reclaimed. Here, once the
            // handed-out voices finish, the next Acquire reuses a handle rather than allocating more.
            let fake = VoicePoolFake()
            let pool = VoicePool.T(fake.Ops, 240)
            let handles = [ for _ in 1..3 -> pool.Acquire() ]
            Expect.equal fake.GenCount 3 "three sounding voices -> three allocations"
            Expect.equal pool.ActiveCount 3 "all three are active"
            for h in handles do fake.MarkStopped h
            let reused = pool.Acquire()
            Expect.equal fake.GenCount 3 "a finished voice is reclaimed and reused — no new allocation"
            Expect.isTrue (List.contains reused handles) "the reused handle is one already reclaimed"
            Expect.isFalse pool.HasStolen "reuse is not a steal"
        }

        test "VoicePool steals the oldest voice at the ceiling rather than failing silently (#20)" {
            // Nothing ever stops, so the pool hits its ceiling with every voice sounding. Past it, the
            // oldest is stopped and its handle reused — a defined oldest-drop, and HasStolen lets the
            // backend log the ceiling once instead of dropping audio silently.
            let fake = VoicePoolFake()
            let pool = VoicePool.T(fake.Ops, 2)
            let first = pool.Acquire()
            pool.Acquire() |> ignore
            let stealer = pool.Acquire()
            Expect.equal fake.GenCount 2 "past the ceiling no new handle is allocated"
            Expect.equal fake.StopCalls [ first ] "the oldest voice is stopped so its handle can be reused"
            Expect.equal stealer first "the stolen (oldest) handle is the one handed back"
            Expect.equal pool.ActiveCount 2 "the pool stays at its ceiling"
            Expect.isTrue pool.HasStolen "the steal is visible, so the backend can log it once"
        }

        test "VoicePool clamps a non-positive ceiling to at least one voice (no empty-pool steal) (#20)" {
            let fake = VoicePoolFake()
            let pool = VoicePool.T(fake.Ops, 0)
            // With ceiling clamped to 1, the first Acquire must allocate (not steal from an empty
            // pool, which would have thrown), and the second must steal that one voice.
            let first = pool.Acquire()
            let second = pool.Acquire()
            Expect.equal fake.GenCount 1 "clamped ceiling of 1 holds a single voice"
            Expect.equal second first "the second Acquire steals and reuses the only voice"
            Expect.isTrue pool.HasStolen "the ceiling is enforced, not ignored"
        }

        test "VoicePool DisposeAll stops and deletes every handle it owns (#20)" {
            let fake = VoicePoolFake()
            let pool = VoicePool.T(fake.Ops, 240)
            let a = pool.Acquire()
            let b = pool.Acquire()
            pool.DisposeAll()
            Expect.equal (Set.ofList fake.DeleteCalls) (Set.ofList [ a; b ]) "every owned handle is deleted"
            Expect.equal (Set.ofList fake.StopCalls) (Set.ofList [ a; b ]) "each sounding voice is stopped before deletion"
            Expect.equal pool.ActiveCount 0 "the pool is emptied"
        }
    ]
