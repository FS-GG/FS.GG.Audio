# FS.GG.Audio — Code & Architecture Review

- **Date:** 2026-07-16
- **Author:** Claude (Opus 4.8), commissioned by @EHotwagner
- **Scope:** Whole repository at `165cb0f` (main) — all four packages (`Core`, `Host`, `Engine`,
  `Elmish`), the sample, the test suite, CI workflows, and packaging/release mechanics.
- **Environment:** .NET SDK 10.0.301, Linux, headless (no audio device).
- **Method:** Every finding marked *proven* was **executed**, not inferred. The review was
  commissioned with the concern that *"not much of this repo has actually been battle tested and
  might not work"*, so the emphasis is on running the code against inputs the test suite does not
  supply, rather than on reading it.

---

## 1. Executive summary

**It works. The architecture is sound. And there are five real bugs, all in the same blind spot.**

The build is clean (0 warnings under `TreatWarningsAsErrors`), all 69 tests pass headless in ~320 ms,
the sample runs deterministically, and the degrade-to-Null path works even under conditions harsher
than it was designed for. `Engine.step` survives adversarial float fuzzing with zero throws and zero
invariant violations. The layering is honest and the `.fsi` contracts are accurate.

So the fear is unfounded in its literal form. But it points at something real, just one level over:
**this code has been tested by its authors, against the inputs they imagined. It has never met
hostile input.** Every serious finding below came from supplying some. In priority order:

1. **`Wav.tryParse` hangs forever** on a malformed WAV (proven). The surrounding `try/with` cannot
   catch an infinite loop. A corrupt or attacker-supplied asset freezes the calling thread
   permanently.
2. **A non-PCM WAV is accepted and uploaded as raw PCM** (proven) — the codec field is never read.
   The result is not silence but **noise**: the one failure mode worse than the ones this codebase
   has spent 300 lines of commentary abolishing.
3. **`NullBackend` grows unboundedly and degrades quadratically** (proven, measured: 10× slowdown
   over 33 minutes of play). This is the *default* backend and the *degrade target* — so the player
   FR-004 exists to protect is the one who gets a game that slows down the longer it runs.
4. **OpenAL errors are never checked** (`alGetError` appears nowhere). Silk.NET does not throw on AL
   errors — it sets a code. So `guarded`'s `try/with` catches marshalling faults and nothing else,
   and the entire #33 device-fault apparatus is **blind to the way OpenAL actually reports failure.**
5. **3D panning ignores depth entirely** (proven). A source 2° off-centre hard-pans to one ear. At
   the default config, 3D audio is effectively a binary left/right switch.

**A correction to a claim I made mid-review, because it matters.** The README says the OpenAL
backend is never exercised in CI. That is false — `gate.yml` really does open a real OpenAL device
via OpenAL Soft's null driver, and the mechanism is genuinely clever. I initially credited it as
working. It isn't, quite: **the gate opens a device and then asks it one type-test question.** Both
device tests pass a resolver returning `None` for everything and assert `backend :? IMixingBackend` —
a static type fact, true at compile time. No buffer is ever uploaded, no source ever generated,
`PlayAt` never reaches a real buffer. The #42 work built the entire apparatus for a non-vacuous
device lane and then never wrote the assertions to fill it. This is worth stating plainly because
`gate.yml:145-148` claims the opposite in its own comment, and I believed the comment before I
checked it.

**Overall: the foundation is genuinely good and I would not restructure it.** The engineering
discipline on display — the diagnostic latches, the #33/#34 work on making silence explicable, the
CI's refusal to report green off an unparsed result — is well above average, and in places
excellent. What is missing is adversarial input testing, and that gap is narrow, cheap, and mostly
closable with infrastructure already paid for.

---

## 2. What was verified as working

Stated first, because the review was commissioned on a worry and the worry is substantially
unfounded. Each was executed on this machine.

