module WebAudioBrowserFixture

open System
open Fable.Core
open Fable.Core.JsInterop
open FS.GG.Audio.Core
open FS.GG.Audio.WebBrowser

let events = ResizeArray<string>()
let mutable host: WebAudioHost option = None

let eventText = function
    | WebAudioHostEvent.AssetReady key -> "asset-ready:" + key
    | WebAudioHostEvent.AssetFailed(key, _) -> "asset-failed:" + key
    | WebAudioHostEvent.Refused WebAudioRefusal.Locked -> "refused:locked"
    | WebAudioHostEvent.Refused WebAudioRefusal.Paused -> "refused:paused"
    | WebAudioHostEvent.Refused(WebAudioRefusal.AssetUnavailable key) -> "refused:asset:" + key
    | WebAudioHostEvent.Refused(WebAudioRefusal.UnknownAssetCompletion key) -> "refused:completion:" + key
    | WebAudioHostEvent.Refused(WebAudioRefusal.UnlockFailed reason) -> "refused:unlock:" + reason
    | WebAudioHostEvent.Refused WebAudioRefusal.Disposed -> "refused:disposed"
    | WebAudioHostEvent.EffectDispatched _ -> "effect"
    | WebAudioHostEvent.Disposed -> "disposed"

let observe () =
    let value = host.Value.Observe()
    createObj [
        "status" ==> string value.Status
        "assets" ==> value.AssetCount
        "ready" ==> value.ReadyAssetCount
        "voices" ==> value.ActiveVoiceCount
        "music" ==> value.HasMusic
        "context" ==> value.ContextCreated
        "contextState" ==> value.ContextState
        "graphVoices" ==> value.GraphVoiceCount
        "graphMusic" ==> value.GraphHasMusic
        "masterGain" ==> value.MasterGain
        "lastPan" ==> value.LastPan
        "disposed" ==> value.IsDisposed
        "events" ==> events.ToArray()
    ]

let api =
    createObj [
        "mount" ==> fun () ->
            host |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            events.Clear()
            host <- Some(new WebAudioHost({ MaxOneShotVoices = 2 }, eventText >> events.Add))
            observe()
        "observe" ==> fun () -> observe()
        "unlock" ==> fun () -> host.Value.UnlockFromGesture()
        "loadSound" ==> fun (id: string) (url: string) -> host.Value.LoadSound(SoundId id, url)
        "loadTrack" ==> fun (id: string) (url: string) -> host.Value.LoadTrack(TrackId id, url)
        "play" ==> fun (id: string) -> host.Value.Dispatch(PlaySfx(SoundId id, 0.8)); observe()
        "playAt" ==> fun (id: string) -> host.Value.Dispatch(PlaySfx3D(SoundId id, 1.0, 0.0, 1.0, 0.75)); observe()
        "music" ==> fun (id: string) -> host.Value.Dispatch(PlayMusic(TrackId id, true)); observe()
        "stopMusic" ==> fun () -> host.Value.Dispatch StopMusic; observe()
        "master" ==> fun (gain: float) -> host.Value.Dispatch(SetMasterVolume gain); observe()
        "bus" ==> fun (gain: float) -> host.Value.Dispatch(SetBusVolume(Ui, gain)); observe()
        "duck" ==> fun () -> host.Value.Dispatch(Duck(Music, 0.5, 100.0)); observe()
        "pause" ==> fun () -> host.Value.Pause(); observe()
        "resume" ==> fun () -> host.Value.Resume(); observe()
        "dispose" ==> fun () -> (host.Value :> IDisposable).Dispose(); observe()
    ]

[<Emit("window.webAudio = $0")>]
let expose (_value: obj) : unit = jsNative

expose api
