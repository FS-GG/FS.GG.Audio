module BrowserAudioExample

open FS.GG.Audio.Core
open FS.GG.Audio.WebBrowser

let connect () =
    let audioEvents = ResizeArray<WebAudioHostEvent>()
    let audio = new WebAudioHost(WebAudioHost.defaultConfig, audioEvents.Add)

    audio.UnlockFromGesture()
    audio.LoadSound(SoundId "hit", "/audio/hit.ogg")
    audio.Dispatch(Audio.playSfx (SoundId "hit") 1.0)
    audio
