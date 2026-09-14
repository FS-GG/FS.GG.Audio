---
name: fs-gg-browser-audio
description: Use when connecting FS.GG game audio effects to the gesture-gated browser Web Audio host.
---

# Browser game audio

Use `FS.GG.Audio.WebBrowser.WebAudioHost` as the browser effect interpreter. Keep gameplay decisions in
the portable `AudioEffect` vocabulary and dispatch an effect only after the authoritative game operation
is accepted. Audio callbacks do not mutate game state.

Create one host for the mounted player and call `UnlockFromGesture` only from a real user gesture. Load
product-owned sound and music URLs, observe readiness, and keep the game playable when an asset is missing.
`Locked`, `Paused`, `AssetUnavailable`, and `Disposed` are normal explicit refusals.

```fsharp
let audioEvents = ResizeArray<WebAudioHostEvent>()
use audio =
    new WebAudioHost(
        WebAudioHost.defaultConfig,
        audioEvents.Add)

audio.UnlockFromGesture()
audio.LoadSound(SoundId "hit", "/audio/hit.ogg")
audio.Dispatch(Audio.playSfx (SoundId "hit") 1.0)
```

Pause and resume with the document/player lifecycle. Dispose on unmount so voices, music, callbacks, and the
audio context cannot leak into the next session. Keep stable semantic sound ids in replay/export; never record
decoded buffers, wall-clock callback timing, or hidden server facts.

For accessibility, provide a visual or textual alternative for information carried by sound, respect the
player's mute and reduced-stimulation choices, and do not treat successful context creation as proof that a
clip was audible on an unavailable physical device.
