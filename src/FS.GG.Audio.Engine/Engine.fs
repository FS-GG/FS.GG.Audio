namespace FS.GG.Audio.Engine

open System
open System.Collections.Generic
open FS.GG.Audio.Core
open FS.GG.Audio.Host

type SpatialConfig =
    { RefDistance: float
      Rolloff: float
      MaxDistance: float option }

type Voice =
    { Sound: SoundId
      Bus: Bus
      RequestGain: float
      EffectiveGain: float
      Pan: float
      Positional: bool }

// Internal envelope vocabulary — hidden from consumers by Engine.fsi.
type Curve =
    | Linear
    | EqualPowerOut
    | EqualPowerIn

type Fade =
    { mutable Elapsed: float
      Duration: float
      StartG: float
      EndG: float
      Curve: Curve }

type Duck =
    { mutable Elapsed: float
      Duration: float
      Amount: float }

[<Sealed>]
type T(config: SpatialConfig, backend: IAudioBackend, device: DeviceDiagnostics.T) =
    let buses = [ Master; Music; Sfx; Ui; Ambient ]
    let baseGain = Dictionary<Bus, float>()
    do for b in buses do baseGain.[b] <- 1.0
    let fades = Dictionary<Bus, Fade>()
    let ducks = Dictionary<Bus, Duck>()
    let mutable listener = (0.0, 0.0, 0.0)
    let mutable music: (TrackId * bool) option = None
    let mutable lastVoices: Voice list = []
    let mixing = match backend with :? IMixingBackend as m -> Some m | _ -> None

    let clamp01 (v: float) =
        if v <= 0.0 || Double.IsNaN v then 0.0 elif v >= 1.0 then 1.0 else v

    // NaN-total, like clamp01 above, and for the same reason: `nan < -1.0` and `nan > 1.0` are BOTH
    // false, so the obvious version hands `nan` straight back — and a `nan` Pan breaks the `[-1, 1]`
    // the .fsi promises for it. nan folds to 0.0 = dead centre, which is the same answer
    // `Host.Spatial.panToPosition` already gives a nan pan, so the two agree on nonsense input
    // instead of one of them quietly relying on the other to clean up.
    let clampUnit (v: float) =
        if Double.IsNaN v then 0.0
        elif v < -1.0 then -1.0
        elif v > 1.0 then 1.0
        else v

    let applyCurve curve (startG: float) (endG: float) (p: float) =
        let p = if p < 0.0 then 0.0 elif p > 1.0 then 1.0 else p
        match curve with
        | Linear -> startG + (endG - startG) * p
        | EqualPowerOut -> startG * cos (p * Math.PI / 2.0)
        | EqualPowerIn -> endG * sin (p * Math.PI / 2.0)

    // Base gain of a bus, shaped by an active fade if present.
    let baseOf (bus: Bus) =
        match fades.TryGetValue bus with
        | true, f -> applyCurve f.Curve f.StartG f.EndG (if f.Duration <= 0.0 then 1.0 else f.Elapsed / f.Duration)
        | _ -> baseGain.[bus]

    // Triangular dip: 1 at the ends, (1 - amount) at the midpoint — a self-contained auto-restoring duck.
    let duckOf (bus: Bus) =
        match ducks.TryGetValue bus with
        | true, d when d.Duration > 0.0 ->
            let t = let r = d.Elapsed / d.Duration in (if r < 0.0 then 0.0 elif r > 1.0 then 1.0 else r)
            let tri = if t <= 0.5 then t * 2.0 else (1.0 - t) * 2.0
            1.0 - d.Amount * tri
        | _ -> 1.0

    let busGain (bus: Bus) = clamp01 (baseOf bus * duckOf bus)

    // Advance envelopes by dt; commit completed fades to base and drop finished envelopes.
    let advance (dt: float) =
        let doneFades = ResizeArray<Bus>()
        for kv in fades do
            kv.Value.Elapsed <- kv.Value.Elapsed + dt
            if kv.Value.Elapsed >= kv.Value.Duration then doneFades.Add kv.Key
        for b in doneFades do
            baseGain.[b] <- clamp01 fades.[b].EndG
            fades.Remove b |> ignore
        let doneDucks = ResizeArray<Bus>()
        for kv in ducks do
            kv.Value.Elapsed <- kv.Value.Elapsed + dt
            if kv.Value.Elapsed >= kv.Value.Duration then doneDucks.Add kv.Key
        for b in doneDucks do ducks.Remove b |> ignore

    // Inverse-distance-clamped attenuation on the planar (x,z) distance + stereo pan by AZIMUTH
    // (DEC-001). y is not consulted: the model is planar, as DEC-001 decided.
    let spatial (x: float) (z: float) =
        let (lx, _, lz) = listener
        let dx, dz = x - lx, z - lz
        let raw = sqrt (dx * dx + dz * dz)
        let capped = match config.MaxDistance with Some m when raw > m -> m | _ -> raw
        let d = max capped config.RefDistance
        let att = config.RefDistance / (config.RefDistance + config.Rolloff * (d - config.RefDistance))
        // Pan is `dx / distance` — the SINE OF THE AZIMUTH — and it is a pure direction: how far off
        // to the side the source is, not how far away.
        //
        // It used to be `dx / RefDistance`, which consulted the lateral offset alone and never `dz`.
        // Two things were wrong with that, and neither is subtle once a source is placed off the axis
        // the tests used. Depth was ignored: a source 2m right and 50m AHEAD is ~2 degrees off centre
        // and was panned hard to one ear, as aggressively as one beside the listener's head. And
        // RefDistance — documented as DISTANCE ATTENUATION configuration and nothing else — silently
        // doubled as the pan width, so at its default of 1.0 anything more than one world unit off
        // axis was fully panned. In a game measured in metres that made 3D audio a binary left/right
        // switch, and made tuning attenuation silently re-tune panning.
        //
        // `raw`, not `capped`: MaxDistance caps how far away a thing SOUNDS, and using it here would
        // bend the direction the sound comes FROM. Only attenuation is capped.
        //
        // Front/back is not recoverable in a stereo pan — a source 45 degrees behind-right and one 45
        // degrees ahead-right both give +0.707 — and that is inherent to the seam (a scalar pan), not
        // to this formula. Elevation is out by DEC-001.
        let pan = if raw <= 0.0 then 0.0 else clampUnit (dx / raw)
        clamp01 att, pan

    member _.BusGain(bus: Bus) = busGain bus

    member _.MusicGain =
        match music with
        | Some _ -> clamp01 (busGain Music * busGain Master)
        | None -> 0.0

    member _.Listener = listener
    member _.LastVoices = lastVoices
    member _.Device = device

    member _.SetListener(x: float, y: float, z: float) = listener <- (x, y, z)

    member _.FadeBus(bus: Bus, target: float, seconds: float) =
        let target = clamp01 target
        if seconds <= 0.0 then
            fades.Remove bus |> ignore
            baseGain.[bus] <- target
        else
            fades.[bus] <- { Elapsed = 0.0; Duration = seconds; StartG = busGain bus; EndG = target; Curve = Linear }

    member _.CrossFade(fromBus: Bus, toBus: Bus, seconds: float) =
        if seconds <= 0.0 then
            fades.Remove fromBus |> ignore
            fades.Remove toBus |> ignore
            baseGain.[fromBus] <- 0.0
            baseGain.[toBus] <- 1.0
        else
            fades.[fromBus] <- { Elapsed = 0.0; Duration = seconds; StartG = busGain fromBus; EndG = 0.0; Curve = EqualPowerOut }
            fades.[toBus] <- { Elapsed = 0.0; Duration = seconds; StartG = 0.0; EndG = 1.0; Curve = EqualPowerIn }

    member _.Step(dt: float, effects: AudioEffect list) =
        // 1. time passes.
        advance (max 0.0 dt)
        // 2. apply the frame's effects, collecting one-shot voices at current gains.
        let voices = ResizeArray<Voice>()
        for effect in effects do
            match effect with
            | SetMasterVolume level -> fades.Remove Master |> ignore; baseGain.[Master] <- clamp01 level
            | SetBusVolume(bus, level) -> fades.Remove bus |> ignore; baseGain.[bus] <- clamp01 level
            | Duck(bus, amount, ms) -> ducks.[bus] <- { Elapsed = 0.0; Duration = ms / 1000.0; Amount = clamp01 amount }
            | StopMusic -> music <- None
            | PlayMusic(track, loop) -> music <- Some(track, loop)
            | PlaySfx(sound, volume) ->
                let eff = clamp01 (volume * busGain Sfx * busGain Master)
                voices.Add { Sound = sound; Bus = Sfx; RequestGain = volume; EffectiveGain = eff; Pan = 0.0; Positional = false }
            | PlaySfx3D(sound, x, _, z, volume) ->
                match mixing with
                | Some _ ->
                    let att, pan = spatial x z
                    let eff = clamp01 (volume * busGain Sfx * busGain Master * att)
                    voices.Add { Sound = sound; Bus = Sfx; RequestGain = volume; EffectiveGain = eff; Pan = pan; Positional = true }
                | None ->
                    // Degrade (FR-008): no 3D capability -> non-positional voice at the bus-scaled gain.
                    let eff = clamp01 (volume * busGain Sfx * busGain Master)
                    voices.Add { Sound = sound; Bus = Sfx; RequestGain = volume; EffectiveGain = eff; Pan = 0.0; Positional = false }
        lastVoices <- List.ofSeq voices
        // 3. realize through the backend (guarded; a device hiccup is silence, not a throw).
        //
        // The guard stays exactly as strong as it was — nothing here throws into game code (FR-008,
        // Principle VIII) — but it is no longer MUTE (#33). "A device hiccup is silence" is a true
        // account of a hiccup and a false account of a dead device: when the backend throws every
        // frame (yanked device, dead driver), the game keeps running, the mixer keeps advancing
        // envelopes, and every frame is silently dropped, forever, with nothing on any channel. The
        // latch names the first fault, and — once the run is long enough that a hiccup is no longer a
        // credible explanation — names it again as the permanent thing it is. A frame that realizes
        // cleanly ends the run, so a device that recovers is never accused of having died.
        //
        // `reached` is load-bearing, not bookkeeping. Most frames of a real game carry no audio at
        // all, and against a plain (non-mixing) backend such a frame makes NO device call whatever.
        // Counting it a success would end the fault run — so a dead device, in a game that plays a
        // sound every few frames rather than every single one, would never accumulate a run, never be
        // escalated, and would go straight back to being indefinite, unexplained silence. A frame that
        // never spoke to the device is evidence of nothing, and must leave the run as it found it.
        let mutable reached = false
        try
            match mixing with
            | Some m ->
                for b in buses do m.SetBusGain(b, busGain b)
                let (lx, ly, lz) = listener
                m.SetListener(lx, ly, lz)
                reached <- true
            | None -> ()
            for effect in effects do
                match effect with
                | PlayMusic(track, loop) ->
                    backend.Play(PlayMusic(track, loop))
                    reached <- true
                | StopMusic ->
                    backend.Play StopMusic
                    reached <- true
                | _ -> ()
            for v in lastVoices do
                (match mixing with
                 | Some m -> m.PlayAt(v.Sound, v.EffectiveGain, v.Pan)
                 | None -> backend.Play(PlaySfx(v.Sound, v.EffectiveGain)))
                reached <- true
            if reached then device.Succeeded DeviceDiagnostics.Realize
        with error ->
            device.Report(DeviceDiagnostics.Realize, error)

