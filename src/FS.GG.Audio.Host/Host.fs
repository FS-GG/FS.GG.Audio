namespace FS.GG.Audio.Host

#nowarn "9" // native-interop pointers (OpenAL device/context handles) are guarded and disposed below.

open System
open FS.GG.Audio.Core

type AssetResolver =
    { ResolveSound: SoundId -> byte[] option
      ResolveTrack: TrackId -> byte[] option }

type IAudioBackend =
    inherit IDisposable
    abstract member Play: effect: AudioEffect -> unit

// Optional mixing/spatial control (004-audio-engine). Additive: existing IAudioBackend-only
// backends stay valid; FS.GG.Audio.Engine feature-detects this and degrades when it is absent.
type IMixingBackend =
    inherit IAudioBackend
    abstract member SetBusGain: bus: Bus * gain: float -> unit
    abstract member SetListener: x: float * y: float * z: float -> unit
    abstract member PlayAt: sound: SoundId * gain: float * pan: float -> unit

// WHY a backend makes no sound (#34), and the distinction is the entire point of this type. A Null
// backend the product ASKED for is correct and expected — it is the default, and the test/CI backend
// (FR-002). One that `OpenAlBackend.create` SUBSTITUTED, because no device would open, is a game
// running silently. At the `IAudioBackend` seam those two are indistinguishable: both satisfy the
// interface, both accept every effect, both play none.
// RequireQualifiedAccess, like every other DU in this file: `open FS.GG.Audio.Host` must not inject
// bare `Requested`/`Unknown`/`DeviceBacked` into a consumer's scope, where they would collide with
// names a game defines for its own reasons.
[<RequireQualifiedAccess>]
type Silence =
    /// `NullBackend.create` — chosen deliberately (FR-002).
    | Requested
    /// `OpenAlBackend.create` could not open a device and degraded to Null (FR-004). Carries the
    /// device's own account of why, which is the only one anyone will get.
    | DeviceUnavailable of reason: string

// What a caller is actually HOLDING (#34) — askable without a type test and without exception
// handling, which is what lets a CI suite skip loudly instead of asserting against a tape recorder.
[<RequireQualifiedAccess>]
type BackendKind =
    /// The bundled OpenAL backend, with a device actually open.
    | DeviceBacked
    /// A record-only backend: it accepts every effect and plays none. `Silence` says why.
    | RecordOnly of Silence
    /// An `IAudioBackend` this library did not build — a product's own backend, or a test fake. We
    /// cannot say whether it reaches a device, and MUST NOT guess `DeviceBacked`: that is how the
    /// guard this type exists to provide would wave a record-only fake through as audible, which is
    /// #34 one level up. Whoever built it knows what it is and does not need to ask.
    | Unknown

// Reach Core's Audio module without shadowing the host's own `Audio` module below.
module CoreAudio = FS.GG.Audio.Core.Audio

[<RequireQualifiedAccess>]
module Wav =

    type PcmData =
        { Channels: int
          BitsPerSample: int
          SampleRate: int
          Data: byte[] }

    // Minimal RIFF/WAVE PCM reader: walk chunks, pull fmt (channels/rate/bits) + data. Total —
    // returns None on anything malformed or unrecognized rather than throwing.
    let tryParse (bytes: byte[]) : PcmData option =
        try
            let ascii off len = Text.Encoding.ASCII.GetString(bytes, off, len)
            if bytes.Length < 44 || ascii 0 4 <> "RIFF" || ascii 8 4 <> "WAVE" then
                None
            else
                let mutable pos = 12
                let mutable channels = 0
                let mutable sampleRate = 0
                let mutable bits = 0
                let mutable dataOff = -1
                let mutable dataLen = 0
                while pos + 8 <= bytes.Length do
                    let id = ascii pos 4
                    let sz = BitConverter.ToInt32(bytes, pos + 4)
                    let body = pos + 8
                    if id = "fmt " && body + 16 <= bytes.Length then
                        channels <- int (BitConverter.ToInt16(bytes, body + 2))
                        sampleRate <- BitConverter.ToInt32(bytes, body + 4)
                        bits <- int (BitConverter.ToInt16(bytes, body + 14))
                    elif id = "data" then
                        dataOff <- body
                        dataLen <- sz
                    // chunks are word-aligned: skip the body plus any pad byte.
                    pos <- body + sz + (sz &&& 1)
                if dataOff < 0 || channels = 0 || bits = 0 then
                    None
                else
                    let len = max 0 (min dataLen (bytes.Length - dataOff))
                    Some
                        { Channels = channels
                          BitsPerSample = bits
                          SampleRate = sampleRate
                          Data = Array.sub bytes dataOff len }
        with _ -> None

