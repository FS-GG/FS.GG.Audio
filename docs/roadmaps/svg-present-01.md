# SVG-PRESENT-01.3 — Gesture-unlocked Web Audio host

Status: implementation complete in this routine change; publication remains deferred to SVG-PREVIEW-B.

FS.GG.Audio owns the portable audio-effect vocabulary and its browser realization. The existing native
OpenAL Host, Engine and Elmish packages remain unchanged. `FS.GG.Audio.Core` now carries an exact curated
Fable view, and `FS.GG.Audio.WebBrowser` provides a pure ownership policy plus the JavaScript graph host.

The existing Engine is intentionally outside the browser closure because it depends on the native Host.
The browser policy instead consumes the same `AudioEffect` contract directly and owns the browser-specific
context, buffer, voice, music and bus lifecycles without introducing an OpenAL edge.

## Completed acceptance

- [x] Construction is locked and silent until `UnlockFromGesture` creates the one owned AudioContext and
  submits its resume request from the gesture stack.
- [x] Asset state distinguishes pending, ready and failed decode; effects against unavailable assets refuse.
- [x] Master/music/SFX/UI/ambient buses, clamped gain, ducking, one music loop, stereo pan and pause/resume
  map to owned Web Audio nodes.
- [x] One-shot voices are bounded and deterministically steal the oldest voice at capacity.
- [x] Disposal stops every voice and loop, clears owned nodes and closes the context; later commands refuse.
- [x] A packed .NET/Fable corpus agrees at SHA-256
  `895a261873da657a3093700e77bf9507c66b328f3730f429252a1fd53c6cc698` and verifies that neither package's
  Fable closure contains a native/OpenAL dependency.
- [x] The browser fixture exercises the exact packed package in Chromium, Firefox and WebKit in CI, records
  actual context state, and checks gesture gating, decode, meaningful graph dispatch, voice pressure,
  pause/resume, failures and disposal.

Automated browser evidence asserts graph state and lifecycle. Audibility is explicitly disclosed as a manual
listening observation and is not inferred from headless execution.

