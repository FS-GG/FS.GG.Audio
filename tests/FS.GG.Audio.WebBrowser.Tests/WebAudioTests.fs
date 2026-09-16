module WebAudioPolicyTests

open Expecto
open FS.GG.Audio.Core
open FS.GG.Audio.WebBrowser

let config = { MaxOneShotVoices = 2 }

let step observation state =
    WebAudioPolicy.update config observation state

let unlocked () =
    WebAudioPolicy.initialize config
    |> step WebAudioObservation.UnlockSucceeded
    |> fst

[<Tests>]
let tests =
    testList
        "portable Web Audio policy"
        [
            test "construction is silent and locked until gesture completion" {
                let initial = WebAudioPolicy.initialize config

                Expect.equal
                    (WebAudioPolicy.observe initial)
                    (WebAudioStatus.Locked, 0, 0, 0, false, 0UL)
                    "no context or asset is implied"

                let unchanged, refused =
                    initial
                    |> step (WebAudioObservation.Dispatch(Audio.playSfx (SoundId "hit") 1.0))

                Expect.equal unchanged initial "locked dispatch changes no state"

                Expect.equal
                    refused
                    [ WebAudioPolicyEffect.Refused WebAudioRefusal.Locked ]
                    "locked refusal is explicit"

                let requested, effects = initial |> step WebAudioObservation.RequestUnlock
                Expect.equal requested initial "request alone does not claim success"

                Expect.equal
                    effects
                    [ WebAudioPolicyEffect.CreateOrResumeContext ]
                    "gesture interpreter creates the context"

                let running, _ = requested |> step WebAudioObservation.UnlockSucceeded

                Expect.equal
                    (WebAudioPolicy.observe running |> fun (status, _, _, _, _, _) -> status)
                    WebAudioStatus.Running
                    "completion unlocks"
            }

            test "ready assets dispatch one-shot, pan, loop and bus controls" {
                let sound = SoundId "hit"
                let track = TrackId "theme"

                let soundKey, trackKey =
                    WebAudioPolicy.soundKey sound, WebAudioPolicy.trackKey track

                let state =
                    unlocked ()
                    |> step (WebAudioObservation.RequestAsset soundKey)
                    |> fst
                    |> step (WebAudioObservation.AssetReady soundKey)
                    |> fst
                    |> step (WebAudioObservation.RequestAsset trackKey)
                    |> fst
                    |> step (WebAudioObservation.AssetReady trackKey)
                    |> fst

                let one, oneEffects =
                    state
                    |> step (WebAudioObservation.Dispatch(PlaySfx3D(sound, 1.0, 0.0, 0.0, 0.75)))

                Expect.equal
                    oneEffects
                    [ WebAudioPolicyEffect.PlayOneShot(0UL, soundKey, Sfx, 0.75, 1.0) ]
                    "positional request becomes a stereo voice"

                let music, musicEffects =
                    one |> step (WebAudioObservation.Dispatch(PlayMusic(track, true)))

                Expect.equal
                    musicEffects
                    [ WebAudioPolicyEffect.StartMusic(trackKey, true) ]
                    "one loop starts from a ready track"

                let _, busEffects =
                    music |> step (WebAudioObservation.Dispatch(SetBusVolume(Ui, 0.4)))

                Expect.equal busEffects [ WebAudioPolicyEffect.SetBusGain(Ui, 0.4) ] "named bus is preserved"
            }

            test "voice pressure steals the oldest one-shot deterministically" {
                let sound = SoundId "hit"
                let key = WebAudioPolicy.soundKey sound

                let ready =
                    unlocked ()
                    |> step (WebAudioObservation.RequestAsset key)
                    |> fst
                    |> step (WebAudioObservation.AssetReady key)
                    |> fst

                let one, _ = ready |> step (WebAudioObservation.Dispatch(PlaySfx(sound, 1.0)))
                let two, _ = one |> step (WebAudioObservation.Dispatch(PlaySfx(sound, 1.0)))
                let three, effects = two |> step (WebAudioObservation.Dispatch(PlaySfx(sound, 1.0)))

                Expect.equal
                    effects
                    [
                        WebAudioPolicyEffect.StopVoice 0UL
                        WebAudioPolicyEffect.PlayOneShot(2UL, key, Sfx, 1.0, 0.0)
                    ]
                    "oldest voice is replaced first"

                Expect.equal
                    (WebAudioPolicy.observe three |> fun (_, _, _, voices, _, next) -> voices, next)
                    (2, 3UL)
                    "ownership remains bounded"
            }

            test "asset failure, pause and disposal preserve explicit lifecycle" {
                let key = WebAudioPolicy.soundKey (SoundId "missing")
                let pending = unlocked () |> step (WebAudioObservation.RequestAsset key) |> fst
                let failed, _ = pending |> step (WebAudioObservation.AssetFailed(key, "decode"))

                let unchanged, refusal =
                    failed |> step (WebAudioObservation.Dispatch(PlaySfx(SoundId "missing", 1.0)))

                Expect.equal unchanged failed "failed asset cannot acquire a voice"

                Expect.equal
                    refusal
                    [ WebAudioPolicyEffect.Refused(WebAudioRefusal.AssetUnavailable key) ]
                    "asset failure remains visible"

                let paused, pauseEffects = failed |> step WebAudioObservation.Pause
                Expect.equal pauseEffects [ WebAudioPolicyEffect.SuspendContext ] "pause suspends the context"
                let resumed, resumeEffects = paused |> step WebAudioObservation.Resume
                Expect.equal resumeEffects [ WebAudioPolicyEffect.ResumeContext ] "resume is explicit"
                let disposed, disposeEffects = resumed |> step WebAudioObservation.Dispose

                Expect.equal
                    disposeEffects
                    [ WebAudioPolicyEffect.StopAll; WebAudioPolicyEffect.CloseContext ]
                    "disposal releases nodes and context"

                let after, post = disposed |> step WebAudioObservation.Resume
                Expect.equal after disposed "disposed is terminal"

                Expect.equal
                    post
                    [ WebAudioPolicyEffect.Refused WebAudioRefusal.Disposed ]
                    "post-disposal use is explicit"
            }
        ]