[<RequireQualifiedAccess>]
module Engine =
    let defaultSpatial = { RefDistance = 1.0; Rolloff = 1.0; MaxDistance = None }

    // Device faults go to stderr by default — the channel FS.GG.Audio.Host already warns on (the
    // missing-asset diagnostic, the voice ceiling, OpenAL-unavailable). A lambda, not the shorter
    // `DeviceDiagnostics.T(eprintfn "%s")`: the partial application would bind Console.Error ONCE,
    // here, pinning the latch to the writer that existed at construction — so a later
    // Console.SetError would never see the line. Host.fs makes the same choice for the same reason.
    let private stderrDevice () = DeviceDiagnostics.T(fun line -> eprintfn "%s" line)

    let create (backend: IAudioBackend) = T(defaultSpatial, backend, stderrDevice ())
    let createWith (config: SpatialConfig) (backend: IAudioBackend) = T(config, backend, stderrDevice ())

    let createWithDiagnostics (device: DeviceDiagnostics.T) (config: SpatialConfig) (backend: IAudioBackend) =
        T(config, backend, device)
    let step (engine: T) (dt: float) (effects: AudioEffect list) = engine.Step(dt, effects)
    let fadeBus (engine: T) (bus: Bus) (target: float) (seconds: float) = engine.FadeBus(bus, target, seconds)
    let crossFade (engine: T) (fromBus: Bus) (toBus: Bus) (seconds: float) = engine.CrossFade(fromBus, toBus, seconds)
    let setListener (engine: T) (x: float) (y: float) (z: float) = engine.SetListener(x, y, z)

    // The sink family (#27). `Host.Audio.play backend` is an `AudioEffect list -> unit` that plays
    // STRAIGHT at the backend, so SetBusVolume/Duck are dropped and PlaySfx3D degrades — a volume
    // slider wired that way is inaudible. These build a sink of the SAME type that steps an engine
    // instead, so the mixing semantics apply and the safe path is the one-liner.
    //
    // They are `create…` rather than `sink…` because the returned closure OWNS mutable state (an
    // engine and a clock): built once and reused per frame it mixes; rebuilt per frame it would
    // carry a fresh engine, no envelope would ever advance, and the bug would be back (DEC-002).

    let createSinkWith (dt: unit -> float) (engine: T) : AudioEffect list -> unit =
        fun effects -> engine.Step(dt (), effects)

    let createSinkOver (engine: T) : AudioEffect list -> unit =
        // Monotonic, and immune to a wall-clock step: Stopwatch, not DateTime.
        let clock = Diagnostics.Stopwatch.StartNew()
        let mutable last = ValueNone
        let dt () =
            let now = clock.Elapsed.TotalSeconds
            match last with
            // The first frame advances the engine by 0, NOT by the sink's age: a sink built at
            // startup and first driven seconds later would otherwise instantly complete any envelope
            // installed in between (a fade set up before the loop starts).
            | ValueNone -> last <- ValueSome now; 0.0
            | ValueSome prev -> last <- ValueSome now; now - prev
        createSinkWith dt engine

    let createSink (backend: IAudioBackend) : AudioEffect list -> unit =
        createSinkOver (create backend)