| Claim | Result |
|---|---|
| `dotnet build FS.GG.Audio.slnx -c Debug` | Clean. 0 warnings, 0 errors, under `TreatWarningsAsErrors`. |
| `dotnet test FS.GG.Audio.slnx -c Debug` | **69/69 pass**, 0 skipped (Core 10, Host 29, Engine 21, Elmish 9), ~320 ms. |
| `dotnet run --project samples/FS.GG.Audio.Sample` | Runs; transcript deterministic, matches its documented narrative. |
| `Engine.step` totality (`.fsi`: *"Total; never throws"*) | **Holds** — 0 throws over `{nan, ±inf, ±1e308, ±0.0, 1e-320}` across `dt` × volume × all 5 buses; every `BusGain`/`EffectiveGain` in `[0,1]`. ⚠️ **The `Pan` half of this claim was wrong** (see §3.5): the fuzz ran over `NullBackend`, which is not an `IMixingBackend`, so `PlaySfx3D` degraded and the spatial path was never reached. Re-run over a mixing backend it found **7 violations** — `Pan` could be `nan`. Fixed 2026-07-16; the totality result stands. |
| Degrade-to-Null (FR-004) | **Works**, under a *harsher* failure than designed for: with the Silk.NET assembly wholly unresolvable, `OpenAlBackend.create` still returned `RecordOnly (DeviceUnavailable …)`, accepted `Play`, disposed cleanly. No throw escaped. |
| `Backend.kindOf` / `isDeviceBacked` (#34) | Correct — reported `RecordOnly` carrying the device's own reason in the value. |
| `.fsi` ↔ `docs/api-surface/` parity | **Zero drift** across all four packages. |
| `.fsi` documentation honesty | 13 non-trivial documented claims checked line-by-line against the implementation — **all accurate**. The signature files do not lie. (The READMEs and one gate comment do — §3.11.) |
| Core is BCL-only (FR-006) | **Confirmed** — lockfile resolves exactly `[FSharp.Core]`. |
| Elmish does not depend on `FS.GG.UI` | **Confirmed** via lockfile. |
| Lockfiles | Committed for all 9 projects; feed hermetic via `packageSourceMapping`. |
| `VoicePool` / `BufferCache` test seams | **Genuinely good** — see §5. Steal, reclaim, ceiling and cache-miss all covered with falsifiable assertions. |

---

## 3. Findings

Severity reflects blast radius on a shipped game, not effort to fix.

### 3.1 HIGH — `Wav.tryParse` hangs forever on a malformed WAV

> **Status: FIXED 2026-07-16**, after this review. The chunk walk now carries a termination guard
> (`Host.fs`), and two tests were added (`HostTests.fs`) — one asserting *termination* under a 5 s
> timeout across the pathological sizes, one pinning the stop-don't-discard decision. Both were
> verified to fail against the unfixed parser before the fix landed. Suite: 69 → 71, all green.
> Independently re-verified: the original reproduction returns in 1 ms, and a 20 000-case
> structured-random RIFF fuzz terminates in 6 ms. The analysis below is retained as the record.

**`src/FS.GG.Audio.Host/Host.fs:80-92`**

The chunk walk advances by `8 + sz + (sz &&& 1)`, where `sz` is read straight from the file with no
validation:

```fsharp
let sz = BitConverter.ToInt32(bytes, pos + 4)
let body = pos + 8
...
pos <- body + sz + (sz &&& 1)
```

For `sz = -8` or `sz = -9` the advance is exactly **zero**, `pos` never moves, and the `while` loop
spins forever. The `try … with _ -> None` at `Host.fs:69/102` **cannot catch an infinite loop** — so
the contract at `Host.fs:66-67` (*"Total — returns None on anything malformed"*) is false.

**Proven.** Measured advance per chunk size:

```
sz    advance   result
-12   -4        ok
-11   -2        ok
-10   -2        ok
-9     0        *** HANG ***
-8     0        *** HANG ***
-7     2        ok
-6     2        ok
```

Worth noting *why* the other negatives pass: they drive `pos` negative,
`Encoding.ASCII.GetString` throws `ArgumentOutOfRangeException`, and the `try/with` swallows it into
`None`. **The parser survives malformed input by accident, not by design.** Only the two values
where the accident doesn't fire are fatal.

**Impact.** `Wav.tryParse` is public (`Host.fsi:72`) and sits on the live play path: `playOneShot` →
`soundBuffer` → `resolveBuffer` → `uploadBuffer` → `tryParse`. A game whose resolver hands over a
corrupt, truncated, or attacker-supplied WAV (mods, UGC, downloaded content) freezes the calling
thread permanently on first play. Rare by accident; trivial on purpose — two bytes.

This also regresses the repo's own stated value: #28 exists to turn a bad asset into a *named
diagnostic* rather than silence. Here a bad asset produces neither diagnostic nor silence, but a hang.

**Fix.** Require forward progress and reject non-positive/oversized sizes:

```fsharp
if sz <= 0 || sz > bytes.Length - body then pos <- bytes.Length   // stop the walk
else pos <- body + sz + (sz &&& 1)
```

A `sz <= 0` guard alone closes both hang values.

### 3.2 HIGH — A non-PCM WAV is accepted and uploaded as raw PCM (it plays as noise)

> **Status: FIXED 2026-07-16**, after this review. `Wav.tryParse` now reads `wFormatTag` and reports
> it as `PcmData.FormatTag`; `uploadBuffer` refuses anything but `Wav.FormatPcm` through a new
> `AssetDiagnostics.UnsupportedCodec` case. Suite: 71 → 74, all green.
>
> **The fix this report recommended was wrong, and would have caused a worse bug.** §3.2 said to
> *"reject everything else via the existing `UnsupportedFormat` leg."* Both halves were mistaken:
>
> - **`WAVE_FORMAT_EXTENSIBLE` (0xFFFE)** is how encoders routinely write *ordinary PCM* — every
>   multichannel export and plenty of stereo ones. Those files parse and play correctly today.
>   A blunt `tag <> 1` rejection would have turned working audio **silent**, trading noise-on-bad-files
>   for silence-on-good-ones. The parser now resolves the SubFormat GUID instead. (`wFormatTag` also
>   has to be read *unsigned* — 0xFFFE as `Int16` is −2 and matches nothing.)
> - **Reusing `UnsupportedFormat` would have emitted a false diagnostic.** It reports channels and
>   bits, so mono 16-bit IMA-ADPCM would read *"1-channel 16-bit WAV, which OpenAL has no buffer
>   format for"* — untrue; OpenAL has `Mono16`. It would send the reader to change a bit depth that
>   was never the problem. Hence a distinct case naming the codec.
>
> Verified: a mutation implementing the naive `tag <> 1` check fails exactly the EXTENSIBLE guard.
> The analysis below is retained as the record.

**`src/FS.GG.Audio.Host/Host.fs:84-87`**

The `fmt ` chunk parse reads channels (`body+2`), sample rate (`body+4`) and bits (`body+14`) — but
**never reads `wFormatTag` at `body+0`**, the field that says which codec the data is in. Any WAV
with plausible channel/bit values therefore parses "successfully", passes `bufferFormat`
(`Host.fs:509-515`, which decides on channels/bits alone), and gets handed to `al.BufferData` as
**raw PCM**.

**Proven** — every one of these is accepted:

```
  ACCEPTED  tag=1  PCM (the real thing)        -> 1ch 16bit
  ACCEPTED  tag=3  IEEE_FLOAT (32-bit float)   -> 1ch 32bit
  ACCEPTED  tag=0x11 IMA-ADPCM                 -> 1ch 16bit
  ACCEPTED  tag=0x55 MP3-in-WAV                -> 2ch 16bit
  ACCEPTED  tag=6  A-law                       -> 1ch 8bit
```

The test WAV itself dutifully writes `1s // PCM` (`HostTests.fs:35`) — the field is understood to
exist and simply never checked.

**Impact, and why this ranks above most of the list.** Every other failure in this codebase degrades
to *silence*, deliberately and thoughtfully. This one degrades to **noise** — a mis-exported asset
(a float32 WAV out of Audacity is a one-click mistake) plays as a burst of static at whatever gain
the mixer asked for. That is worse than silence for a player, worse for a developer trying to
diagnose it, and it defeats the `UnsupportedFormat` diagnostic at `Host.fs:276-281`, which exists to
catch exactly this class and never fires because the format looks fine on the two fields it reads.

**Fix.** Read `wFormatTag` at `body+0`; accept `1` (PCM) and treat `0xFFFE` (WAVE_FORMAT_EXTENSIBLE)
by reading the subformat GUID; reject everything else via the existing
`AssetDiagnostics.UnsupportedFormat` leg. Roughly a five-line change to a diagnostic that is already
written.

*(Related, minor: a negative `data` size yields `Some` with an empty `Data` array (`Host.fs:96`)
rather than `None`.)*

### 3.3 HIGH — `NullBackend` grows unboundedly and degrades quadratically

> **Status: the quadratic is FIXED 2026-07-16; the unbounded retention is now opt-out, not gone.**
> `NullBackend` accumulates in a `ResizeArray` and delegates normalization to `Core.Audio.interpret`,
> so the documented `Evidence` = `interpret` equality holds by construction rather than by a
> second hand-maintained clamp. Re-running this section's own reproduction: the per-batch cost is now
> **flat (3/4/3/4/5/1 ms) where it was 1711 → 16881 ms**. `Core.Audio.record`'s misleading comment is
> corrected in place.
>
> **Not fixed: the retention itself.** The effects are still held for the life of the instance, so the
> heap still grows (~5 MB / 33 min). A new `NullBackend.Clear()` lets a long-lived holder bound it,
> and the `.fsi` now documents the retention as a known property — but a game that reached this
> backend through the FR-004 degrade never calls `Clear`, so **that path still accumulates.** Fixing
> it properly means deciding whether a *substituted* Null should record at all (see §4c) — that
> changes the observable semantics of a public member, so it is the owner's call, not a reviewer's.
>
> Guarded by an **allocation-ratio** test, not a timing one: allocation is what a tail-append actually
> wastes, it is byte-deterministic across runs (0.911 every time, where the timing equivalent swung
> to 1.88 on noise), and it needs no large batch — so a regression fails in 5 s instead of 4 m 28 s.
> Verified by mutation: restoring the tail-append fails it at 7.66x against a 2.0 threshold.

**`src/FS.GG.Audio.Core/Audio.fs:79-80`, `src/FS.GG.Audio.Host/Host.fs:494-498`**

`Audio.record` appends to the tail of an F# list:

```fsharp
{ evidence with Requested = evidence.Requested @ [ normalize effect ] }
```

`Audio.fs:77-78` justifies the O(n) append: *"Requested is a small per-frame batch, so the O(n)
append is not a hot path."* **That holds for `interpret`, and is false for `NullBackend`.**
`NullBackend.T` folds every effect it is ever given into one `evidence` value that is **never reset
and has no clear API** — so `n` is not a per-frame batch, it is *every effect played for the
lifetime of the process*, and each play copies the entire accumulated list.

**Proven and measured.** One sfx per frame at 60 fps into `NullBackend`:

```
total frames   batch ms       retained effects
20000          1711           20000
40000          4743           40000
60000          7481           60000
80000          10664          80000
100000         13848          100000
120000         16881          120000
```

The same 20 000 plays cost **1.7 s at the start and 16.9 s after 120 k frames — a 10× slowdown, and
still growing.** 120 k frames is ~33 minutes of play.

**Impact, and why it matters more than it looks.** `NullBackend` is not a test fixture. It is the
**default** in the README quick-start (`README.md:33`), the backend `Engine.create` is demonstrated
over, and — decisively — **the target of the FR-004 degrade**: `OpenAlBackend.create` substitutes it
whenever no device opens. So the player this hurts is exactly the one FR-004 exists to protect:
someone with no sound card or a dead driver, whose game was meant to degrade *gracefully* to
silence, instead gets one that slows down the longer it runs. The degrade path converts "no audio"
into "no audio, plus a compounding performance bug."

Note the heap stays modest (~61→67 MB) — the retained list is only a few MB. **The cost is CPU, not
memory**: ~2.4 billion cons cells across the run. Do not dismiss this on the heap number.

**Fix.** Two independent changes, both worth making:

1. Accumulate in a `ResizeArray` (O(1) append), materialize the list in the `Evidence` getter.
2. Give `NullBackend` a bound or a reset. A recorder that retains forever is a test affordance
   leaking into production.

At minimum, correct the comment at `Audio.fs:77-78`, which currently reassures the reader with a
claim that is false for the module's biggest caller.

### 3.4 HIGH — OpenAL errors are never checked; the #33 fault latch is blind at the real device

> **Status: FIXED 2026-07-16.** `guarded` now clears OpenAL's error flag, runs the action, and reads
> the flag back — reporting a non-`NoError` code through the latch. The double read is required
> because the flag is global and sticky (it holds the *first* error until read), so an unread error
> from an earlier call would otherwise be attributed to the wrong operation.
>
> **The finding was confirmed against a real device, and it was worse than described.** Verified:
> `BufferData` with a sample rate of 0 → `InvalidValue`; a bogus source handle → `InvalidName`;
> **neither throws.** So the `try/with` saw nothing, `action` returned `true`, and the call fell
> through to `Succeeded` — the latch did not merely miss the failure, it *affirmatively recorded the
> device as healthy on a call that had just failed*.
>
> The message wording is corrected with it: "the audio device **threw** on X … rather than
> **crashing**" was accurate only for the rarest leg and false for the common one. It now says
> "failed", and describes the containment rather than a crash that was never going to happen.
>
> Guarded by a **real-device test driven through the public surface** — a WAV declaring sample rate 0,
> which `tryParse` passes (it gates on channels/bits, not rate) and OpenAL rejects with
> `InvalidValue`. Mutation-verified: with the check removed, the captured stderr is **empty**.
>
> *Remaining, minor:* `tryParse` still accepts `sampleRate = 0`, so that asset reaches the device and
> is diagnosed by #33 rather than by the more useful #28 asset diagnostic (which could name the id).
> It is no longer silent, which was the bug; naming it better is a follow-up.

**`src/FS.GG.Audio.Host/Host.fs:504-790`** — verified: **`alGetError` / `GetError` appears nowhere in
the file.**

This is the most architecturally significant finding in the report, because it undermines a system
the repo has invested heavily and thoughtfully in.

**OpenAL does not raise exceptions. It sets an error code.** Silk.NET's `AL` is a thin binding: it
does not translate `AL_INVALID_VALUE`, `AL_INVALID_OPERATION` or `AL_OUT_OF_MEMORY` into .NET
exceptions. So `guarded` (`Host.fs:623-627`) —

```fsharp
try
    if action () then deviceDiagnostics.Succeeded op
with error ->
    deviceDiagnostics.Report(op, error)
```

— catches only .NET/marshalling faults (a missing native library, a bad pointer). **It cannot see an
AL error at all.** Concretely:

- `al.BufferData` rejecting a malformed buffer → `AL_INVALID_VALUE` → **no exception**. `action`
  returns `true`. `guarded` calls `Succeeded`, which *ends the fault run* — actively reporting the
  device as healthy on a call that just failed.
- `al.GenSource()` returning `0` at a device whose real source ceiling is below the hard-coded 240
  (`Host.fs:548-549` guesses *"commonly ~256"* and never asks the device) → silent, and the invalid
  handle is then pooled and reused forever.
- Rebuffering a stolen source that isn't fully `Stopped` → `AL_INVALID_OPERATION` → silent.

So a real device that is failing every call reports as *working*, and the persistent-fault
escalation at `Host.fs:396` never fires. This is precisely the silent-failure class that
`DeviceDiagnostics`' ~130 lines of design commentary exist to abolish — surviving underneath the
abolition, in the one layer that touches actual hardware.

The reason it has gone unnoticed is §5: nothing in the suite ever reaches `BufferData` or `GenSource`
at a real device, so no AL error has ever had the chance to go unreported.

**Fix.** Check `al.GetError()` after each device call inside `guarded`, and route a non-`NoError`
code through `deviceDiagnostics.Report` as a synthesized exception (or, better, widen
`DeviceDiagnostics.Report` to take a reason string so an AL code doesn't need to masquerade as an
`exn`). Also query `ALC_MONO_SOURCES` rather than hard-coding 240.

### 3.5 MEDIUM-HIGH — 3D panning ignores depth; 3D is effectively a binary L/R switch

> **Status: FIXED 2026-07-16.** `pan` is now `dx / distance` — the sine of the azimuth — a pure
> direction that does not vary with distance. `RefDistance` no longer doubles as the pan width, and
> the `.fsi` now says so on `SpatialConfig` ("Attenuation ONLY"). Re-running this section's own
> reproduction: the 2.3°-off source panned **+1.00 before, +0.04 after**; on-axis sources still pan
> ±1.00. The sample transcript is byte-identical (all its 3D sources are on-axis).
>
> `raw`, not `capped`, is used for the divisor: `MaxDistance` caps how far away a source *sounds*,
> and using it here would bend the direction it comes *from*.
>
> **A second bug was found while fixing this: `Voice.Pan` could be `nan`.** `clampUnit` was not
> NaN-total (`nan < -1.0` and `nan > 1.0` are both false, so it returned `nan` unchanged), breaking
> the `[-1, 1]` the `.fsi` promises. Now folds to `0.0` (centred) — the same answer
> `Host.Spatial.panToPosition` already gives, so the two agree on nonsense rather than one relying on
> the other to clean up.
>
> **That bug also exposes an error in this report's own §2.** The fuzz reported there claimed *"every
> `Pan` in `[-1,1]`"* as verified. It used `NullBackend`, which is **not** an `IMixingBackend` — so
> every `PlaySfx3D` took the degrade path and `spatial` was never called once. The fuzz proved
> `Engine.step`'s totality (that part stands) and proved **nothing whatever** about the pan law. Re-run
> against a real mixing backend, it found **7 invariant violations** immediately. A test that cannot
> reach the code it claims to cover is the report's own recurring criticism, made by the report.
>
> Mutation-verified both ways: restoring `dx / RefDistance` fails the angle test (`1.0` where `< 0.05`
> was required) *and* the strict-monotonicity test (`Expected a (1.0) to be less than b (1.0)` — the
> plateau). The monotonicity assertion is strict for exactly that reason; a non-strict `<=` passed the
> old law, since a saturated plateau is technically non-decreasing.

**`src/FS.GG.Audio.Engine/Engine.fs:97-105`**

```fsharp
let pan = clampUnit (if config.RefDistance <= 0.0 then dx else dx / config.RefDistance)
```

Pan is computed from the **lateral offset alone, divided by `RefDistance`**. The source's actual
distance — specifically `dz`, how far *ahead* it is — never enters. Pan should be a function of the
*angle* to the source (`dx / distance`, the sine of the azimuth), not of `dx` scaled by a
distance-attenuation constant.

**Depth is ignored.** Proven, listener at origin, default `RefDistance = 1.0`:

```
2m to the right, level with listener       azimuth = +90.0 deg  ->  pan = +1.00   (correct)
2m right, 50m straight AHEAD               azimuth =  +2.3 deg  ->  pan = +1.00   (wrong)
```

**`RefDistance` silently doubles as the pan width.** At the default of `1.0`, *any* source more than
one world unit off-axis is fully panned — so for a game in metres, essentially every 3D sound is
hard L or hard R with nothing between. `Engine.fsi:6-7` documents `SpatialConfig` as
*"Distance-attenuation configuration"* and never mentions it also sets the pan law, so a product
tuning it for attenuation silently changes its panning.

**Why the tests miss it.** `EngineTests.fs:131` and `:354` are the only pan assertions, and both
assert the *saturated* case (`pan = +1.0` for an emitter on the `+x` axis). They confirm the clamp
fires. No test places a source off-axis-but-ahead — the only geometry that distinguishes a correct
pan law from this one. **The suite validates the bug.** The sample's own output (`pan=±1.00` for
sources at ±3 m) then presents the saturation as the intended demonstration of 3D.

