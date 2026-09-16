namespace FS.GG.Audio.WebBrowser

open System
open FS.GG.Audio.Core

/// Browser audio lifecycle. Construction remains locked and silent until a gesture unlock succeeds.
[<RequireQualifiedAccess>]
type WebAudioStatus =
    | Locked
    | Running
    | Paused
    | Disposed

/// Decode state for a product-owned sound or track asset.
[<RequireQualifiedAccess>]
type WebAudioAssetStatus =
    | Pending
    | Ready
    | Failed of string

type WebAudioConfig = { MaxOneShotVoices: int }

[<RequireQualifiedAccess>]
type WebAudioRefusal =
    | Locked
    | Paused
    | AssetUnavailable of string
    | UnknownAssetCompletion of string
    | UnlockFailed of string
    | Disposed

/// Pure observations for the Web Audio ownership policy.
[<RequireQualifiedAccess>]
type WebAudioObservation =
    | RequestUnlock
    | UnlockSucceeded
    | UnlockFailed of string
    | RequestAsset of assetKey: string
    | AssetReady of assetKey: string
    | AssetFailed of assetKey: string * diagnostic: string
    | Dispatch of AudioEffect
    | Pause
    | Resume
    | VoiceEnded of voiceId: uint64
    | Dispose

/// Ordered instructions interpreted by the browser graph host.
[<RequireQualifiedAccess>]
type WebAudioPolicyEffect =
    | CreateOrResumeContext
    | DecodeAsset of assetKey: string
    | PlayOneShot of voiceId: uint64 * assetKey: string * bus: Bus * gain: float * pan: float
    | StopVoice of voiceId: uint64
    | StartMusic of assetKey: string * loop: bool
    | StopMusic
    | SetBusGain of bus: Bus * gain: float
    | DuckBus of bus: Bus * amount: float * milliseconds: float
    | SuspendContext
    | ResumeContext
    | StopAll
    | CloseContext
    | Refused of WebAudioRefusal

/// Abstract pure state used for .NET/Fable correspondence and browser interpretation.
type WebAudioPolicyState

[<RequireQualifiedAccess>]
module WebAudioPolicy =
    val soundKey: SoundId -> string
    val trackKey: TrackId -> string
    val initialize: config: WebAudioConfig -> WebAudioPolicyState

    val update:
        config: WebAudioConfig ->
        observation: WebAudioObservation ->
        state: WebAudioPolicyState ->
            WebAudioPolicyState * WebAudioPolicyEffect list

    /// Status, asset count, ready count, active voice count, music presence and next voice id.
    val observe: state: WebAudioPolicyState -> WebAudioStatus * int * int * int * bool * uint64

[<RequireQualifiedAccess>]
type WebAudioHostEvent =
    | AssetReady of string
    | AssetFailed of string * string
    | Refused of WebAudioRefusal
    | EffectDispatched of AudioEffect
    | Disposed

type WebAudioHostObservation =
    {
        Status: WebAudioStatus
        AssetCount: int
        ReadyAssetCount: int
        ActiveVoiceCount: int
        HasMusic: bool
        ContextCreated: bool
        ContextState: string
        GraphVoiceCount: int
        GraphHasMusic: bool
        MasterGain: float
        LastPan: float
        IsDisposed: bool
    }

/// Gesture-gated, disposable Web Audio graph for the existing `AudioEffect` vocabulary.
[<Sealed>]
type WebAudioHost =
    new: config: WebAudioConfig * onEvent: (WebAudioHostEvent -> unit) -> WebAudioHost
    member UnlockFromGesture: unit -> unit
    member LoadSound: sound: SoundId * url: string -> unit
    member LoadTrack: track: TrackId * url: string -> unit
    member Dispatch: effect: AudioEffect -> unit
    member Pause: unit -> unit
    member Resume: unit -> unit
    member Observe: unit -> WebAudioHostObservation
    interface IDisposable

[<RequireQualifiedAccess>]
module WebAudioHost =
    val defaultConfig: WebAudioConfig
