# Third-party notices — FS.GG.Audio

The FS.GG.Audio source is MIT (see [`LICENSE`](LICENSE)). Some of the packages **redistribute
third-party software with its own licence**, and one of those licences is not MIT. This file is that
notice. It applies to anything you get from these packages, whether you restored them from nuget.org,
GitHub Packages, or built them yourself.

## Which packages this concerns

| Package | Redistributes OpenAL Soft (LGPL)? |
|---|---|
| `FS.GG.Audio.Core` | **No.** FSharp.Core only — no native code, no OpenAL. |
| `FS.GG.Audio.Host` | **Yes**, directly. |
| `FS.GG.Audio.Engine` | **Yes**, transitively (it depends on `FS.GG.Audio.Host`). |
| `FS.GG.Audio.Elmish` | **Yes**, transitively (it depends on `FS.GG.Audio.Host`). |

Restoring any of the bottom three puts an OpenAL Soft shared library into your build output under
`runtimes/<rid>/native/`, for every runtime identifier you publish.

## OpenAL Soft — LGPL-2.0-or-later

- **Delivered by:** [`Silk.NET.OpenAL.Soft.Native`](https://www.nuget.org/packages/Silk.NET.OpenAL.Soft.Native)
  (pinned in `Directory.Packages.props`), which declares `LGPL-2.0-or-later` and
  `requireLicenseAcceptance = true`.
- **What it is:** prebuilt [OpenAL Soft](https://openal-soft.org/) binaries — `libopenal.so`
  (linux-x64, linux-arm, linux-arm64), `libopenal.dylib` (osx-x64, osx-arm64), and `soft_oal.dll`
  (win-x64, win-x86).
- **Upstream:** <https://github.com/kcat/openal-soft> · **Licence text:**
  <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>

**It is a separate, dynamically-loaded, replaceable shared library.** It is not linked into any
FS.GG.Audio assembly and no OpenAL Soft code is copied into one. `FS.GG.Audio.Host` reaches it
through the Silk.NET managed bindings, which P/Invoke into whichever `libopenal` the platform loader
resolves at run time.

That is a deliberate design decision, recorded before the code was written — see DEC-001 in
[`work/002-audio-host/clarifications.md`](work/002-audio-host/clarifications.md) and the
"permissive licensing only" boundary in [`work/002-audio-host/charter.md`](work/002-audio-host/charter.md).
The intent is to keep the component usable by closed-source games: the LGPL's terms for a work that
merely *uses* the library turn on the user being able to substitute their own copy, and dynamic
linking against a replaceable shared library is the mechanism that allows it.

**Replacing it** is supported and is the point of shipping it this way. Drop your own OpenAL
implementation in place of `runtimes/<rid>/native/libopenal.*` in the published output, or put one
earlier on the platform's library search path (`LD_LIBRARY_PATH` on Linux, `DYLD_LIBRARY_PATH` on
macOS, the DLL search order on Windows). FS.GG.Audio does not pin, verify, or hard-code the library's
identity — whatever the loader resolves is what it calls. If nothing resolves, `OpenAlBackend.create`
degrades to the record-only Null backend rather than failing (FR-004), so a build with the native
removed still runs, silently. Ask `Backend.isDeviceBacked` if that distinction matters to you.

**Excluding it entirely:** if you do not want the LGPL binary in your output at all, take
`FS.GG.Audio.Core` alone (it has no native dependency) and implement `IAudioBackend` yourself, or add
`<ExcludeAssets>` / a `runtimes` filter against `Silk.NET.OpenAL.Soft.Native` and supply your own
OpenAL. The `IAudioBackend` seam exists so the component is never captive to one native dependency.

## Silk.NET — MIT

- [`Silk.NET.OpenAL`](https://www.nuget.org/packages/Silk.NET.OpenAL) and `Silk.NET.Core` — managed
  bindings, MIT. Upstream: <https://github.com/dotnet/Silk.NET>.

The managed bindings and the native library are separate packages with separate licences; only the
native one is LGPL.

## Elmish — MIT

- [`Elmish`](https://www.nuget.org/packages/Elmish), used by `FS.GG.Audio.Elmish` for its `Cmd`
  authoring surface. MIT. Upstream: <https://github.com/elmish/elmish>.

## FSharp.Core — MIT

- [`FSharp.Core`](https://www.nuget.org/packages/FSharp.Core), used by every package. MIT.

---

This notice is a factual record of what these packages redistribute and how, written so that the
obligation to inform you is actually discharged rather than merely reasoned about. **It is not legal
advice.** If your distribution model depends on the analysis above — particularly if you ship a
closed-source product — verify it against the licence text and your own counsel rather than against
this file.
