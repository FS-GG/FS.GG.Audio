namespace FS.GG.Audio.Host

open System
open FS.GG.Audio.Core

/// Public contract type. Caller-supplied resolution of product-owned ids to PCM (WAV) bytes.
/// The host does NOT own the id -> asset mapping (FR-005); a product supplies these functions.
///
/// `None` => unresolved: the host treats it as a no-op, never a throw — the id plays as SILENCE.
/// That is a real failure mode and the one a game developer actually hits (a typo'd id, an asset
/// that was never shipped), so the device backend does not swallow it: it names the id and the
/// reason once, on stderr (#28). See `AssetDiagnostics`. The Null backend records the request
/// regardless — it resolves nothing, so its evidence says "requested", never "audible".
type AssetResolver =
    { ResolveSound: SoundId -> byte[] option
      ResolveTrack: TrackId -> byte[] option }

/// Public contract type. The narrow device seam (FR-001). Implementations: the Null/record
/// backend (default, deterministic) and the OpenAL backend (Silk.NET). Game-facing code holds an
/// IAudioBackend and never names a concrete backend type.
type IAudioBackend =
    inherit IDisposable
    /// Realize one requested effect. Volumes arrive already clamped by Core. Never throws; a
    /// backend that cannot act degrades to a no-op.
    abstract member Play: effect: AudioEffect -> unit

/// Public contract type (004-audio-engine). Optional mixing/spatial control a backend MAY
/// implement alongside `IAudioBackend`. `FS.GG.Audio.Engine` feature-detects it
/// (`:? IMixingBackend`); a backend that does not implement it degrades to plain `Play`, with
/// bus/fade/duck folded into one-shot gains and 3D collapsed to non-positional voices. Additive:
/// existing backends that implement only `IAudioBackend` stay valid.
type IMixingBackend =
    inherit IAudioBackend
    /// Set a bus's realized gain (already clamped to `[0,1]`), called as fades/ducks advance.
    abstract member SetBusGain: bus: Bus * gain: float -> unit
    /// Set the listener position in metres.
    abstract member SetListener: x: float * y: float * z: float -> unit
    /// Play a positional one-shot with a pre-resolved effective gain and pan in `[-1, 1]`.
    abstract member PlayAt: sound: SoundId * gain: float * pan: float -> unit

/// Public contract type (#34). WHY a backend makes no sound — and the distinction is the point.
///
/// A Null backend the product ASKED for is correct and expected: it is the default, and the test/CI
/// backend (FR-002). One that `OpenAlBackend.create` SUBSTITUTED, because no device would open, is a
/// game running silently. At the `IAudioBackend` seam the two are indistinguishable — both satisfy
/// the interface, both accept every effect, both play none — which is exactly how a shipped game
/// comes to run record-only, and how a CI suite comes to assert playback against a recorder.
[<RequireQualifiedAccess>]
type Silence =
    /// `NullBackend.create` — record-only, chosen deliberately (FR-002).
    | Requested
    /// `OpenAlBackend.create` could not open a device and degraded to Null (FR-004). Carries the
    /// device's own account of why, which is the only one anyone will get.
    | DeviceUnavailable of reason: string

/// Public contract type (#34). What a caller is actually HOLDING, asked without a type test and
/// without exception handling. Before this, the only way to know was `:? NullBackend.T`, so nothing
/// asked.
[<RequireQualifiedAccess>]
type BackendKind =
    /// The bundled OpenAL backend, with a device actually open.
    | DeviceBacked
    /// A record-only backend: it accepts every effect and plays none. `Silence` says why.
    | RecordOnly of Silence
    /// An `IAudioBackend` this library did not build — a product's own backend, or a test fake.
    /// Neither `DeviceBacked` nor `RecordOnly`: this library cannot say whether it reaches a device,
    /// and will not guess that it does. Whoever built it already knows what it is.
    | Unknown