**Fix.**

```fsharp
let dist = sqrt (dx*dx + dz*dz)
let pan = if dist <= 0.0 then 0.0 else clampUnit (dx / dist)   // sine of azimuth
```

Distance-independent, a real continuum, and decoupled from `RefDistance`. Add a test asserting a
distant nearly-ahead source is *near-centre*.

### 3.6 MEDIUM — `fadeBus` with a NaN duration silences a bus permanently

**`src/FS.GG.Audio.Engine/Engine.fs:120-126`**

`nan <= 0.0` is `false`, so a NaN duration installs a fade with `Duration = nan`. Then:

- `advance` (`Engine.fs:86`): `Elapsed >= nan` is always false → **the fade never completes and is
  never removed.**
- `baseOf` (`Engine.fs:67`): `Elapsed / nan` = `nan` → the progress clamp `if p < 0.0 … elif p > 1.0`
  passes NaN straight through → `applyCurve` returns `nan` → `busGain` = `clamp01 nan` = **`0.0`**.

**Proven:**

```
Music gain after NaN fade, 1 frame : 0.000000
Music gain after 1000 more frames  : 0.000000   (expected 1.0)
```

The bus is **silent forever**, no diagnostic, recoverable only by a `SetBusVolume` that happens to
cancel the fade. Exactly the "unexplained silence" class #33/#34 was written to eliminate,
reintroduced through a different door.

