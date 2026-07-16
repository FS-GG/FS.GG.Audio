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

// A tiny valid WAV (2 sample frames) in a caller-chosen codec/shape, for the WAV-parser tests.
// `formatTag` is the one field that used to go unread — hence a knob for it.
let private wavWith (formatTag: int16) (channels: int16) (bits: int16) : byte[] =
    use ms = new System.IO.MemoryStream()
    use w = new System.IO.BinaryWriter(ms)
    let data = [| 0uy; 0uy; 1uy; 0uy |]
    let rate = 44100
    let byteRate = rate * int channels * int bits / 8
    let blockAlign = int16 (int channels * int bits / 8)
    w.Write(System.Text.Encoding.ASCII.GetBytes "RIFF")
    w.Write(36 + data.Length)
    w.Write(System.Text.Encoding.ASCII.GetBytes "WAVE")
    w.Write(System.Text.Encoding.ASCII.GetBytes "fmt ")
    w.Write(16)
    w.Write(formatTag)
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

// A tiny valid mono/16-bit PCM WAV (2 sample frames) for the WAV-parser test.
let private sampleWav () : byte[] = wavWith 1s 1s 16s

// The same mono/16-bit PCM payload written in the WAVE_FORMAT_EXTENSIBLE form: wFormatTag is 0xFFFE
// and the real codec lives in a SubFormat GUID whose first four bytes are the tag it stands for.
// This is an ORDINARY, playable PCM file — the form any multichannel export takes, and plenty of
// stereo ones — which is exactly why the codec check has to resolve the subformat instead of
// rejecting the marker.
let private extensiblePcmWav () : byte[] =
    use ms = new System.IO.MemoryStream()
    use w = new System.IO.BinaryWriter(ms)
    let data = [| 0uy; 0uy; 1uy; 0uy |]
    w.Write(System.Text.Encoding.ASCII.GetBytes "RIFF")
    w.Write(60 + data.Length)
    w.Write(System.Text.Encoding.ASCII.GetBytes "WAVE")
    w.Write(System.Text.Encoding.ASCII.GetBytes "fmt ")
    w.Write(40) // 16 + cbSize(22), word-aligned
    w.Write(0xFFFEs) // wFormatTag = WAVE_FORMAT_EXTENSIBLE (as a signed Int16 this reads -2)
    w.Write(1s)
    w.Write(44100)
    w.Write(88200)
    w.Write(2s)
    w.Write(16s)
    w.Write(22s) // cbSize
    w.Write(16s) // wValidBitsPerSample
    w.Write(4) // dwChannelMask
    // KSDATAFORMAT_SUBTYPE_PCM = 00000001-0000-0010-8000-00AA00389B71
    w.Write(1)
    w.Write(0s)
    w.Write(0x10s)
    w.Write([| 0x80uy; 0x00uy; 0x00uy; 0xAAuy; 0x00uy; 0x38uy; 0x9Buy; 0x71uy |])
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
                    text.Split([| "reached IAudioBackend directly" |], System.StringSplitOptions.None).Length - 1

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

        test "Null backend opens no device, never throws, and reports itself REQUESTED (FR-002, #34)" {
            let nb = NullBackend.create () :> IAudioBackend
            HostAudio.play nb [ CoreAudio.stopMusic; CoreAudio.playSfx (SoundId "x") 0.2 ]
            nb.Dispose()
            // `Expect.isTrue true` stood here — a tautology that passed whatever happened, asserting
            // only "no exception" by side effect. The real content is that a DELIBERATE Null is
            // distinguishable from a SUBSTITUTED one (#34): both are silent, and only one is a bug.
            Expect.equal
                (Backend.kindOf nb)
                (BackendKind.RecordOnly Silence.Requested)
                "a Null the product asked for reports Requested — never DeviceUnavailable"
            Expect.isFalse (Backend.isDeviceBacked nb) "record-only: nothing given to it is audible"
        }

        // The guard must not wave through a backend it did not build. `kindOf` matches BOTH known
        // backends positively; a `| _ -> DeviceBacked` default would report every record-only fake in
        // this repo (and every product's own stub) as audible, so a suite writing the recommended
        // `if isDeviceBacked then assert else skip` against its own fake would assert against a
        // recorder and go green because nothing played — #34, reintroduced by #34's own fix.
        test "an IAudioBackend this library did not build is Unknown, never DeviceBacked (#34)" {
            let fake = new RecordingBackend() :> IAudioBackend
            Expect.equal (Backend.kindOf fake) BackendKind.Unknown "a foreign backend is Unknown, not assumed audible"
            Expect.isFalse
                (Backend.isDeviceBacked fake)
                "isDeviceBacked fails CLOSED on a backend it cannot vouch for — guessing true is how a fake passes for a device"
        }

        // The Null fallback must stay a PLAIN IAudioBackend. FS.GG.Audio.Engine feature-detects
        // IMixingBackend, so if Null implemented it the Engine would push realized gains at a recorder
        // instead of taking its non-positional degrade path. Headless-safe — no device required — so
        // unlike the #11 device test below, this one ALWAYS has a subject and always asserts.
        test "the Null backend never implements IMixingBackend, so the Engine degrades over it (#11)" {
            let nb = NullBackend.create () :> IAudioBackend
            Expect.isFalse (nb :? IMixingBackend) "the Null fallback stays non-mixing"
        }

        test "OpenAlBackend.create degrades to a Null it MARKS as substituted, and never throws (FR-004, #34)" {
            let resolver =
                { ResolveSound = (fun _ -> None)
                  ResolveTrack = (fun _ -> None) }
            let backend = OpenAlBackend.create resolver
            try
                HostAudio.play backend [ CoreAudio.playSfx (SoundId "s") 0.5; CoreAudio.stopMusic ]
                // Runs on BOTH kinds of box and asserts something FALSIFIABLE on each — where it used
                // to `Expect.isTrue true` and assert nothing at all.
                match Backend.kindOf backend with
                | BackendKind.DeviceBacked ->
                    // NOT `Expect.isTrue (isDeviceBacked backend)` — `isDeviceBacked` is *defined* as
                    // this very match, so asserting it here could not fail under any implementation,
                    // and the branch would say nothing on a machine that has a device. Assert instead
                    // what being device-backed is supposed to MEAN: it is not the Null fallback, and it
                    // spatializes (#11).
                    Expect.isFalse (backend :? NullBackend.T) "a device opened: this is not the Null fallback"
                    Expect.isTrue (backend :? IMixingBackend) "a real device backend implements IMixingBackend (#11)"
                | BackendKind.RecordOnly(Silence.DeviceUnavailable reason) ->
                    Expect.isFalse
                        (System.String.IsNullOrWhiteSpace reason)
                        "the substitution carries the device's own reason, so the silence is explicable"
                    Expect.isFalse (Backend.isDeviceBacked backend) "a substituted Null is not device-backed"
                | BackendKind.RecordOnly Silence.Requested ->
                    // Must be impossible: `create` never REQUESTS a Null, it SUBSTITUTES one. If it ever
                    // reports Requested, the substitution has lost the fact that it was a substitution —
                    // #34 exactly — and this is the assertion that catches it.
                    failtest
                        "OpenAlBackend.create returned a Null marked Requested — a substitution must never masquerade as a deliberate record-only backend"
                | BackendKind.Unknown ->
                    failtest "OpenAlBackend.create returned a backend this library does not recognise as its own"
            finally
                backend.Dispose()
        }

        // This test was VACUOUS on headless CI, and that was the bug (#34). `create` degrades to Null
        // where there is no device, so it asserted against a recorder and passed *because* nothing
        // played — a green tick on a subject that was never constructed. It now SKIPS loudly
        // (`Ignored`, not `Passed`) when it has nothing to test, which is the honest report.
        test "the OpenAL backend implements IMixingBackend when a device is present (#11)" {
            let resolver =
                { ResolveSound = (fun _ -> None)
                  ResolveTrack = (fun _ -> None) }
            let backend = OpenAlBackend.create resolver
            try
                match Backend.kindOf backend with
                | BackendKind.DeviceBacked ->
                    Expect.isTrue (backend :? IMixingBackend) "a real device backend spatializes (#11)"
                | kind ->
                    skiptest (
                        sprintf
                            "no audio device here (%A) — OpenAlBackend.create substituted the Null backend, so the real device backend was NEVER CONSTRUCTED and this assertion has no subject. Reported Ignored rather than Passed on purpose (#34)."
                            kind)
            finally
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

        // Both cases above return at the length/magic guard, BEFORE the chunk loop is entered — so
        // the walk itself had no malformed-input coverage at all, and a hang lived there. A chunk
        // size is unsigned on disk and signed once read: the advance `8 + sz + (sz &&& 1)` is exactly
        // 0 for sz = -8 and sz = -9, so `pos` stopped moving and tryParse spun forever. The `try`
        // inside it cannot catch that — a hang is not an exception.
        //
        // Assert TERMINATION, not just the return value. A regression here must fail this test in
        // seconds; left to `Expect.isNone` alone it would wedge the whole CI run with no clue which
        // test did it, which is the failure mode that hid the bug in the first place.
        test "Wav.tryParse terminates on a corrupt chunk size, never spins (FR-005)" {
            let riffWith (size: int) =
                let b = Array.zeroCreate<byte> 64
                System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0)
                System.BitConverter.GetBytes(56).CopyTo(b, 4)
                System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(b, 8)
                System.Text.Encoding.ASCII.GetBytes("junk").CopyTo(b, 12)
                System.BitConverter.GetBytes(size).CopyTo(b, 16)
                b
            // -8 and -9 are the two sizes that advance by exactly zero. The rest bracket them, and
            // Int32.MaxValue covers the other end, where `body + sz` used to overflow `pos` negative
            // and the parser survived only because `ascii` then threw into the catch.
            for size in [ -9; -8; -7; -1; 0; System.Int32.MinValue; System.Int32.MaxValue ] do
                let parse = System.Threading.Tasks.Task.Run(fun () -> Wav.tryParse (riffWith size))
                if not (parse.Wait(System.TimeSpan.FromSeconds 5.0)) then
                    failtestf "Wav.tryParse did not terminate on a chunk size of %d — it is spinning" size
                Expect.isNone parse.Result (sprintf "a chunk size of %d is malformed -> None" size)
        }

        // wFormatTag went unread for the life of this reader, and the omission was the one bug in the
        // component whose degrade was NOISE rather than silence: an IEEE-float or ADPCM file has
        // plausible channels/bits, so it passed `bufferFormat` and was uploaded as raw PCM.
        test "Wav.tryParse reports the codec, so non-PCM is not decoded as PCM (FR-005)" {
            let tagOf (bytes: byte[]) =
                match Wav.tryParse bytes with
                | Some pcm -> pcm.FormatTag
                | None -> failtest "expected a structurally valid WAV to parse"
            // Structural parse: these ARE well-formed WAVs and still parse. What changed is that the
            // codec now comes back with them, so `uploadBuffer` can refuse them by name.
            Expect.equal (tagOf (sampleWav ())) Wav.FormatPcm "plain PCM reports FormatPcm"
            Expect.equal (tagOf (wavWith 3s 1s 32s)) 3 "IEEE-float reports its own tag, not PCM"
            Expect.equal (tagOf (wavWith 0x11s 1s 16s)) 0x11 "IMA-ADPCM reports its own tag, not PCM"
            Expect.equal (tagOf (wavWith 0x55s 2s 16s)) 0x55 "MP3-in-WAV reports its own tag, not PCM"
            // The one that matters most: mono 16-bit ADPCM has a channel/bit pair OpenAL supports, so
            // the codec is the ONLY thing standing between those bytes and a burst of static.
            Expect.notEqual (tagOf (wavWith 0x11s 1s 16s)) Wav.FormatPcm "mono 16-bit ADPCM is not PCM"
        }

        test "Wav.tryParse resolves WAVE_FORMAT_EXTENSIBLE to its subformat, so PCM still plays (FR-005)" {
            // The regression this guards is worse than the bug it accompanies. Extensible PCM is
            // ordinary, playable audio that parses correctly today; a codec check that rejected
            // `tag <> 1` outright would turn every such asset SILENT — trading noise-on-bad-files for
            // silence-on-good-ones. 0xFFFE is also negative read as Int16, so a signed read would
            // misreport it here too.
            match Wav.tryParse (extensiblePcmWav ()) with
            | Some pcm ->
                Expect.equal pcm.FormatTag Wav.FormatPcm "extensible PCM resolves to FormatPcm, not 0xFFFE"
                Expect.equal pcm.Channels 1 "mono survives the extensible header"
                Expect.equal pcm.BitsPerSample 16 "16-bit survives the extensible header"
                Expect.equal pcm.Data.Length 4 "the data chunk is found past the longer fmt chunk"
            | None -> failtest "expected an extensible PCM WAV to parse — it is valid, playable audio"
        }

        test "Wav.tryParse will not read a subformat the fmt chunk never carried (FR-005)" {
            // A file declaring EXTENSIBLE with a SHORT (16-byte) fmt chunk supplies no subformat GUID.
            // Offset 24 of that chunk is not a GUID — it is whatever chunk follows, i.e. the audio
            // samples. Bounds-checking the read against the FILE length is not enough: it is in
            // bounds and still meaningless. Here the data is crafted to begin `01 00 00 00`, so a
            // reader that trusts the file length alone finds "PCM" in the sample data, uploads these
            // bytes as PCM, and hands back the exact noise bug the codec check exists to prevent.
            let shortExtensible () =
                use ms = new System.IO.MemoryStream()
                use w = new System.IO.BinaryWriter(ms)
                let data = Array.append (System.BitConverter.GetBytes 1) (Array.zeroCreate 28)
                w.Write(System.Text.Encoding.ASCII.GetBytes "RIFF")
                w.Write(36 + data.Length)
                w.Write(System.Text.Encoding.ASCII.GetBytes "WAVE")
                w.Write(System.Text.Encoding.ASCII.GetBytes "fmt ")
                w.Write(16) // a 16-byte fmt chunk: no cbSize, no subformat
                w.Write(0xFFFEs) // ... yet it claims EXTENSIBLE
                w.Write(1s)
                w.Write(44100)
                w.Write(88200)
                w.Write(2s)
                w.Write(16s)
                w.Write(System.Text.Encoding.ASCII.GetBytes "data")
                w.Write(data.Length)
                w.Write(data)
                w.Flush()
                ms.ToArray()
            match Wav.tryParse (shortExtensible ()) with
            | Some pcm ->
                Expect.notEqual
                    pcm.FormatTag
                    Wav.FormatPcm
                    "a subformat that was never carried must not be read out of the sample data as PCM"
                Expect.equal pcm.FormatTag 0xFFFE "the codec is unknown, and says so, rather than guessing"
            | None -> () // rejecting it outright is also a correct answer
        }

        test "AssetDiagnostics names the codec and the fix, and does not misreport it as a bit depth (#28)" {
            let msg =
                AssetDiagnostics.message
                    (AssetDiagnostics.Sound(SoundId "boom"))
                    (AssetDiagnostics.UnsupportedCodec 0x11)
            Expect.stringContains msg "boom" "names the product's own id"
            Expect.stringContains msg "IMA-ADPCM" "names the codec, not just a number"
            Expect.stringContains msg "PCM" "names the fix: re-export as PCM"
            // The reason this is its own Failure case. UnsupportedFormat would have said "1-channel
            // 16-bit WAV, which OpenAL has no buffer format for" — false, OpenAL has Mono16 — and
            // sent the reader to change a bit depth that was never the problem.
            Expect.isFalse
                (msg.Contains "no buffer format")
                "must not blame the channel/bit pair for what the codec did"
        }

        test "Wav.tryParse keeps a complete fmt+data that precedes a corrupt trailing chunk (FR-005)" {
            // Pins the decision the guard above encodes: a bad size stops the walk rather than
            // discarding what was already read, so an asset whose audio is fully readable before some
            // trailing garbage still plays. The alternative (None on any corruption anywhere) would
            // silently drop files that are fine — a stricter parser, and a worse one.
            let wav = sampleWav ()
            let corrupt = Array.append wav (Array.zeroCreate 8)
            System.Text.Encoding.ASCII.GetBytes("LIST").CopyTo(corrupt, wav.Length)
            System.BitConverter.GetBytes(-8).CopyTo(corrupt, wav.Length + 4)
            match Wav.tryParse corrupt with
            | Some pcm -> Expect.equal pcm.Data.Length 4 "the good data chunk ahead of the corruption still parses"
            | None -> failtest "expected the leading fmt+data to survive a corrupt trailing chunk"
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

        // --- #28: a missing sound asset is silence — say which id, and why, once. ---

        test "a missing asset is named, not swallowed — the id and the resolver that failed (#28)" {
            // The defect: `SoundId "explosion"` resolving to nothing played nothing and REPORTED
            // nothing, so a typo'd or unshipped asset was indistinguishable from a played one.
            let line = AssetDiagnostics.message (AssetDiagnostics.Sound(SoundId "explosion")) AssetDiagnostics.Unresolved
            Expect.stringContains line "explosion" "the diagnostic names the id that failed"
            Expect.stringContains line "silent" "...and the consequence, so it is not mistaken for a warning about nothing"
            // It cannot name a path — the host does not own the id -> file mapping (FR-005) — so it
            // names the product-supplied function that returned None, which is where the fix lives.
            Expect.stringContains line "AssetResolver.ResolveSound" "...and points at the resolver the product supplies"
        }

        test "a missing track names the track resolver, not the sound one (#28)" {
            let line = AssetDiagnostics.message (AssetDiagnostics.Track(TrackId "theme")) AssetDiagnostics.Unresolved
            Expect.stringContains line "theme" "the diagnostic names the track"
            Expect.stringContains line "AssetResolver.ResolveTrack" "a missing track points at ResolveTrack"
        }

        test "an undecodable asset is distinguished from a missing one (#28)" {
            // Three failures, three different fixes: ship the asset / re-export it / convert it.
            // Collapsing them into one "no sound" is what sent the last reader to a template comment
            // in another repo to find out what was wrong.
            let notWav = AssetDiagnostics.message (AssetDiagnostics.Sound(SoundId "s")) (AssetDiagnostics.NotWav 1234)
            Expect.stringContains notWav "not a PCM WAV" "bytes that are not a WAV say so"
            Expect.stringContains notWav "1234" "...and how many bytes came back, so an HTML 404 body is recognisable"

            let badFormat =
                AssetDiagnostics.message (AssetDiagnostics.Sound(SoundId "s")) (AssetDiagnostics.UnsupportedFormat(6, 24))
            Expect.stringContains badFormat "6-channel 24-bit" "an unsupported format names the format it got"
            Expect.stringContains badFormat "mono or stereo" "...and the one it needs"
        }

        test "the missing-asset diagnostic fires once per id, not once per play (#28)" {
            // Load-bearing: BufferCache deliberately does NOT cache a failed resolve (see the #20
            // test above), so the backend re-enters this leg on EVERY play of the missing id. A cue
            // retriggered each frame would emit a line each frame and bury its own message.
            let lines = ResizeArray<string>()
            let diagnostics = AssetDiagnostics.T(lines.Add)
            let missing = AssetDiagnostics.Sound(SoundId "explosion")

            for _ in 1..60 do
                diagnostics.Report(missing, AssetDiagnostics.Unresolved)

            Expect.equal lines.Count 1 "sixty plays of one missing id emit one line, not sixty"
            Expect.equal diagnostics.ReportedCount 1 "one distinct id reported"

            // But a *different* missing id is genuinely new information, and is not latched away.
            diagnostics.Report(AssetDiagnostics.Sound(SoundId "footstep"), AssetDiagnostics.Unresolved)
            Expect.equal lines.Count 2 "a second missing id is reported on its own"
            // ...and so is the same string under the other id type: SoundId "x" is not TrackId "x".
            diagnostics.Report(AssetDiagnostics.Track(TrackId "explosion"), AssetDiagnostics.Unresolved)
            Expect.equal lines.Count 3 "a track is not deduped against a sound with the same name"
            Expect.equal diagnostics.ReportedCount 3 "three distinct assets reported"
        }

        // --- #33: the device-fault diagnostic. The OpenAL backend's guarded legs need a real device
        // to reach in anger, so — exactly as for AssetDiagnostics above — the latch is device-free
        // and is asserted directly. The Engine suite drives the same latch through a live failure. ---

        test "DeviceDiagnostics.message names the operation, the degrade, and the way back (#33)" {
            let error = System.InvalidOperationException "AL_INVALID_OPERATION" :> exn

            let transient = DeviceDiagnostics.message DeviceDiagnostics.PlayAt DeviceDiagnostics.Transient error
            Expect.stringContains transient "IMixingBackend.PlayAt" "the faulting operation is named"
            Expect.stringContains transient "AL_INVALID_OPERATION" "the driver's own account survives"
            Expect.stringContains transient "SILENCE" "the consequence is named"

            // The escalated line has to carry what the first one cannot: that this is not a blip, and
            // that nothing will play again until someone does something about it.
            let persistent =
                DeviceDiagnostics.message DeviceDiagnostics.PlayAt (DeviceDiagnostics.Persistent 60) error
            Expect.stringContains persistent "PERSISTENT" "a dead device is called what it is"
            Expect.stringContains persistent "60" "the length of the fault run is named"
            Expect.stringContains persistent "OpenAlBackend.create" "and the fix is named"

            // And the retraction, when it comes back: a diagnostic left standing after it stops being
            // true is the same species of lie as one that was never emitted.
            let back = DeviceDiagnostics.recovered DeviceDiagnostics.PlayAt 60
            Expect.stringContains back "IMixingBackend.PlayAt" "the recovered operation is named"
            Expect.stringContains back "again" "it says the device came back"
        }

        test "DeviceDiagnostics latches per operation: first fault once, persistent once (#33)" {
            let lines = ResizeArray<string>()
            let device = DeviceDiagnostics.T(lines.Add, 3)
            let boom = System.Exception "device on fire"

            device.Report(DeviceDiagnostics.PlayAt, boom)
            Expect.equal lines.Count 1 "the first fault on an operation is named"
            device.Report(DeviceDiagnostics.PlayAt, boom)
            Expect.equal lines.Count 1 "a second fault is not a second line"
            device.Report(DeviceDiagnostics.PlayAt, boom)
            Expect.equal lines.Count 2 "the third consecutive fault crosses persistentAfter"
            for _ in 1..100 do device.Report(DeviceDiagnostics.PlayAt, boom)
            Expect.equal lines.Count 2 "and it escalates exactly once, however long it goes on"

            // Operations are counted separately: a device answering SetBusGain while it fails every
            // PlayAt is not the same animal as one that has stopped answering, and a working leg's
            // success must not reset — or mask — a dead leg's run.
            Expect.equal (device.ConsecutiveFaults DeviceDiagnostics.SetBusGain) 0 "counted on its own"
            Expect.isTrue (device.IsPersistent DeviceDiagnostics.PlayAt) "PlayAt is gone"
            device.Report(DeviceDiagnostics.SetBusGain, boom)
            Expect.equal lines.Count 3 "the first fault on a second operation is news, and is reported"
            Expect.isFalse (device.IsPersistent DeviceDiagnostics.SetBusGain) "one fault is not persistent"
        }

        test "a device that recovers ends its fault run — a hiccup is never escalated (#33)" {
            // The distinction the whole issue turns on. Degrading a transient fault to silence is
            // correct and must stay quiet-ish; degrading a PERMANENT one to silence is the defect.
            let lines = ResizeArray<string>()
            let device = DeviceDiagnostics.T(lines.Add, 3)
            let boom = System.Exception "hiccup"

            // Fail, fail, recover — twenty times over. The run never reaches the threshold, so this is
            // named once as a hiccup and never accused of being a dead device.
            for _ in 1..20 do
                device.Report(DeviceDiagnostics.Realize, boom)
                device.Report(DeviceDiagnostics.Realize, boom)
                device.Succeeded DeviceDiagnostics.Realize

            Expect.equal lines.Count 1 "forty faults, none of them a run, one line"
            Expect.isFalse (device.IsPersistent DeviceDiagnostics.Realize) "a recovering device is not a dead one"
            Expect.equal (device.ConsecutiveFaults DeviceDiagnostics.Realize) 0 "each success reset the run"
        }
    ]