/// Public contract module. A pure, total minimal PCM WAV reader (no device, no OpenAL types).
[<RequireQualifiedAccess>]
module Wav =

    /// The `wFormatTag` of uncompressed PCM — the only codec whose bytes mean, to OpenAL's
    /// `BufferData`, what `BufferData` assumes they mean. Compare `PcmData.FormatTag` against this
    /// before treating a parsed WAV as playable.
    [<Literal>]
    val FormatPcm: int = 1

    /// Decoded payload of a WAV file.
    type PcmData =
        { /// The `wFormatTag` from the `fmt ` chunk: which codec `Data` is actually in.
          /// `FormatPcm` (1) is the only one this component can play — see `tryParse`.
          ///
          /// Already resolved through `WAVE_FORMAT_EXTENSIBLE` (0xFFFE): a PCM file written in the
          /// extensible form — routine for multichannel exports — reports `FormatPcm` here, not
          /// 0xFFFE. It stays 0xFFFE only when the subformat GUID could not be read at all, which is
          /// not a claim that the file is PCM.
          FormatTag: int
          Channels: int
          BitsPerSample: int
          SampleRate: int
          Data: byte[] }

    /// Parse a minimal WAV (RIFF/WAVE, fmt + data chunks). Total; returns None on anything it does
    /// not understand rather than throwing, and terminates on any input — including a corrupt chunk
    /// size, which once made the walk spin forever.
    ///
    /// A STRUCTURAL parse: it reports what the header says and decides nothing about playability. A
    /// `Some` therefore does NOT mean the file can be played — a 5-channel 32-bit WAV parses here,
    /// and so does an IEEE-float one. Check `FormatTag` against `FormatPcm` before trusting `Data`
    /// to be PCM; the bundled OpenAL backend does, and reports `AssetDiagnostics.UnsupportedCodec`
    /// when it is not.
    val tryParse: bytes: byte[] -> PcmData option

/// Public contract module. The pure pan -> source-position mapping the OpenAL backend spatializes
/// through (#11). No device, no OpenAL types.
[<RequireQualifiedAccess>]
module Spatial =

    /// Map a stereo pan in `[-1, 1]` (as `IMixingBackend.PlayAt` carries it) to a source position in
    /// the listener's own frame: `-1` hard left, `0` dead ahead, `+1` hard right. Total — pan is
    /// clamped and `nan` centres. The result is always unit-length, which is what keeps a device's
    /// distance model from attenuating a gain `FS.GG.Audio.Engine` has already attenuated.
    val panToPosition: pan: float -> float * float * float

/// Public contract module. A device-free memo of uploaded buffer handles keyed by a product id
/// (`SoundId`/`TrackId`), so an asset is decoded and uploaded once rather than on every play (#20).
/// It holds only `uint` handles and a create-callback — no device, no OpenAL types — so it is
/// exercised headless.
[<RequireQualifiedAccess>]
module BufferCache =

    /// A memo of buffer handles keyed by `'k`.
    [<Sealed>]
    type T<'k when 'k: equality> =
        /// A fresh, empty cache.
        new: unit -> T<'k>
        /// The cached handle for `key`, created once via `create` on first miss. A `None` from
        /// `create` (unresolved / unparseable asset) is NOT cached, so a later successful resolve of
        /// the same id can still populate the entry.
        member GetOrAdd: key: 'k * create: (unit -> uint option) -> uint option
        /// Number of distinct handles held (one per successfully uploaded id).
        member Count: int
        /// Every cached handle, for deletion when the backend is disposed.
        member Handles: uint[]

/// Public contract module. A device-free, bounded pool of one-shot voice handles that reclaims
/// finished voices instead of leaking them (#20): the OpenAL backend used to allocate a source per
/// one-shot and never delete it, so a long session exhausted the source ceiling and `Play` then
/// failed silently. The pool takes its device operations as callbacks, so its reclaim/steal logic
/// runs headless behind counting fakes.
[<RequireQualifiedAccess>]
module VoicePool =

    /// The device operations a pool drives, named so the two `uint -> unit` handle operations cannot
    /// be transposed. In the OpenAL backend: `GenSource`, a `SourceState = Stopped` test,
    /// `SourceStop`, and `DeleteSource`.
    type Ops =
        { /// Allocate a fresh source handle.
          Gen: unit -> uint
          /// True once a handed-out voice has finished (is reclaimable).
          IsStopped: uint -> bool
          /// Stop a still-sounding voice so its handle can be reused or deleted.
          Stop: uint -> unit
          /// Release a handle for good.
          Delete: uint -> unit }

    /// A bounded pool of one-shot voice handles.
    [<Sealed>]
    type T =
        /// A pool driven by `ops`, holding at most `ceiling` live handles before it steals the
        /// oldest still-sounding voice.
        new: ops: Ops * ceiling: int -> T
        /// A source handle ready to be configured and played: reclaims finished voices, reuses a
        /// free handle when one exists, grows up to `ceiling`, and past it steals the oldest voice.
        member Acquire: unit -> uint
        /// Voices handed out and presumed still sounding.
        member ActiveCount: int
        /// Reclaimed handles available for reuse.
        member FreeCount: int
        /// True once the ceiling has forced at least one oldest-voice steal.
        member HasStolen: bool
        /// Stop and delete every handle the pool owns.
        member DisposeAll: unit -> unit