This is *not* reachable via `Engine.step` (the fuzz confirms `step` is total — `max 0.0 nan`
correctly yields `0.0`). It is reachable through the public `Engine.fadeBus`, whose `seconds` is a
plausible NaN source in computed real-world code. `crossFade` (`Engine.fs:128-136`) has the identical
structure and the same hole. The `.fsi` documents `seconds <= 0` as immediate and says nothing about
NaN.

**Fix.** Treat non-finite as immediate in both, matching the documented `<= 0` leg:

```fsharp
if not (Double.IsFinite seconds) || seconds <= 0.0 then (* immediate *) else ...
```

Belt and braces: make `applyCurve`'s clamp NaN-total (`if not (p >= 0.0) then 0.0`), so no future
caller can reintroduce a NaN gain.

*(Related, minor: `Duck` with NaN `ms` installs an entry that never expires — harmless, since gain is
unaffected and the dictionary is bounded to 5 buses, but it's the same missing guard at
`Engine.fs:147`.)*

### 3.7 MEDIUM (design) — `crossFade` overrides the player's bus volume

**`src/FS.GG.Audio.Engine/Engine.fs:136`** — `EndG` is hard-coded to unity rather than the target
bus's current base gain, so cross-fading *onto* a bus ramps it to full regardless of the player's
slider.

