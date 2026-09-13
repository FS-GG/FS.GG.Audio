module WebAudioCorrespondence

open System
open FS.GG.Audio.Core
open FS.GG.Audio.WebBrowser

let private status = function WebAudioStatus.Locked -> "locked" | WebAudioStatus.Running -> "running" | WebAudioStatus.Paused -> "paused" | WebAudioStatus.Disposed -> "disposed"
let private bus = function Master -> "master" | Music -> "music" | Sfx -> "sfx" | Ui -> "ui" | Ambient -> "ambient"
let private effect = function
    | WebAudioPolicyEffect.CreateOrResumeContext -> "unlock"
    | WebAudioPolicyEffect.DecodeAsset key -> "decode:" + key
    | WebAudioPolicyEffect.PlayOneShot(id,key,target,gain,pan) -> $"play:{id}:{key}:{bus target}:{gain:F3}:{pan:F3}"
    | WebAudioPolicyEffect.StopVoice id -> $"stop-voice:{id}"
    | WebAudioPolicyEffect.StartMusic(key,loop) ->
        let mode = if loop then "loop" else "once"
        $"music:{key}:{mode}"
    | WebAudioPolicyEffect.StopMusic -> "stop-music"
    | WebAudioPolicyEffect.SetBusGain(target,gain) -> $"gain:{bus target}:{gain:F3}"
    | WebAudioPolicyEffect.DuckBus(target,amount,milliseconds) -> $"duck:{bus target}:{amount:F3}:{milliseconds:F1}"
    | WebAudioPolicyEffect.SuspendContext -> "suspend"
    | WebAudioPolicyEffect.ResumeContext -> "resume"
    | WebAudioPolicyEffect.StopAll -> "stop-all"
    | WebAudioPolicyEffect.CloseContext -> "close"
    | WebAudioPolicyEffect.Refused WebAudioRefusal.Locked -> "refused:locked"
    | WebAudioPolicyEffect.Refused WebAudioRefusal.Paused -> "refused:paused"
    | WebAudioPolicyEffect.Refused(WebAudioRefusal.AssetUnavailable key) -> "refused:asset:" + key
    | WebAudioPolicyEffect.Refused(WebAudioRefusal.UnknownAssetCompletion key) -> "refused:completion:" + key
    | WebAudioPolicyEffect.Refused(WebAudioRefusal.UnlockFailed reason) -> "refused:unlock:" + reason
    | WebAudioPolicyEffect.Refused WebAudioRefusal.Disposed -> "refused:disposed"

let run () =
    let config = { MaxOneShotVoices = 2 }
    let sound = SoundId "hit"
    let key = WebAudioPolicy.soundKey sound
    let observations =
        [ WebAudioObservation.Dispatch(PlaySfx(sound,1.0))
          WebAudioObservation.RequestUnlock
          WebAudioObservation.UnlockSucceeded
          WebAudioObservation.RequestAsset key
          WebAudioObservation.AssetReady key
          WebAudioObservation.Dispatch(SetBusVolume(Ui,0.4))
          WebAudioObservation.Dispatch(PlaySfx3D(sound,1.0,0.0,1.0,0.8))
          WebAudioObservation.Dispatch(PlaySfx(sound,0.7))
          WebAudioObservation.Dispatch(PlaySfx(sound,0.6))
          WebAudioObservation.Pause
          WebAudioObservation.Dispatch(PlaySfx(sound,1.0))
          WebAudioObservation.Resume
          WebAudioObservation.Dispose ]
    let _, rows =
        observations
        |> List.fold (fun (state,rows) observation ->
            let next,effects = WebAudioPolicy.update config observation state
            let currentStatus,assets,ready,voices,music,nextId = WebAudioPolicy.observe next
            let musicState = if music then "music" else "silent"
            let effectText = effects |> List.map effect |> String.concat ","
            let row = $"{status currentStatus}|{assets}|{ready}|{voices}|{musicState}|{nextId}|{effectText}"
            next, row::rows) (WebAudioPolicy.initialize config,[])
    rows |> List.rev |> String.concat "\n" |> fun value -> value + "\n"