/// Public contract module (#28). The missing-asset diagnostic. A `SoundId`/`TrackId` that does not
/// become a playable buffer used to be pure silence: the device backend's `None` legs were a bare
/// `()`, so a typo'd or unshipped asset played nothing and reported nothing, and a caller could not
/// tell "played" from "your asset is missing". It is now named — the id, the reason, and the fix —
/// once per id.
///
/// Device-free (it holds product ids and an emit callback, no OpenAL types), which is what lets the
/// failure leg be exercised headless: the backend that hits it in anger needs a real device.
[<RequireQualifiedAccess>]
module AssetDiagnostics =

    /// Why an id produced no playable buffer. Distinct fixes, so the diagnostic names which one
    /// rather than just reporting silence.
    type Failure =
        /// The product's `AssetResolver` returned `None` — the id names nothing the product could
        /// hand over. The headline case: a typo'd id, or an asset that was never shipped.
        | Unresolved
        /// Bytes came back, but they are not a WAV `Wav.tryParse` can read at all. An authoring or
        /// export problem, not a missing file.
        | NotWav of bytes: int
        /// A WAV that parsed, whose channel/bit-depth pair has no OpenAL buffer format — only
        /// 8-/16-bit mono and stereo do. A conversion problem.
        | UnsupportedFormat of channels: int * bitsPerSample: int
        /// A WAV that parsed, in a codec that is not uncompressed PCM (IEEE-float, ADPCM, A-law,
        /// MP3-in-WAV, …). Carries the `wFormatTag`, resolved through `WAVE_FORMAT_EXTENSIBLE`.
        ///
        /// Its own case rather than a reuse of `UnsupportedFormat`, which reports channels and bits:
        /// mono 16-bit IMA-ADPCM has a channel/bit pair OpenAL supports perfectly well, so that
        /// message would misname the cause and send the reader to re-export at a bit depth that was
        /// never the problem. The bytes are wrong, not their shape.
        ///
        /// The one failure in this component whose degrade is not merely silence-instead-of-sound:
        /// before it was checked, these bytes were uploaded AS PCM and played as noise.
        | UnsupportedCodec of formatTag: int

    /// The asset that failed, carrying the product's own id — the only handle the host has on it.
    type Asset =
        | Sound of SoundId
        | Track of TrackId

    /// The diagnostic line for one failure: the id, the consequence (silence), and the thing the
    /// product actually controls. Pure and total — a value rather than a print, so a test asserts on
    /// it directly.
    ///
    /// It deliberately does NOT name a file path: the host does not own the id -> asset mapping
    /// (FR-005), so no path exists at this layer to name. It names the `AssetResolver` function that
    /// returned `None` instead — that closure is where the product's mapping lives, and it is the
    /// thing the reader has to go and look at.
    val message: asset: Asset -> failure: Failure -> string

    /// A warn-once-per-id latch over `message`.
    [<Sealed>]
    type T =
        /// A latch emitting through `emit` — the OpenAL backend passes stderr (the channel it
        /// already warns on), a test passes a buffer.
        new: emit: (string -> unit) -> T
        /// Report that `asset` failed with `failure`: emits `message` the FIRST time this id is
        /// reported, and never again for that id.
        ///
        /// Warn-once is load-bearing, not politeness. A failed resolve is deliberately NOT cached
        /// (`BufferCache.GetOrAdd`, so a later successful resolve still populates the entry), which
        /// means the failure leg is re-entered on EVERY play of the missing id — printing from
        /// there would emit a line per frame for a cue the product retriggers, burying the message
        /// it is delivering.
        member Report: asset: Asset * failure: Failure -> unit
        /// Distinct ids reported so far — one emitted line each.
        member ReportedCount: int