**Proven:**

```
Ambient before crossfade : 0.300000     (player's slider at 30%)
Ambient after crossfade  : 1.000000     (slider overridden)
```

And it *stays* there — `advance` commits `EndG` into `baseGain` on completion (`Engine.fs:88`).

Unlike the rest, **this one is documented**: `Engine.fsi:82-83` says `toBus` *"ramps from 0 up to
unity."* So it is a decision, not an oversight, and I flag it as a design concern. But I think the
decision is wrong: a mixer that silently discards a user's volume preference is a bug from the
player's seat regardless of what the signature file says, and the equal-power constraint is
satisfied just as well by ramping to the bus's own base gain.

**Suggested.** `EndG = baseGain.[toBus]`, `.fsi` updated to match. If it's deliberate for a reason I
can't see, the `.fsi` should at least warn that `crossFade` destroys the target bus's configured
volume.

### 3.8 MEDIUM — No thread safety, and no documentation saying so

Verified: **zero** synchronization primitives across `src/` (no `lock`, `Concurrent*`, `Interlocked`,
`volatile`), and **zero** mentions of threading in any `.fsi`.

Everything that matters is unsynchronized mutable state: `Engine.T`'s `baseGain`/`fades`/`ducks`
dictionaries plus `listener`/`music`/`lastVoices`; `OpenAl.Backend`'s `musicSource`,
`appliedMusicGain`, `busGains`, and the `BufferCache`/`VoicePool` collections; `Audio.play`'s
process-global `warnedRawDrop` (`Host.fs:456`); the diagnostic `HashSet` latches.

Single-threaded use is a perfectly reasonable design for a game-audio mixer. The problem is that
**nothing says so**, while the surface invites concurrent use: `Audio.Cmd.ofEngine`
(`Elmish.fs:35-44`) returns a `Cmd` executed by the Elmish runtime, on whatever thread it runs
effects on — which a product has no reason to assume is its render thread. Two threads in
`Engine.step` will corrupt a `Dictionary` (torn resize → potential infinite loop on read, a
well-known .NET failure mode) with no diagnostic.

Additionally, OpenAL contexts are thread-affine and `alcMakeContextCurrent` is process-wide in
OpenAL Soft: constructing a second `OpenAlBackend` silently steals the current context from the
first.

**Fix.** Cheapest correct action is a documentation contract: state on `Engine.T` and `IAudioBackend`
that instances are **not thread-safe** and must be driven from one thread; warn on `Cmd.ofEngine`;
note the single-instance constraint on `OpenAlBackend.create`. Add locks only if a real consumer
needs them.

### 3.9 MEDIUM (legal) — LGPL native ships inside an MIT-declared package

> **Status: FIXED 2026-07-16.** `THIRD-PARTY-NOTICES.md` added and **packed into the nupkg** of every
> package that redistributes the native, so the notice travels with the bytes it is about. Host's,
> Engine's and Elmish's `<Description>` (what nuget.org actually renders) now name the LGPL native;
> the root README's dependency table names `Silk.NET.OpenAL.Soft.Native`; Host's package README says
> it too.
>
> **This finding understated the blast radius.** It said the exposure "widens to `FS.GG.Audio.Elmish`".
> It widens to **`FS.GG.Audio.Engine` as well** — the lockfiles list the native as `CentralTransitive`
> for *both*. Three of the four packages put `libopenal` into a consumer's output, not two. Only Core
> is clean.
>
> Two facts confirmed that the finding did not have: the package declares `LGPL-2.0-or-later` **and**
> `requireLicenseAcceptance = true`, and it ships prebuilt binaries for seven runtime identifiers.
>
> The notice is deliberately **not** packed into Core: Core has no native dependency (FR-006), so the
> claim would be false, and a licence notice that overclaims is its own kind of wrong.
>
> Gated in `gate.yml` alongside the readme assertion, in **both** directions — the three must carry it
> and mention LGPL in the nuspec; Core must not carry it. Verified against real `dotnet pack` output,
> and mutation-verified: dropping the property from Engine drops the notice, and the gate fails.
>
> The content reflects the project's own recorded reasoning (DEC-001, `work/002-audio-host/charter.md`)
> rather than inventing a rationale. It states facts and mechanism, and explicitly says it is not legal
> advice — **a human should still review the wording before the next publish.**


**`Directory.Packages.props:13-14`, `Directory.Build.props:60`, `src/FS.GG.Audio.Host/README.md`**

`FS.GG.Audio.Host` stamps `<PackageLicenseExpression>MIT</PackageLicenseExpression>` and pulls
`Silk.NET.OpenAL.Soft.Native`, which places an **LGPL** OpenAL Soft binary into every consumer's
`runtimes/*/native`. There is **no `NOTICE`, no `THIRD-PARTY-NOTICES`** file; `LICENSE` (MIT) is the
only one in the repo.

The compliance *argument* exists and is sound — dynamic linking plus a replaceable binary — but it
lives exclusively where no consumer will read it: `Directory.Packages.props:13-14` and the internal
SDD artifacts under `work/002-audio-host/`. Every consumer-facing surface is silent:

