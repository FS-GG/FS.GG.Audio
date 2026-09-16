namespace FS.GG.Audio.Core

// Pure audio request surface + record-only interpreter. A product's `update` emits AudioEffect
// values (never plays sound); the interpreter folds a batch into ordered evidence. BCL-only,
// dependency-light — the real audio-output backend (FS.GG.Audio.Host) is a follow-up and will
// consume the same values without changing this surface.
// Extracted verbatim from FS.GG.UI.Canvas.Audio (FS.GG.Rendering, Feature 243); behavior byte-parity.

type SoundId = SoundId of string

type TrackId = TrackId of string

type Bus =
    | Master
    | Music
    | Sfx
    | Ui
    | Ambient

type AudioEffect =
    | PlaySfx of sound: SoundId * volume: float
    | PlayMusic of track: TrackId * loop: bool
    | StopMusic
    | SetMasterVolume of level: float
    | PlaySfx3D of sound: SoundId * x: float * y: float * z: float * volume: float
    | SetBusVolume of bus: Bus * level: float
    | Duck of bus: Bus * amount: float * milliseconds: float

type AudioEvidence = { Requested: AudioEffect list }

[<RequireQualifiedAccess>]
module Audio =

    let minVolume = 0.0
    let maxVolume = 1.0

    // Total clamp into [minVolume, maxVolume]. `nan` fails both comparisons, so it falls through to
    // minVolume — a defined, non-throwing floor (Principle VI: safe failure, no surprise on bad input).
    let clampVolume (level: float) : float =
        if level <= minVolume || System.Double.IsNaN level then
            minVolume
        elif level >= maxVolume then
            maxVolume
        else
            level

    let playSfx (sound: SoundId) (volume: float) : AudioEffect = PlaySfx(sound, clampVolume volume)

    let playMusic (track: TrackId) (loop: bool) : AudioEffect = PlayMusic(track, loop)

    let stopMusic: AudioEffect = StopMusic

    let setMasterVolume (level: float) : AudioEffect = SetMasterVolume(clampVolume level)

    let playSfx3D (sound: SoundId) (x: float) (y: float) (z: float) (volume: float) : AudioEffect =
        PlaySfx3D(sound, x, y, z, clampVolume volume)

    let setBusVolume (bus: Bus) (level: float) : AudioEffect = SetBusVolume(bus, clampVolume level)

    // Duck amount is a normalized attenuation in [0,1]; the attack duration is a non-negative
    // millisecond floor (`nan`/negative -> 0.0, a defined instantaneous duck).
    let duck (bus: Bus) (amount: float) (milliseconds: float) : AudioEffect =
        let ms = if milliseconds > 0.0 then milliseconds else 0.0
        Duck(bus, clampVolume amount, ms)

    // Normalize the volume carried by an effect so recorded evidence is always in range, regardless
    // of whether the caller went through a smart constructor.
    let private normalize (effect: AudioEffect) : AudioEffect =
        match effect with
        | PlaySfx(sound, volume) -> PlaySfx(sound, clampVolume volume)
        | SetMasterVolume level -> SetMasterVolume(clampVolume level)
        | PlaySfx3D(sound, x, y, z, volume) -> PlaySfx3D(sound, x, y, z, clampVolume volume)
        | SetBusVolume(bus, level) -> SetBusVolume(bus, clampVolume level)
        | Duck(bus, amount, ms) -> Duck(bus, clampVolume amount, (if ms > 0.0 then ms else 0.0))
        | PlayMusic _
        | StopMusic -> effect

    let emptyEvidence: AudioEvidence = { Requested = [] }

    // Append to the tail so Requested stays oldest-first without a reverse per call. Requested is a
    // small per-frame batch, so the O(n) append is not a hot path.
    //
    // That last sentence is a claim about the CALLER, not about this function, and it is only true of
    // callers that actually pass a batch. Fold `record` over a long-lived accumulator instead and it
    // is quadratic in everything accumulated so far — which is exactly what FS.GG.Audio.Host's
    // NullBackend did, at a measured 10x slowdown after ~33 minutes of play (review 2026-07-16 §3.3).
    // It now accumulates in a ResizeArray and calls `interpret` per effect for the normalization.
    // Anything that records for longer than a frame should do the same rather than fold this.
    let record (effect: AudioEffect) (evidence: AudioEvidence) : AudioEvidence =
        { evidence with
            Requested = evidence.Requested @ [ normalize effect ]
        }

    let interpret (effects: AudioEffect list) : AudioEvidence =
        {
            Requested = List.map normalize effects
        }