/// Public contract module (#33). The device-fault diagnostic. Every guarded device call — the
/// backend's `Play`/`PlayAt`/`SetBusGain`/`SetListener`, and `FS.GG.Audio.Engine`'s per-frame realize
/// pass — used to end in a bare `with _ -> ()`. Degrading a fault to silence is correct and stays
/// (FR-004, Principle VIII: a device hiccup must never crash a game), but it left a *persistent*
/// fault — the device unplugged, the driver dead — indistinguishable from a game that is simply
/// quiet: the process keeps running, the mixer keeps advancing envelopes, every frame is dropped,
/// and nothing on any channel ever says so.
///
/// The fix is a message, not a throw. A fault run is counted per operation; the first fault is named
/// once, and once the run is long enough that "hiccup" is no longer credible it is named once more,
/// as what it is. A success ENDS the run — a device that fails and recovers is the transient case the
/// degrade exists for, and is never escalated.
///
/// Device-free (it holds operation names, counters and an emit callback — no OpenAL types), which is
/// what lets both failure legs be exercised headless: the backend that faults in anger needs a real
/// device to construct.
[<RequireQualifiedAccess>]
module DeviceDiagnostics =

    /// The guarded device call that threw. Fault runs are counted per operation: a device still
    /// answering `SetBusGain` while it fails every `PlayAt` is not the same as one that has stopped
    /// answering at all, and a working leg's success must not mask a dead leg's run.
    type Operation =
        /// `FS.GG.Audio.Engine`'s per-frame realize pass — the whole batch of device calls a `step`
        /// makes. This is the leg that fails in production, on a player's machine.
        | Realize
        /// `IAudioBackend.Play`.
        | Play
        /// `IMixingBackend.PlayAt`.
        | PlayAt
        /// `IMixingBackend.SetBusGain`.
        | SetBusGain
        /// `IMixingBackend.SetListener`.
        | SetListener

    /// How bad a fault is, which is entirely a question of whether it has stopped.
    type Fault =
        /// A first, isolated fault. Degrading it to silence is right — this is the hiccup the
        /// guard exists for — so the diagnostic says so and does not cry wolf.
        | Transient
        /// A run of `consecutive` faults with no intervening success: long enough that a driver
        /// glitch would have cleared. The device is gone and playback is silent from here.
        | Persistent of consecutive: int

    /// The run length at which a fault stops being a hiccup and is reported as persistent. The engine
    /// realizes once per frame, so at 60 fps this is roughly a second of unbroken failure.
    [<Literal>]
    val DefaultPersistentAfter: int = 60

    /// The diagnostic line for one fault: the operation, the device's own exception text, the
    /// consequence (silence), and — for a persistent fault — what to do about it. Pure and total: a
    /// value rather than a print, so a test asserts on it.
    val message: op: Operation -> fault: Fault -> error: exn -> string

    /// The retraction line for an operation that has come back after a persistent fault. A device CAN
    /// recover — a headset reconnects, a driver restarts — and the `Persistent` line is then left
    /// standing in the log as a claim that is no longer true. An unretracted diagnostic is the same
    /// species of lie as an absent one, so recovery is reported too. Pure and total.
    val recovered: op: Operation -> afterConsecutive: int -> string

    /// A warn-once-per-operation latch over `message`/`recovered`, tracking each operation's run of
    /// consecutive faults.
    [<Sealed>]
    type T =
        /// A latch emitting through `emit`, escalating to `Persistent` after `persistentAfter`
        /// consecutive faults on one operation. The OpenAL backend and the Engine pass stderr (the
        /// channel they already warn on); a test passes a buffer, and a product can pass its own log.
        new: emit: (string -> unit) * persistentAfter: int -> T
        /// As above, with `DefaultPersistentAfter`.
        new: emit: (string -> unit) -> T
        /// Report that `op` threw. Emits the first-fault line the FIRST time `op` faults, and the
        /// persistent-fault line once `op`'s run reaches `persistentAfter` — each at most once, ever.
        ///
        /// Warn-once is load-bearing, not politeness: these legs are re-entered on every frame, so
        /// printing from them would emit a line per frame, for as long as the game runs, about a
        /// device that is dead and is going to stay dead.
        member Report: op: Operation * error: exn -> unit
        /// Report that `op` REACHED THE DEVICE and completed, ending its fault run — and, if it had
        /// been reported persistent, emitting the retraction once. Without this a device that glitches
        /// once an hour would eventually be declared dead.
        ///
        /// Call this ONLY for a call that actually touched the hardware. A no-op that reached no
        /// device — a `Duck` the raw path drops, a bus gain with no music playing, a one-shot whose
        /// asset never resolved, a frame carrying no audio at all — says nothing about whether the
        /// device is alive. Passing one here would let a dead device be silently "recovered" by calls
        /// that never asked it for anything, and its persistent fault would then never be reported:
        /// a game that plays a sound every few frames rather than every frame would never accumulate
        /// a run at all.
        member Succeeded: op: Operation -> unit
        /// `op`'s current run of consecutive faults — 0 once it has reached the device again.
        member ConsecutiveFaults: op: Operation -> int
        /// Whether `op` is faulting persistently RIGHT NOW: a live predicate, not a latch. This is the
        /// machine-readable form of "nothing this backend plays is audible" — and because a device
        /// that died and came back is not a dead device, it goes false again when the device answers.
        /// The warn-once behaviour belongs to the emitted lines, not to this.
        member IsPersistent: op: Operation -> bool
        /// Lines emitted so far: one per faulting operation, one per operation gone persistent, and
        /// one per operation that came back from it.
        member ReportedCount: int