- `FS.GG.Audio.Host.fsproj:9` (the nuget.org `<Description>`) — no mention of LGPL or OpenAL Soft.
- `src/FS.GG.Audio.Host/README.md` — rendered on nuget.org; "LGPL" appears **zero** times; closes
  with *"MIT — see LICENSE."*
- Root `README.md:13` — lists Host's dependencies as "Core, Silk.NET.OpenAL", **omitting
  `Silk.NET.OpenAL.Soft.Native` entirely** — the one package carrying the LGPL binary.

The irony is sharp: `work/002-audio-host/charter.md:50-51` explicitly reasons about *"the LGPL-clean
path for closed-source games."* That is exactly the reader who needs this disclosed and exactly the
one who won't get it. LGPL §4(d)/§6 compliance turns on *informing the recipient*; the analysis is
done, it simply isn't delivered. This widens to `FS.GG.Audio.Elmish`, which inherits the native
transitively.

**Fix.** Add `THIRD-PARTY-NOTICES.md`, pack it into Host's and Elmish's nupkg, add a line to Host's
`<Description>` and `README.md`, correct the dependency table at `README.md:13`. Low effort; the only
finding here with legal weight.

### 3.10 ~~LOW — The api-surface baseline is gated by nothing~~ — **THIS FINDING WAS WRONG**

> **Retracted 2026-07-16.** The baseline **is** gated, by a test — `EngineTests.fs:360`, *"committed
> .fsi baselines match the sources, no drift (FR-009)"* — which `gate.yml` runs like any other. It
> compares `docs/api-surface/<pkg>/<name>.fsi` against `src/<pkg>/<name>.fsi` byte-for-byte and fails
> the gate on drift.
>
> **How the error was made, since it is instructive.** The check was searched for in
> `.github/workflows/`, found absent there, and reported as absent everywhere. The enforcement lives
> in the test suite, which is exactly where this repo puts its other structural checks — the same
> place the reviewer had already been reading. It is the report's own recurring criticism turned on
> itself: a conclusion asserted from one angle without checking whether the thing existed elsewhere.
> It then propagated into three PR descriptions ("nothing gates this — see §3.10") before the gate
> caught a real drift and disproved it.
>
> **The residue is a real, narrower finding, now fixed.** The test checked Core, Engine and Host —
> and **not Elmish**, which has a committed baseline like the other three. So one package's baseline
> could drift with the gate green. `check "FS.GG.Audio.Elmish" "Elmish.fsi"` is added, and verified
> to bite. The per-package list is what let one go missing in the first place.

*(Original finding retained below for the record; its premise is false.)*

`docs/api-surface/**` is currently identical to `src/**/*.fsi` — verified, zero drift — but that is
**discipline, not enforcement**. No workflow compares them; `gate.yml` has no api-surface job, and
`release.yml:167` merely *zips* the baseline into a release asset.