[<RequireQualifiedAccess>]
module Spatial =

    // FS.GG.Audio.Engine owns the spatial model: by the time a voice reaches IMixingBackend.PlayAt
    // its distance attenuation is already folded into `gain`, and its direction survives only as a
    // stereo pan in [-1, 1]. A device backend therefore has to place the source where the device's
    // own distance model cannot attenuate it a second time: on the unit circle around the listener,
    // in the listener's frame. Pan drives the lateral axis; the remainder goes to -z (straight
    // ahead, the OpenAL listener's default facing), so a centred voice sits in front of the
    // listener rather than inside their head.
    let panToPosition (pan: float) : float * float * float =
        let p =
            if Double.IsNaN pan then 0.0
            elif pan < -1.0 then -1.0
            elif pan > 1.0 then 1.0
            else pan
        // p^2 <= 1 by the clamp above, so the sqrt is total and the result is always unit-length.
        // At a hard pan the depth is `-sqrt 0.0` = negative zero; fold it to +0.0 so a printed or
        // compared position reads as the plain 0.0 it is.
        let z = -sqrt (1.0 - p * p)
        (p, 0.0, (if z = 0.0 then 0.0 else z))

[<RequireQualifiedAccess>]
module BufferCache =

    // A device-free memo of uploaded buffer handles, keyed by a product id (SoundId/TrackId). The
    // OpenAL backend used to parse + upload an asset on *every* play (#20); this uploads once and
    // hands back the same handle thereafter. Pure w.r.t. the device — it only holds `uint` handles
    // and calls back to create one — so it is exercised headless behind a fake `create`.
    [<Sealed>]
    type T<'k when 'k: equality>() =
        let cache = Collections.Generic.Dictionary<'k, uint>()

        // The cached handle for `key`, creating and storing it once via `create` on first miss. A
        // `None` from `create` (unresolved / unparseable asset) is deliberately NOT cached, so a
        // later successful resolve of the same id can still populate the entry.
        member _.GetOrAdd(key: 'k, create: unit -> uint option) : uint option =
            match cache.TryGetValue key with
            | true, handle -> Some handle
            | _ ->
                match create () with
                | Some handle ->
                    cache.[key] <- handle
                    Some handle
                | None -> None

        // Number of distinct handles held (one per successfully uploaded id).
        member _.Count = cache.Count

        // Every cached handle, for deletion when the backend is disposed.
        member _.Handles : uint[] = Seq.toArray cache.Values

[<RequireQualifiedAccess>]
module VoicePool =

    // The device operations a pool drives, named so a caller cannot transpose the two `uint -> unit`
    // handle operations. In the OpenAL backend these are GenSource, a `SourceState = Stopped` test,
    // SourceStop, and DeleteSource; in a test they are counting fakes, which is what lets the
    // reclaim/steal logic run with no device (#20).
    type Ops =
        { Gen: unit -> uint
          IsStopped: uint -> bool
          Stop: uint -> unit
          Delete: uint -> unit }

    // A bounded pool of one-shot voice handles. Before handing out a source it reclaims any that
    // have finished (the leak in #20: one-shots were GenSource'd and never deleted, so a long
    // session exhausted OpenAL's finite source ceiling and `Play` then failed *silently*). Finished
    // handles are reused rather than churned; past `ceiling` the oldest still-sounding voice is
    // stolen — a defined oldest-drop, never silent failure. Music is long-lived and NOT pooled.
    [<Sealed>]
    type T(ops: Ops, ceiling: int) =
        // A ceiling below 1 would make the first Acquire steal from an empty pool; clamp it so the
        // pool always holds at least one voice and Acquire never indexes an empty list.
        let ceiling = max 1 ceiling
        // Handed out and presumed sounding, oldest-first.
        let active = Collections.Generic.List<uint>()
        // Finished handles kept for reuse, so a steady stream of one-shots does not churn Gen/Delete.
        let free = Collections.Generic.List<uint>()
        let mutable stolen = false

        // Move every finished voice from `active` to `free`.
        let reclaimFinished () =
            let mutable i = 0
            while i < active.Count do
                if ops.IsStopped active.[i] then
                    free.Add active.[i]
                    active.RemoveAt i
                else
                    i <- i + 1

        // A source handle ready to be (re)configured and played: reclaims finished voices, reuses a
        // free handle when one exists, otherwise grows up to `ceiling`, and past it steals the
        // oldest sounding voice and reuses that handle.
        member _.Acquire() : uint =
            reclaimFinished ()
            let src =
                if free.Count > 0 then
                    let s = free.[free.Count - 1]
                    free.RemoveAt(free.Count - 1)
                    s
                elif active.Count < ceiling then
                    ops.Gen()
                else
                    stolen <- true
                    let victim = active.[0]
                    active.RemoveAt 0
                    ops.Stop victim
                    victim
            active.Add src
            src

        // Voices handed out and presumed still sounding.
        member _.ActiveCount = active.Count
        // Reclaimed handles available for reuse.
        member _.FreeCount = free.Count
        // True once the ceiling has forced at least one oldest-voice steal — the backend logs this
        // once, so exhaustion is visible rather than silent.
        member _.HasStolen = stolen

        // Stop and delete every handle the pool owns.
        member _.DisposeAll() =
            for s in active do
                ops.Stop s
                ops.Delete s
            for s in free do ops.Delete s
            active.Clear()
            free.Clear()

[<RequireQualifiedAccess>]
module AssetDiagnostics =

    // Why an id produced no playable buffer (#28). Three distinct fixes, so the diagnostic names
    // which one rather than saying "no sound": a missing asset is a resolver/shipping problem, a bad
    // file is an authoring problem, an unsupported format is a conversion problem.
    type Failure =
        | Unresolved
        | NotWav of bytes: int
        | UnsupportedFormat of channels: int * bitsPerSample: int

    // The asset that failed, carrying the product's own id — the only handle the host has on it.
    type Asset =
        | Sound of SoundId
        | Track of TrackId

    let private describe (asset: Asset) =
        match asset with
        | Sound(SoundId id) -> sprintf "sound '%s'" id
        | Track(TrackId id) -> sprintf "track '%s'" id

    let private resolverFn (asset: Asset) =
        match asset with
        | Sound _ -> "AssetResolver.ResolveSound"
        | Track _ -> "AssetResolver.ResolveTrack"

    // The line for one failure. A value rather than a print, so a test asserts on it directly. It
    // cannot name the file it looked for — the host does not own the id -> asset mapping (FR-005),
    // so no path exists at this layer — and names the resolver function that failed instead, which
    // is where the product's mapping actually lives.
    let message (asset: Asset) (failure: Failure) : string =
        let what = describe asset
        match failure with
        | Unresolved ->
            sprintf
                "FS.GG.Audio.Host: %s did not resolve to an asset — %s returned None, so every play of it is silent. The host does not own the id -> file mapping (FR-005): check the resolver your product supplies (a typo'd id, or an asset that was never shipped)."
                what
                (resolverFn asset)
        | NotWav bytes ->
            sprintf
                "FS.GG.Audio.Host: %s resolved to %d bytes that are not a PCM WAV this reader understands, so every play of it is silent. Re-export it as PCM WAV (RIFF/WAVE, fmt + data chunks)."
                what
                bytes
        | UnsupportedFormat(channels, bits) ->
            sprintf
                "FS.GG.Audio.Host: %s is a %d-channel %d-bit WAV, which OpenAL has no buffer format for, so every play of it is silent. Convert it to 8- or 16-bit mono or stereo (mono if it is positional — OpenAL spatializes mono buffers only)."
                what
                channels
                bits

    // A warn-once-per-id latch over `message`. Device-free (it holds ids and an emit callback, no
    // OpenAL types), which is what lets the failure leg be asserted headless — the backend that
    // hits it in anger cannot be constructed without a device.
    [<Sealed>]
    type T(emit: string -> unit) =
        let reported = Collections.Generic.HashSet<Asset>()

        // Warn-once is load-bearing, not politeness: a failed resolve is deliberately NOT cached
        // (BufferCache.GetOrAdd), so this leg is re-entered on EVERY play of the missing id — a
        // bare print here would emit once a frame for a cue the product retriggers.
        member _.Report(asset: Asset, failure: Failure) : unit =
            if reported.Add asset then
                emit (message asset failure)

        // Distinct ids reported so far — one emitted line each.
        member _.ReportedCount = reported.Count

[<RequireQualifiedAccess>]
module DeviceDiagnostics =

    // The guarded device call that threw (#33). Named rather than free-text because the latch counts
    // fault runs PER operation: a device still answering SetBusGain while it fails every PlayAt is a
    // different animal from one that has stopped answering at all, and collapsing them would let a
    // working leg's success mask a dead one's run.
    type Operation =
        | Realize
        | Play
        | PlayAt
        | SetBusGain
        | SetListener

    // A fault is a hiccup until it has repeated often enough that "hiccup" stops being a credible
    // explanation. That distinction is the whole of #33: degrading a *transient* fault to silence is
    // correct, and degrading a *persistent* one to silence is how a dead device becomes indefinite,
    // unexplained quiet.
    type Fault =
        | Transient
        | Persistent of consecutive: int

    // The run length at which a fault stops being a hiccup. The engine realizes once per frame, so
    // at 60 fps this is about a second of unbroken failure — long enough that a driver glitch would
    // have cleared, short enough that a player is still wondering why the game went quiet.
    [<Literal>]
    let DefaultPersistentAfter = 60

    let private describe (op: Operation) =
        match op with
        | Realize -> "Engine.step's realize pass"
        | Play -> "IAudioBackend.Play"
        | PlayAt -> "IMixingBackend.PlayAt"
        | SetBusGain -> "IMixingBackend.SetBusGain"
        | SetListener -> "IMixingBackend.SetListener"

    // The line for one fault. A value rather than a print, so a test asserts on it directly. It
    // carries the device's own exception text: this layer cannot say WHY a driver refused a call,
    // and the driver's message is the only account of it anyone will get.
    let message (op: Operation) (fault: Fault) (error: exn) : string =
        let what = describe op
        let why = sprintf "%s: %s" (error.GetType().Name) error.Message
        match fault with
        | Transient ->
            sprintf
                "FS.GG.Audio: the audio device threw on %s (%s). That call degraded to SILENCE rather than crashing, which is the right answer for a hiccup — playback continues. If the device is genuinely gone the fault will repeat, and this will say so again, once."
                what
                why
        | Persistent consecutive ->
            sprintf
                "FS.GG.Audio: the audio device has thrown on %s for %d consecutive calls (%s) — this is a PERSISTENT fault, not a hiccup: the device is gone (unplugged, or its driver died). Everything this backend is asked to play is silent for as long as this lasts, and the game will keep running and mixing, inaudibly, without noticing. If it does not come back, rebuild the backend (OpenAlBackend.create) to reopen a device."
                what
                consecutive
                why

    // The retraction. A device CAN come back — a headset reconnects, a driver restarts — and when it
    // does, the `Persistent` line above is left standing in the log as a claim that is no longer true.
    // An unretracted diagnostic is the same species of lie as an absent one, which is the whole point
    // of this family, so recovery is reported exactly once too.
    let recovered (op: Operation) (afterConsecutive: int) : string =
        sprintf
            "FS.GG.Audio: the audio device is answering %s again, after %d consecutive faults — sound is audible once more. Disregard the persistent-fault line above; the device came back."
            (describe op)
            afterConsecutive

    // A warn-once-per-operation latch over `message` that tracks each operation's run of consecutive
    // faults, so a persistent fault can be told from a hiccup. Device-free (it holds operation names,
    // counters and an emit callback — no OpenAL types), which is what lets BOTH failure legs be
    // asserted headless: the device that faults in anger cannot be constructed without a device.
    [<Sealed>]
    type T(emit: string -> unit, persistentAfter: int) =
        // A threshold below 1 would escalate before a single fault had been counted.
        let persistentAfter = max 1 persistentAfter
        let consecutive = Collections.Generic.Dictionary<Operation, int>()
        let firstReported = Collections.Generic.HashSet<Operation>()
        let persistentReported = Collections.Generic.HashSet<Operation>()
        let recoveryReported = Collections.Generic.HashSet<Operation>()

        new(emit: string -> unit) = T(emit, DefaultPersistentAfter)

        // Report that `op` threw. Emits the first-fault line the FIRST time `op` faults, and the
        // persistent-fault line once `op` has faulted `persistentAfter` times with NO intervening
        // success — each at most once, ever.
        //
        // Warn-once is load-bearing, not politeness: these legs are re-entered on every frame (the
        // engine realizes once a frame; the backend guards every device call), so a bare print here
        // would emit a line per frame, for as long as the game runs, about a device that is dead and
        // is going to stay dead.
        member _.Report(op: Operation, error: exn) : unit =
            let run =
                match consecutive.TryGetValue op with
                | true, n -> n + 1
                | _ -> 1
            consecutive.[op] <- run
            if firstReported.Add op then
                emit (message op Transient error)
            if run >= persistentAfter && persistentReported.Add op then
                emit (message op (Persistent run) error)

        // Report that `op` REACHED THE DEVICE and completed. This ends its fault run: an operation
        // that fails and then works is precisely the transient hiccup the degrade exists for, and it
        // must never accumulate toward "persistent". Without this, a device that glitches once an
        // hour would eventually be declared dead.
        //
        // Only ever call this for a call that actually touched the hardware. A no-op that reached no
        // device says nothing about whether the device is alive, and passing it here would let a dead
        // device be silently "recovered" by calls that never asked it for anything.
        member _.Succeeded(op: Operation) : unit =
            let run =
                match consecutive.TryGetValue op with
                | true, n -> n
                | _ -> 0
            consecutive.Remove op |> ignore
            // It came back. If we said it was gone, say that we were wrong — once.
            if run >= persistentAfter && persistentReported.Contains op && recoveryReported.Add op then
                emit (recovered op run)

        // `op`'s current run of consecutive faults — 0 once it has reached the device again.
        member _.ConsecutiveFaults(op: Operation) : int =
            match consecutive.TryGetValue op with
            | true, n -> n
            | _ -> 0

        // Whether `op` is faulting persistently RIGHT NOW — a live predicate, not a latch. A device
        // that died and came back is not a dead device, and a product driving "audio is broken" UI off
        // this must see it recover; the warn-once behaviour belongs to the emitted lines, not here.
        member _.IsPersistent(op: Operation) : bool =
            match consecutive.TryGetValue op with
            | true, n -> n >= persistentAfter
            | _ -> false

        // Lines emitted so far: one per operation that has faulted, one per operation that went
        // persistent, and one per operation that came back from it.
        member _.ReportedCount = firstReported.Count + persistentReported.Count + recoveryReported.Count

[<RequireQualifiedAccess>]
module Audio =

    // The effects a raw backend structurally CANNOT realize (#27). SetBusVolume and Duck are
    // envelopes over a mix, and this path has neither a mixer nor a clock, so they are discarded;
    // PlaySfx3D carries a world position with no listener to resolve it against, so it degrades to a
    // non-positional voice. FS.GG.Audio.Engine consumes all three itself (it pushes realized gains
    // through IMixingBackend.SetBusGain/PlayAt and never forwards these to Play), so this is true
    // only of effects that reached a backend directly.
    let requiresEngine (effect: AudioEffect) : bool =
        match effect with
        | SetBusVolume _
        | Duck _
        | PlaySfx3D _ -> true
        | PlaySfx _
        | PlayMusic _
        | StopMusic
        | SetMasterVolume _ -> false

    // Hidden by Host.fsi. Once per process, not per batch: a slider drag emits a dropped
    // SetBusVolume every frame, and a per-batch warning would bury the message it is delivering.
    let mutable private warnedRawDrop = false

    let play (backend: IAudioBackend) (effects: AudioEffect list) : unit =
        // The drop used to be silent, which is the whole of #27: the effect is a well-formed value,
        // the sink accepts it, and nothing happens — no error, no diagnostic, no type error. Say so
        // once, and name the surface that does realize it.
        //
        // Every raw drive in the org funnels through here — Elmish's Audio.Cmd.ofEffects delegates
        // to this rather than re-driving the backend itself (#29), which is what earns it this
        // diagnostic. So the message must NOT name one caller: it describes what happened (a batch
        // reached IAudioBackend directly) and names the destination on BOTH surfaces, because the
        // one latch below fires for whichever gets there first and the reader needs THEIR fix. An
        // Elmish product told to "use Engine.createSink" would be reading about a function it has no
        // reason to call.
        // MODAL, not past-tense: this fires for a batch carrying ANY requiresEngine effect, so it
        // cannot say which ones were in it. "SetBusVolume/Duck were dropped" would tell a caller
        // whose batch held only a PlaySfx3D that two effects it never sent had been discarded — a
        // diagnostic asserting something that did not happen, which is the same species of lie as
        // the silence this family exists to end (#27/#28/#33). Describe what the PATH does.
        if not warnedRawDrop && List.exists requiresEngine effects then
            warnedRawDrop <- true
            eprintfn
                "FS.GG.Audio: a batch reached IAudioBackend directly, which has no mixer and no clock — this path DROPS SetBusVolume/Duck and degrades PlaySfx3D to non-positional, so a volume slider wired this way does nothing. FS.GG.Audio.Engine realizes them: build the sink with Engine.createSink (raw host drive), or return Audio.Cmd.ofEngine instead of Audio.Cmd.ofEffects (Elmish). Keep the raw path for deliberate fire-and-forget playback."
        for effect in effects do
            backend.Play effect

[<RequireQualifiedAccess>]
module NullBackend =

    [<Sealed>]
    type T(silence: Silence) =
        let mutable evidence = CoreAudio.emptyEvidence
        // The default is `Requested`: `create ()` is the product/test/CI backend asking for record-only
        // on purpose. Only `OpenAlBackend.create` builds one with `DeviceUnavailable`, and it is the
        // only caller that should — a substitution nobody asked for is the whole of #34.
        new() = new T(Silence.Requested)
        member _.Silence = silence
        member _.Evidence = evidence
        interface IAudioBackend with
            member _.Play(effect: AudioEffect) =
                // Record-only: identical folding to Core.Audio.interpret, one effect at a time.
                evidence <- CoreAudio.record effect evidence
            member _.Dispose() = ()

    let create () = new T()

// The real OpenAL device backend. All device interaction is isolated here and fully guarded, so a
// missing device/native library never escapes as an exception into game code (FR-004).
module private OpenAl =

    open Silk.NET.OpenAL
    open Microsoft.FSharp.NativeInterop

    let private bufferFormat channels bits =
        match channels, bits with
        | 1, 8 -> Some BufferFormat.Mono8
        | 1, 16 -> Some BufferFormat.Mono16
        | 2, 8 -> Some BufferFormat.Stereo8
        | 2, 16 -> Some BufferFormat.Stereo16
        | _ -> None

    // A real device backend. Construction opens the device/context; failure throws and the caller
    // (create, below) degrades to Null. Per-effect Play is guarded so a runtime device error is a
    // no-op, never a throw.
    type Backend(resolver: AssetResolver) =
        let al = AL.GetApi(false)
        let alc = ALContext.GetApi(false)
        let device = alc.OpenDevice("")
        do if NativePtr.toNativeInt device = 0n then failwith "OpenAL: no output device"
        let context = alc.CreateContext(device, NativePtr.nullPtr)
        do
            if NativePtr.toNativeInt context = 0n then
                alc.CloseDevice device |> ignore
                failwith "OpenAL: could not create context"
        do alc.MakeContextCurrent context |> ignore

        // Decoded buffers are uploaded once per id and reused (#20): id-keyed caches replace the old
        // parse-and-GenBuffer-on-every-play path. Sounds and tracks live in separate id-typed caches.
        let soundBuffers = BufferCache.T<SoundId>()
        let trackBuffers = BufferCache.T<TrackId>()

        // The music voice is long-lived (it loops and its gain is driven by bus fades), so it is
        // tracked on its own and never pooled.
        let mutable musicSource : uint option = None

        // A one-shot voice has finished — and is reclaimable — once its source reaches Stopped.
        let sourceStopped (src: uint) : bool =
            let mutable state = 0
            al.GetSourceProperty(src, GetSourceInteger.SourceState, &state)
            state = int SourceState.Stopped

        // OpenAL Soft exposes a finite source ceiling (commonly ~256). Stay below it with headroom
        // for the music voice and any driver-reserved sources; past this the pool steals the oldest.
        let oneShotCeiling = 240

        // One-shot voices are pooled so finished sources are reclaimed instead of leaked (#20).
        let voices =
            VoicePool.T(
                { Gen = (fun () -> al.GenSource())
                  IsStopped = sourceStopped
                  Stop = (fun (s: uint) -> al.SourceStop s)
                  Delete = (fun (s: uint) -> al.DeleteSource s) },
                oneShotCeiling)

        // Log the source-ceiling hit once (not per play), so exhaustion is visible not silent (#20).
        let mutable ceilingLogged = false

        // Realized bus gains, as pushed by IMixingBackend.SetBusGain. Engine folds Sfx*Master into
        // each one-shot's gain before PlayAt, but it forwards PlayMusic to `Play` un-scaled — so the
        // music voice is the one thing the backend must scale itself, and it is the only reader of
        // this table. Master must NOT reach the listener gain here: that would apply it a second
        // time to voices whose gain already carries it.
        let busGains = Collections.Generic.Dictionary<Bus, float>()
        do for b in [ Master; Music; Sfx; Ui; Ambient ] do busGains.[b] <- 1.0

        let musicGain () = float32 (CoreAudio.clampVolume (busGains.[Music] * busGains.[Master]))

        // Last gain written to the music source. The Engine re-pushes every bus on every frame, so
        // without this a steady mix still costs two native writes per frame. A frame that does move
        // the gain writes twice (Master is pushed before Music, so the first write carries the
        // previous frame's Music), which is inaudible: both land before the Engine returns and the
        // device renders. A negative sentinel cannot equal a clamped gain, so the first write always
        // lands.
        let mutable appliedMusicGain = -1.0f

        // Push the current Music*Master gain at the playing music source, if it has moved. Nothing
        // else writes that source's gain, so the memo cannot go stale. Returns whether it actually
        // wrote to the device (#33): with no music playing, or a gain that has not moved, this is a
        // pure no-op and says nothing about whether the hardware is still answering.
        let applyMusicGain () : bool =
            let gain = musicGain ()
            match musicSource with
            | Some src when gain <> appliedMusicGain ->
                al.SetSourceProperty(src, SourceFloat.Gain, gain)
                appliedMusicGain <- gain
                true
            | _ -> false

        // A missing or unplayable asset used to be pure silence (#28): both lookups below ended in a
        // bare `None`, so a typo'd or unshipped id played nothing and reported nothing, and a caller
        // could not tell "played" from "your asset is missing". Name it once per id, on the channel
        // this file already warns on (the voice ceiling above, OpenAL-unavailable in `create`).
        // `fun line -> eprintfn ...` and NOT the shorter `AssetDiagnostics.T(eprintfn "%s")`: the
        // partial application would bind Console.Error ONCE, here, pinning the latch to the writer
        // that existed at construction — so a later Console.SetError (how this suite captures
        // stderr) would never see the line. Every other eprintfn in this file is a direct call and
        // re-reads the writer; this matches them.
        let assetDiagnostics = AssetDiagnostics.T(fun line -> eprintfn "%s" line)

        // Every device call below used to end in a bare `with _ -> ()` (#33): a throw from the driver
        // was swallowed with nothing on any channel, so a device that had genuinely died was
        // indistinguishable from a game that happened to be quiet. The degrade is UNCHANGED — a fault
        // is still silence, never a throw into game code (FR-004, Principle VIII) — but it is no
        // longer anonymous. Same writer discipline as `assetDiagnostics` above, and for the same
        // reason: a lambda, not a partial application, so a later Console.SetError still sees it.
        let deviceDiagnostics = DeviceDiagnostics.T(fun line -> eprintfn "%s" line)

        // One guarded device call: run `action`, degrade a throw to silence, and route it through the
        // latch — which names the fault once, and names it once more if the operation keeps failing
        // and so is not a hiccup.
        //
        // `action` returns whether it actually REACHED the device. That distinction is load-bearing,
        // not bookkeeping: plenty of calls through here touch no hardware at all (a `Duck` the raw
        // path drops, a bus gain no music source is listening to, a one-shot whose asset never
        // resolved). Such a call completing is no evidence the device is alive, so it must NOT end a
        // fault run — otherwise a dead device is quietly "recovered" by the very calls that never
        // asked it for anything, and the persistent fault is never reported.
        let guarded (op: DeviceDiagnostics.Operation) (action: unit -> bool) : unit =
            try
                if action () then deviceDiagnostics.Succeeded op
            with error ->
                deviceDiagnostics.Report(op, error)

        // Decode + upload a WAV to a fresh AL buffer. Callers reach this only through the id-keyed
        // caches below, so a given asset is parsed and uploaded at most once. Failure says WHICH
        // step failed, so the diagnostic can name the fix rather than just the silence (#28).
        let uploadBuffer (bytes: byte[]) : Result<uint, AssetDiagnostics.Failure> =
            match Wav.tryParse bytes with
            | None -> Error(AssetDiagnostics.NotWav bytes.Length)
            | Some pcm ->
                match bufferFormat pcm.Channels pcm.BitsPerSample with
                | None -> Error(AssetDiagnostics.UnsupportedFormat(pcm.Channels, pcm.BitsPerSample))
                | Some fmt ->
                    let buf = al.GenBuffer()
                    al.BufferData(buf, fmt, pcm.Data, pcm.SampleRate)
                    Ok buf

        // Resolve -> decode -> upload, reporting whichever step failed. Shared by both caches so a
        // missing track is exactly as loud as a missing sound.
        let resolveBuffer (asset: AssetDiagnostics.Asset) (resolve: unit -> byte[] option) : uint option =
            match resolve () with
            | None ->
                assetDiagnostics.Report(asset, AssetDiagnostics.Unresolved)
                None
            | Some bytes ->
                match uploadBuffer bytes with
                | Ok buf -> Some buf
                | Error failure ->
                    assetDiagnostics.Report(asset, failure)
                    None

        // The cached buffer for a sound/track id, uploaded once on first use (resolve failures are
        // not cached, so a later successful resolve still populates the entry — which is precisely
        // why the diagnostic latches per id rather than printing from this leg).
        let soundBuffer (sound: SoundId) : uint option =
            soundBuffers.GetOrAdd(
                sound,
                fun () -> resolveBuffer (AssetDiagnostics.Sound sound) (fun () -> resolver.ResolveSound sound))

        let trackBuffer (track: TrackId) : uint option =
            trackBuffers.GetOrAdd(
                track,
                fun () -> resolveBuffer (AssetDiagnostics.Track track) (fun () -> resolver.ResolveTrack track))

        // Point an already-allocated source at `buf` and (re)start it. Every source is
        // listener-relative with the distance model switched off, so its position is read as a pure
        // direction and the gain we pass is the gain that plays. `position` is None for a
        // non-positional voice, which then sits exactly at the listener (dead centre, unattenuated).
        // The source must be Stopped before this reassigns its buffer — the pool (reclaimed/stolen
        // voices) and the music path both guarantee that. OpenAL spatializes mono buffers only — a
        // stereo asset plays centred whatever we set here.
        let configureAndPlay (src: uint) (buf: uint) (loop: bool) (gain: float32) (position: (float * float * float) option) : unit =
            al.SetSourceProperty(src, SourceInteger.Buffer, int buf)
            al.SetSourceProperty(src, SourceBoolean.Looping, loop)
            al.SetSourceProperty(src, SourceFloat.Gain, gain)
            al.SetSourceProperty(src, SourceBoolean.SourceRelative, true)
            al.SetSourceProperty(src, SourceFloat.RolloffFactor, 0.0f)
            let (px, py, pz) = defaultArg position (0.0, 0.0, 0.0)
            al.SetSourceProperty(src, SourceVector3.Position, float32 px, float32 py, float32 pz)
            al.SourcePlay src

        // Play a resolved one-shot on a pooled voice; `position` None => non-positional. Returns
        // whether the voice reached the device — an id that resolves to nothing never gets near the
        // hardware, so it is not evidence the hardware is alive (#33).
        let playOneShot (sound: SoundId) (gain: float32) (position: (float * float * float) option) : bool =
            match soundBuffer sound with
            | Some buf ->
                configureAndPlay (voices.Acquire()) buf false gain position
                if voices.HasStolen && not ceilingLogged then
                    ceilingLogged <- true
                    eprintfn
                        "FS.GG.Audio.Host: OpenAL one-shot source ceiling (%d) reached; stealing the oldest voice per play — overlapping sounds will be dropped."
                        oneShotCeiling
                true
            // Still a no-op — there is nothing to play — but no longer a *silent* one: soundBuffer
            // reported the id and the reason on its way to None (#28).
            | None -> false

        interface IAudioBackend with
            member _.Play(effect: AudioEffect) =
                // Safe failure (Principle VIII): a device fault degrades to silence, not a crash —
                // and `guarded` now says so rather than swallowing it whole (#33).
                guarded DeviceDiagnostics.Play (fun () ->
                    match effect with
                    // A PlaySfx3D that arrives here was dispatched straight at the backend rather
                    // than through FS.GG.Audio.Engine, so nothing has spatialized it: the position
                    // is in the product's world frame and the backend has no listener to relate it
                    // to. Degrade to a non-positional one-shot at the carried gain (004). Driven by
                    // the Engine, 3D voices arrive pre-spatialized through PlayAt below.
                    | PlaySfx(sound, volume)
                    | PlaySfx3D(sound, _, _, _, volume) -> playOneShot sound (float32 volume) None
                    // Bus mixing / ducking are envelopes over time: the raw backend has no clock, so
                    // FS.GG.Audio.Engine advances them and pushes the realized gains through
                    // IMixingBackend.SetBusGain below. They touch no hardware, hence `false`.
                    | SetBusVolume _
                    | Duck _ -> false
                    | PlayMusic(track, loop) ->
                        match trackBuffer track with
                        | Some buf ->
                            // Reuse the existing music handle across track changes rather than genning
                            // a fresh source each time (the old path leaked the previous music source
                            // on every PlayMusic, #20).
                            let src =
                                match musicSource with
                                | Some s ->
                                    al.SourceStop s
                                    s
                                | None -> al.GenSource()
                            let gain = musicGain ()
                            configureAndPlay src buf loop gain None
                            musicSource <- Some src
                            appliedMusicGain <- gain
                            true
                        // As in playOneShot: nothing to play, but trackBuffer has already named the
                        // track and the reason (#28). The music voice is left exactly as it was — a
                        // missing new track does not stop the track that is playing.
                        | None -> false
                    | StopMusic ->
                        // Stopping music that is not playing reaches no device.
                        let playing = musicSource.IsSome
                        musicSource |> Option.iter (fun s -> al.SourceStop s; al.DeleteSource s)
                        musicSource <- None
                        appliedMusicGain <- -1.0f
                        playing
                    | SetMasterVolume level ->
                        al.SetListenerProperty(ListenerFloat.Gain, float32 level)
                        true)

            member _.Dispose() =
                try
                    voices.DisposeAll()
                    musicSource |> Option.iter (fun s -> al.SourceStop s; al.DeleteSource s)
                    for buf in soundBuffers.Handles do al.DeleteBuffer buf
                    for buf in trackBuffers.Handles do al.DeleteBuffer buf
                    alc.DestroyContext context
                    alc.CloseDevice device |> ignore
                    al.Dispose()
                    alc.Dispose()
                with _ -> ()

        interface IMixingBackend with
            member _.SetBusGain(bus: Bus, gain: float) =
                guarded DeviceDiagnostics.SetBusGain (fun () ->
                    busGains.[bus] <- gain
                    // Only the music voice is left for the backend to scale, and it is long-lived:
                    // a fade or a duck has to reach the source that is already playing. The one-shot
                    // buses (Sfx, Ui, Ambient) are folded into each voice's gain before PlayAt — so
                    // this writes to the device only for Music/Master, and only when music is up and
                    // the gain has actually moved.
                    (bus = Music || bus = Master) && applyMusicGain ())

            member _.SetListener(x: float, y: float, z: float) =
                guarded DeviceDiagnostics.SetListener (fun () ->
                    // Mirrored into the device for truthfulness, though today's sources are all
                    // listener-relative and so read positions as directions, ignoring it. The
                    // Engine's listener remains the single source of truth for attenuation and pan.
                    //
                    // Unconditional, so it is the ONE call the Engine makes at the device every
                    // single frame — which makes it the leg that reliably catches a dead device.
                    al.SetListenerProperty(ListenerVector3.Position, float32 x, float32 y, float32 z)
                    true)

            member _.PlayAt(sound: SoundId, gain: float, pan: float) =
                guarded DeviceDiagnostics.PlayAt (fun () ->
                    playOneShot sound (float32 gain) (Some(Spatial.panToPosition pan)))

[<RequireQualifiedAccess>]
module OpenAlBackend =

    let create (resolver: AssetResolver) : IAudioBackend =
        try
            new OpenAl.Backend(resolver) :> IAudioBackend
        with ex ->
            // Degrade-to-zero (FR-004) is UNCHANGED and not in question: no device / no native library
            // must never crash game code. What changed is that the substitution is no longer only a
            // line on a channel nobody reads (#34). It is carried IN the returned value — ask
            // `Backend.kindOf` / `Backend.isDeviceBacked` — because a single print at construction is
            // not a sufficient account of "nothing this process plays will ever be audible": it scrolls
            // past, most shipped games never surface stderr, and a CI suite cannot branch on a log line.
            // Addressed to whoever reads a shipped game's stderr, so it says what happened and the one
            // thing to do about it. How to write a TEST against this lives in the `.fsi` doc, where its
            // audience actually is — not here, in an end user's support log.
            eprintfn
                "FS.GG.Audio.Host: OpenAL unavailable (%s) — using the record-only Null backend, so NOTHING this process plays will be audible. This is the deliberate degrade (FR-004), not a crash. Do not rely on seeing this line: ask Backend.isDeviceBacked and surface it in your own UI."
                ex.Message
            new NullBackend.T(Silence.DeviceUnavailable ex.Message) :> IAudioBackend

[<RequireQualifiedAccess>]
module Backend =

    // What did I actually get? (#34) Before this, the only way to know was `:? NullBackend.T` — a type
    // test against the fallback, awkward and silent about WHY. So nothing asked, and two things went
    // unnoticed: a shipped game running record-only because a machine had no sound card, and — the
    // serious one — a headless CI suite asserting playback against a recorder, green *because* nothing
    // played. A gate that reports green on a subject it never constructed is reporting on nothing.
    //
    // Both known backends are matched POSITIVELY, and anything else is `Unknown` rather than assumed
    // audible. The tempting `| _ -> DeviceBacked` is the unsafe default: every record-only test fake
    // in this repo (and every product's own stub) is an `IAudioBackend` that is not `NullBackend.T`,
    // so it would report `DeviceBacked`, and the guard below would wave it through as audible — which
    // is exactly the vacuous green this whole item exists to kill, reintroduced by its own fix.
    // Unknown ⇒ "I cannot vouch for this", never "sure, it plays".
    //
    // Total and non-throwing, so a test can branch on it in the same breath as building the backend.
    let kindOf (backend: IAudioBackend) : BackendKind =
        match backend with
        | :? OpenAl.Backend -> BackendKind.DeviceBacked
        | :? NullBackend.T as nb -> BackendKind.RecordOnly nb.Silence
        | _ -> BackendKind.Unknown

    // True ONLY for a backend this library opened a device for. The predicate a CI suite branches on
    // to SKIP (loudly, `Ignored`) rather than assert vacuously.
    //
    // `Unknown` is false, deliberately: a custom backend may well drive real hardware, but this library
    // did not build it and cannot say — and the caller who DID build it already knows what it is, so it
    // has no reason to ask. Guessing `true` here would be the fail-open answer, and it is the one that
    // lets a record-only fake pass for a device.
    //
    // It says nothing about whether the device is *currently* answering: a real device that has since
    // died is still `DeviceBacked`, and #33's DeviceDiagnostics is what reports that. This answers
    // "was a device ever opened", which is the question a test must settle before trusting itself.
    let isDeviceBacked (backend: IAudioBackend) : bool =
        match kindOf backend with
        | BackendKind.DeviceBacked -> true
        | BackendKind.RecordOnly _
        | BackendKind.Unknown -> false