/// Public contract module. The imperative drive (FR-006).
[<RequireQualifiedAccess>]
module Audio =

    /// Public contract function (#27). True for exactly the effects a RAW backend structurally cannot
    /// realize — `SetBusVolume` and `Duck` (no mixer and no clock on this path, so they are dropped)
    /// and `PlaySfx3D` (no listener to resolve the position against, so it degrades to
    /// non-positional). Pure and total.
    ///
    /// These are not errors: they are the effects that need `FS.GG.Audio.Engine` to mean anything.
    /// Driven by the Engine they never reach a backend as-is, so this predicate describes precisely
    /// what the raw path loses.
    val requiresEngine: effect: AudioEffect -> bool

    /// RAW drive. Fold a per-frame batch of requests through the backend in dispatch order. The
    /// product's `update` is unchanged: it emits AudioEffect values; this plays them.
    ///
    /// There is NO mixing here: an effect satisfying `requiresEngine` is discarded or degraded rather
    /// than realized (#27), so a volume slider built on this sink does nothing. The first batch that
    /// carries such an effect logs one diagnostic to stderr naming the surface that does realize it —
    /// `FS.GG.Audio.Engine`'s `Engine.createSink`, an `AudioEffect list -> unit` of this exact shape.
    /// Keep `play` for deliberate fire-and-forget playback.
    ///
    /// This is THE raw drive, not merely one of them: `FS.GG.Audio.Elmish`'s `Audio.Cmd.ofEffects`
    /// delegates here (#29), so the dispatch-order guarantee and the diagnostic are the same on both
    /// surfaces. The warn-once latch is therefore shared and process-wide, and the message names the
    /// engine-backed destination for each surface (`Engine.createSink` here, `Audio.Cmd.ofEngine` in
    /// Elmish) rather than assuming which one dropped the effect.
    val play: backend: IAudioBackend -> effects: AudioEffect list -> unit