Worse than a stale doc, because the two are consumed differently: `Directory.Build.props:90-95` packs
the **real** `.fsi` into each nupkg under `api-surface/`, while `release.yml:167` ships the
**committed baseline** as the release zip. On drift, one release publishes two disagreeing accounts
of "the public surface" — and the nupkg copy is the one FS.GG.Rendering's generated scaffold mirror
consumes (cross-repo Audio#106).

~~**Fix.** A `diff -r docs/api-surface src` step in `gate.yml`. ~5 lines.~~ Unnecessary — the test
already does this, and a second implementation in YAML would be one more thing to keep in agreement.

### 3.11 LOW — Three documentation claims are false

- **`README.md:48-50`** — *"The real OpenAL device backend is exercised only behind an opt-in manual
  lane, never in the CI assertion path."* **Wrong in both halves.** `gate.yml:154-203` runs it in the
  required check and hard-fails on skip; and **no manual lane exists** — the only `workflow_dispatch`
  in the repo is `release.yml:24`, for publishing. Stale prose describing an architecture that was
  replaced.
- **`gate.yml:145-148`** — claims the null driver makes *"the whole device path — buffer upload,
  source pooling and the #20 voice ceiling, PlayAt/pan, the #28 asset diagnostics, the #33
  device-fault latch — actually execute."* **False** (§5): the gate opens a device and asks it one
  type-test question. Only construction, the `Unresolved` diagnostic leg, and `Dispose` run.
- **`Host.fs:66-67`** — `Wav.tryParse` *"Total — returns None on anything malformed"*. False (§3.1).

### 3.12 LOW — Release-lane holes

- **`release.yml:56`** runs `dotnet test` with **no** `LD_LIBRARY_PATH`, **no** `ALSOFT_DRIVERS`, and
  **no** skip guard. The release verify lane is exactly the vacuous lane `gate.yml` exists to
  prevent — the device test silently skips at the one moment that ships bytes.
- **`release.yml:122`** — the `publish` job runs a **bare `dotnet restore`**, while `verify`
  (`release.yml:52`) uses `--locked-mode`. The graph packed and pushed to nuget.org is not the graph
  `verify` validated. `release.yml` also skips the readme-pack assertion `gate.yml:208-229` performs.
- **`Directory.Build.props:8`** — `RestoreLockedMode` is conditioned on `ContinuousIntegrationBuild`,
  which is **never set anywhere in the repo** and is not set automatically by the SDK from GitHub
  Actions. The condition never evaluates true; the property is dead. That is precisely why the hole
  above is real rather than silently covered. Set it in CI or delete it — as written it reads as a
  guarantee it doesn't provide.

---

## 4. Architecture assessment

**The layering is sound and I would not restructure it.** `Core` (pure vocabulary) → `Host` (device
seam) → `Engine` (mixing) → `Elmish` (authoring bridge) is a clean, honest decomposition.

Done well:

- **The pure/impure boundary is real.** `Core` is genuinely BCL-only and genuinely pure. This is the
  property that makes the whole thing headless-testable, and it is not compromised anywhere I looked.
- **`IMixingBackend` as an optional, feature-detected capability** (`Engine.fs:49`) is right —
  additive, keeps `IAudioBackend`-only backends valid, and the degrade leg (`Engine.fs:159-162`) is
  explicit rather than accidental.
- **The diagnostic families (#20/#27/#28/#33/#34) are excellent** and unusually thoughtful. Making
  them device-free value-producers rather than prints is what lets the failure legs be asserted
  headless; the reasoning at `Host.fs:598-602` about lambdas vs. partial application and
  `Console.SetError` is the kind of detail usually learned the hard way and then forgotten. §3.4 is
  not a criticism of this design — it is the observation that the design stops one layer short of
  the hardware.
- **`Engine`'s `reached` flag** (`Engine.fs:181-204`) is subtle and correct: a frame that made no
  device call is evidence of nothing and must not end a fault run. The comment explaining it is
  better than most design docs.
- **`Backend.kindOf` refusing to guess `DeviceBacked`** for unknown backends (`Host.fs:830-834`) is
  the right fail-closed default.

Three observations, descending in importance:

**(a) The `Engine`/`Host` split leaves the spatial model split too.** `Engine` owns attenuation and
pan; `Host`'s `Spatial.panToPosition` re-derives a position *from the pan*. That round-trip
(direction → scalar pan → synthetic unit-circle position) discards elevation and true azimuth, and
it is where §3.5 hides. The seam carries less information than both sides have. If 3D fidelity ever
matters, pass the listener-relative direction across `IMixingBackend` rather than a scalar pan.

**(b) `y` is discarded throughout** (`Engine.fs:153`; `spatial` uses only `x`/`z`). Documented as
DEC-001 and defensible for a planar game — but `playSfx3D` *takes* a `y` and silently ignores it,
which is a small trap. Name it in the `.fsi` doc for `playSfx3D` itself, not only in the decision
record.

**(c) The record-only backend is doing double duty** as both test fixture and production degrade
target, and §3.3 is what happens when a design tuned for the first meets the second. These are
arguably two types with two lifetimes: a `RecordingBackend` (retains everything, for tests) and a
`SilentBackend` (retains nothing, for the degrade).

---

## 5. Testing assessment

69 tests, all passing, 0 skipped, ~320 ms. The suite's *design* is good and in places unusually
self-aware — `HostTests.fs:139-140` and `:181-185` explicitly kill their own tautologies, and
`ElmishTests.fs:156-159` orders a latch check so a false positive is observable, which is genuinely
sharp test design. `VoicePool`/`BufferCache`'s fake `Ops`/`create` seams are exactly the right way to
exercise device-shaped logic headless: 7 tests covering memo, per-id keying, failed-upload-not-cached,
reclaim/reuse, steal-at-ceiling (asserting victim identity), the ceiling clamp, and `DisposeAll`.
**That is the model the rest of the file should follow.**

### The Host coverage number is misleading

| Project | Src (code lines) | Tests | Verdict |
|---|---|---|---|
| Core | 83 | 10 + 2 FsCheck props | Dense, appropriate |
| Engine | 256 | 21 | Good — envelopes, degrade, determinism, #33 legs |
| Elmish | 56 | 9 | Over-covered for a delegation shim |
| **Host** | **851 (472 code)** | **29** | **Looks healthy; isn't** |

Host's 29 tests cluster on the device-free seams (~66% of the file, well covered). `module private
OpenAl` (`Host.fs:504-790`) is **160 code lines = 34% of Host**, and gets **two type tests**.

### The device lane is built but not filled

Both "device present" tests pass a resolver returning `None` for everything (`HostTests.fs:172-173`,
`:211-212`) and then assert `backend :? IMixingBackend` (`:187`, `:217`) — a **static type fact**,
true at compile time, unfalsifiable unless someone deletes an interface declaration. `sampleWav()`
(`HostTests.fs:21`) is fed only to `Wav.tryParse` (`:263`) and **never to a backend**.

So what actually executes on CI is: `alcOpenDevice` → `CreateContext` → `MakeContextCurrent` → the
`Unresolved` diagnostic leg → `Dispose`. **No buffer is uploaded. No source is generated. `PlayAt`
never reaches a real buffer.** Zero coverage, specifically, for: `bufferFormat` (a Mono8↔Stereo8
transposition would ship), `uploadBuffer`, `resolveBuffer`, `soundBuffer`/`trackBuffer`,
`musicGain`/`applyMusicGain` and the `appliedMusicGain` memo, `playOneShot`'s ceiling latch, `Play`'s
per-effect legs, music source reuse, and `Dispose`.

The sharpest example: `configureAndPlay` (`Host.fs:677-685`) sets `SourceRelative = true` and
`RolloffFactor = 0.0`. **That is the entire premise `Spatial.panToPosition` rests on** — the pure
function is tested five ways (`HostTests.fs:227-260`); its contract with the device (distance model
off) is tested zero ways. Delete `RolloffFactor = 0` and every test still passes while every 3D voice
is silently double-attenuated.

The mechanism itself is real — I verified all three legs: `LD_LIBRARY_PATH` + `ALSOFT_DRIVERS=null`
→ `DeviceBacked`; `ALSOFT_DRIVERS=bogus` → `RecordOnly` with the test reporting `1 ignored`; and the
awk skip-guard at `gate.yml:192-202` does fire. **The infrastructure is built, verified, and paid
for. The assertions to fill it were never written** — a gate that reports green over a subject it
barely constructed, which is the very defect the comment block above it congratulates itself for
having killed.

### Every serious finding lives where no test looks

| Finding | Why the suite misses it |
|---|---|
| §3.1 WAV hang | `Wav.tryParse`'s two malformed inputs (`[|1uy;2uy;3uy|]`, `"NOTAWAVEFILE…"`) both return at the length/magic guard (`Host.fs:71`) **before the chunk loop is entered**. The parser has zero malformed-input coverage. |
| §3.2 non-PCM noise | No test supplies a non-PCM WAV. |
| §3.3 Null growth | No test plays enough effects for the quadratic to appear (needs ~20 k). |
| §3.4 AL errors | Nothing ever reaches `BufferData`/`GenSource` at a real device, so no AL error has had a chance to go unreported. |
| §3.5 Pan law | Both pan assertions assert the *saturated* case. The suite validates the bug. |
| §3.6 NaN fade | No non-finite value is ever passed to `fadeBus`/`crossFade`. |

**The highest-value change is not more tests — it is adversarial tests**, and the infrastructure
already exists. FsCheck 3.3.3 is referenced (`Directory.Packages.props:24`) and already used in Core.
Three property tests would have caught four findings:

1. `Wav.tryParse` **terminates** on arbitrary `byte[]` (random-bytes generator finds §3.1 fast;
   termination is the property that matters) and rejects any non-PCM `wFormatTag` (§3.2).
2. `fadeBus`/`crossFade` over arbitrary floats leaves every bus gain finite and in `[0,1]` after
   `step` (§3.6).
3. A 3D voice's `|pan|` is monotonically non-decreasing in azimuth and near-zero for a nearly-ahead
   source (§3.5).

The existing `Engine.step` fuzz result (§2) shows the approach works: `step` is total precisely
*because* it was written defensively, and the fuzz proves it. Extend that same rigour outward to
`fadeBus`, `crossFade` and `Wav.tryParse`, which did not get it.

### Smaller items

- **`EngineTests.fs:180`** — `Expect.isTrue true "step completed without an exception…"`. A literal
  tautology; the same species the Host suite hunted down and removed. Asserts "no throw" only by side
  effect.
- **The sample proves nothing.** It runs (exit 0, deterministic), but drives its own private
  `ConsoleBackend` (`Program.fs:18-35`) — not Null, not OpenAL, no device — asserts nothing, has no
  expected-output check, and is **never executed in CI** (only `dotnet publish`'d at
  `release.yml:171`). It exercises real Engine math and could regress into garbage undetected. It is
  a demo, not evidence. Piping it to a committed golden transcript in `gate.yml` would make it one,
  cheaply.
- No mixed reclaim+steal `VoicePool` test (some stopped, some sounding, at ceiling); no `Acquire`
  after `DisposeAll`.

---

## 6. Recommendations, in priority order

| # | Action | Severity | Effort |
|---|---|---|---|
| 1 | ~~Guard the `Wav.tryParse` chunk walk (`sz <= 0` → stop). Add adversarial WAV tests.~~ **DONE 2026-07-16** — see §3.1. | HIGH | ~~~30 min~~ |
| 2 | ~~Validate `wFormatTag` at `body+0`; reject non-PCM via the existing `UnsupportedFormat` leg.~~ **DONE 2026-07-16** — but *not* as recommended: `UnsupportedFormat` would have lied, and a blunt tag check would have silenced valid `WAVE_FORMAT_EXTENSIBLE` PCM. See §3.2. | HIGH | ~~~30 min~~ |
| 3 | ~~Make `NullBackend` accumulate in a `ResizeArray`; add a bound or reset.~~ **PARTLY DONE 2026-07-16** — quadratic gone, `Clear()` added. Retention on the FR-004 degrade path remains and needs an owner decision (§3.3, §4c). | HIGH | ~~~1 h~~ |
| 4 | ~~Check `al.GetError()` inside `guarded`~~ **DONE 2026-07-16** (§3.4). Querying `ALC_MONO_SOURCES` instead of the hard-coded 240 remains open. | HIGH | ~~~2 h~~ |
| 5 | **Fill the device lane the gate already built** — real resolver + `sampleWav()`, driving `Play`/`PlayAt`/`PlayMusic`/`SetBusGain`/`Dispose`. **STARTED 2026-07-16**: §3.4's fix added the first real assertion (a device error code is named), which proved the lane works end-to-end. The rest of the device path is still type-tested only. | HIGH | ~1.5 h |
| 6 | ~~Fix the pan law to `dx / distance`. Add an off-axis-but-ahead test.~~ **DONE 2026-07-16** (§3.5). Also fixed a `nan` `Pan` found in the process, and corrected §2's overstated fuzz claim. | MED-HIGH | ~~~1 h~~ |
| 7 | Treat non-finite `seconds` as immediate in `fadeBus`/`crossFade`; make `applyCurve`'s clamp NaN-total. | MED | ~30 min |
| 8 | ~~Add `THIRD-PARTY-NOTICES.md`, pack into Host/Elmish, fix `README.md:13` dep table.~~ **DONE 2026-07-16** (§3.9) — and into **Engine** too, which the finding missed. Gated both directions. Wording still wants a human/legal read. | MED (legal) | ~~~1 h~~ |
| 9 | Document the single-thread contract on `Engine.T`, `IAudioBackend`, `Cmd.ofEngine`. | MED | ~30 min |
| 10 | Reconsider `crossFade`'s `EndG = 1.0` overriding the target bus's volume. | MED (design) | discuss |
| 11 | Give `release.yml:56` gate.yml's device env + skip guard; add `--locked-mode` at `release.yml:122`. | LOW | ~20 min |
| 12 | Fix `README.md:48-50`, `gate.yml:145-148`, `Host.fs:66-67` — all three describe behavior that isn't. | LOW | ~20 min |
| 13 | ~~Add `diff -r docs/api-surface src` to `gate.yml`~~ **WITHDRAWN — that finding was wrong** (§3.10): `EngineTests.fs:360` already gates it. Elmish was the one package it missed; **added 2026-07-16**. Set or delete `ContinuousIntegrationBuild` — still open. | LOW | ~5 min |
| 14 | Add the three FsCheck property tests from §5; replace `EngineTests.fs:180`. | — | ~2 h |

Items 1–4 are all in `src/`, total roughly a morning, and none require an audio device to fix or to
test. Item 5 is the highest-leverage *testing* change in the list: the expensive part is already
built and verified working.

---

## 7. Closing note on the commissioning concern

The worry was that the repo *"might not work."* On the evidence: **it works.** It builds clean, tests
green, degrades correctly, is deterministic, and its CI is stricter than its own README gives it
credit for. Several of its internal design notes are better than most shipped documentation.

What it has not had is contact with **hostile input** — and every serious finding here came from
about twenty minutes of supplying some. That is the honest shape of the risk: not that the
architecture is wrong, but that a codebase this young (first commit 2026-07-07, nine days ago) has
been tested by its authors rather than by the world.

There is a pattern worth naming, because it recurs across §3.1, §3.2, §3.4 and §5: **this repo is
very good at reasoning about failure and less good at checking that the reasoning reached the
hardware.** The `DeviceDiagnostics` design is excellent and cannot see an AL error. The
`UnsupportedFormat` diagnostic is well-written and never fires. The device lane in CI is ingenious
and asserts a type fact. The `#28` asset diagnostics turn a bad asset into a named message, and a
bad-enough asset hangs the process before it gets there. In each case the thinking is sound and the
last inch to the metal is unverified — which is exactly the inch that testing against real inputs
would have covered.

That is a good problem to have. It is much easier to fix an untested edge than an unsound design,
and there is no unsound design here.
