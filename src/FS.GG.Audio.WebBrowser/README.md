# FS.GG.Audio.WebBrowser

`FS.GG.Audio.WebBrowser` provides a gesture-gated Web Audio host for the portable
`FS.GG.Audio.Core` effect vocabulary. It owns browser context creation, decoded
asset buffers, bounded one-shot voices, looped music, bus gain, ducking,
positional stereo pan, pause/resume, and deterministic disposal.

Create the host during application setup, call `UnlockFromGesture` from a real
user gesture, load product-owned asset URLs, and dispatch existing `AudioEffect`
values. The pure `WebAudioPolicy` can be tested on .NET and Fable without a
browser audio device.

The package does not claim automated audibility. Browser integration tests
verify real `AudioContext` graph state and lifecycle; listening remains a manual
product acceptance step.