/// Public contract module. The deterministic, headless record-only backend — the default and
/// the test/CI backend (FR-002).
[<RequireQualifiedAccess>]
module NullBackend =

    /// A record-only backend: opens no device, never throws.
    [<Sealed>]
    type T =
        interface IAudioBackend
        /// Accumulated evidence — equal to `FS.GG.Audio.Core.Audio.interpret` of the same batch.
        member Evidence: AudioEvidence
        /// Why this backend is silent (#34): `Requested` when the product built it on purpose,
        /// `DeviceUnavailable` when `OpenAlBackend.create` substituted it. Prefer `Backend.kindOf`,
        /// which answers the same question for ANY `IAudioBackend` without a type test.
        member Silence: Silence

    /// Create a fresh Null backend. Its `Silence` is `Requested` — this is the deliberate,
    /// record-only backend, never a substitution.
    val create: unit -> T

/// Public contract module. The real OpenAL device backend (Silk.NET.OpenAL) (FR-003, FR-004).
[<RequireQualifiedAccess>]
module OpenAlBackend =

    /// Attempt to open an OpenAL device and return a backend that plays through it. If the device
    /// or the OpenAL Soft native library is unavailable, log the reason and return a Null backend
    /// instead (degrade-to-zero, FR-004) — the returned IAudioBackend is always usable, never null,
    /// and never throws into game code.
    ///
    /// **That substitution is silent unless you ask (#34).** The returned value is an `IAudioBackend`
    /// either way, so a caller who does not check cannot tell a device from a tape recorder: a shipped
    /// game runs record-only, and a headless test suite asserts playback against a recorder and passes
    /// *because* nothing played. Ask `Backend.isDeviceBacked` (or `Backend.kindOf`, which also carries
    /// the device's reason) — in a product, to surface "no audio device" in its own UI rather than
    /// trusting stderr; in a test, to SKIP loudly rather than assert vacuously.
    ///
    /// The device backend also implements `IMixingBackend` (#11), so driven by `FS.GG.Audio.Engine`
    /// it spatializes: pan reaches the hardware, and bus fades/ducks reach the music voice. The Null
    /// fallback does not, which is exactly what makes the Engine take its non-positional degrade path
    /// on a machine with no device.
    /// Spatialization is per-source, so a positional sound must be a **mono** asset; OpenAL plays a
    /// stereo buffer centred, whatever position it is given.
    ///
    /// An id `resolver` cannot resolve — or resolves to bytes this backend cannot decode — plays as
    /// SILENCE (there is nothing to play), but not silently: the id and the reason are named once on
    /// stderr (#28, see `AssetDiagnostics`). Playback is otherwise untouched, and a missing track in
    /// particular does not stop the music already playing.
    val create: resolver: AssetResolver -> IAudioBackend

/// Public contract module (#34). Ask what a backend actually IS, without a type test and without
/// exception handling — so a caller can tell a real device from a Null that was substituted under it.
[<RequireQualifiedAccess>]
module Backend =

    /// What this backend is, and — if it is silent — why. Total and non-throwing.
    ///
    /// Both backends this library builds are matched positively; anything else is `Unknown`, never
    /// assumed `DeviceBacked`. A record-only test fake is an `IAudioBackend` that is not
    /// `NullBackend.T`, so a "not-Null means device" rule would wave it through as audible — the same
    /// vacuous green this type exists to prevent.
    val kindOf: backend: IAudioBackend -> BackendKind

    /// True ONLY for a backend this library opened a device for (`BackendKind.DeviceBacked`).
    ///
    /// This is the predicate a TEST SUITE must branch on before it trusts its own audio assertions.
    /// On a headless box `OpenAlBackend.create` degrades to Null, so a test that drives playback is
    /// asserting against a recorder and passes *because* nothing played. Skip loudly (`Ignored`)
    /// rather than assert vacuously — a green tick on a subject that was never constructed is
    /// reporting on nothing.
    ///
    /// `BackendKind.Unknown` is `false`, deliberately: a custom backend may well drive real hardware,
    /// but this library did not build it and cannot say. Guessing `true` is the fail-open answer, and
    /// it is the one that lets a record-only fake pass for a device. A caller who built its own backend
    /// knows what it is and has no reason to ask.
    ///
    /// It says nothing about whether the device is *currently* answering: a real device that has since
    /// died is still `DeviceBacked`, and `DeviceDiagnostics` (#33) is what reports that. This answers
    /// "was a device ever opened".
    val isDeviceBacked: backend: IAudioBackend -> bool
