# SDD + Governance Workflow — Feedback Report #2 (multi-package run)

- **Date:** 2026-07-07 14:05 CEST (2026-07-07T12:05:56Z)
- **Author:** Claude (Opus 4.8), pair-driving with @EHotwagner
- **Scope:** Second hands-on assessment of the FS.GG SDD lifecycle + Governance boundary,
  from driving **`004-audio-engine`** (`FS.GG.Audio.Engine`) charter → ship. Unlike the
  first report (which came from the small, single-package `003-audio-elmish`), this run was
  **large and multi-package**: a new package plus **additive changes to two already-shipped
  packages** (`FS.GG.Audio.Core`, `FS.GG.Audio.Host`), 9 FRs, 3 real design ambiguities, and
  a 26-obligation evidence graph.
- **Environment:** `fsgg-sdd` generator **0.8.0**, report schema **1.1.0**, artifact
  `schemaVersion: 1`, .NET SDK **10.0.301**.
- **Companion:** `2026-07-07-sdd-governance-workflow-feedback.md` (report #1). This one records
  the **deltas** — new findings the bigger run exposed, plus which report-#1 findings
  reproduced. It does not repeat report #1's detail.

---

## 1. Executive summary

The lifecycle scaled to a large, multi-package, shipped-surface-mutating change and still
produced a truthful **`shipReady`** verdict (22 obligations satisfied + 4 honest deferrals, 0
synthetic). The parts that carried real judgement this time — **clarify decisions that actually
shaped the design**, a **signatures-first contract doc**, and **first-class deferrals across a
multi-package change** — are the parts worth celebrating.

The new pain is concentrated where the tool meets *scale* and *shipped contracts*: (a) resolved
clarify decisions get **no task disposition**, blocking `analyze` two stages after the decision
was authored; (b) mutating an **already-shipped public surface** gets no special handling; and
(c) authoring a **26-obligation** evidence graph is almost entirely manual. None of these are
correctness holes — the model stayed honest — but each is friction that a contract-first
framework should absorb rather than push onto the author.

---

## 2. What worked (new or notably better this run)

### 2.1 Clarify earned its place
`003` had no real ambiguities, so `clarify` was boilerplate. `004` recorded three genuine design
forks — the distance-attenuation model, the **backend-seam strategy** (extend the shipped
`IAudioBackend`? → resolved to an *optional* `IMixingBackend` with feature-detection), and the
fade/duck defaults — and resolved each with a `DEC` tag that then flowed into the contract, the
tasks, and the evidence. This is the stage working as designed: pinning consequential decisions
*before* they harden into code.

### 2.2 The contract doc fixed report #1's "mechanical plan" gap
Authoring `work/004-audio-engine/contracts/audio-engine-surface.md` (signatures for the Engine
surface + the additive Core/Host bumps) put the real design in a **clobber-safe, tool-sanctioned**
location — and the evidence graph referenced it (`PC-001` → `EV021`). This is the right answer to
report #1 §3.4: the generated `plan.md` stays mechanical, but the design lives in `contracts/`.

### 2.3 The evidence scaffold defaulted to *honest* this time
`004`'s scaffold produced `kind: missing` / `result: missing` / **blocking** for all 26
obligations — forcing an affirmative classification. That is exactly the posture report #1 §3.3
asked for. (See §3.6 for the catch: it's inconsistent with `003`.)

### 2.4 Deferrals held across a multi-package change
`DEC-004` (Doppler/full-3D) and its checklist mirror (`CR-010`) fanned into four deferral
obligations (`EV019/020/025/026`), each with the required rationale/owner/scope/visibility, and
`ship` reported **22 supported + 4 deferred** without treating the deferrals as failures. The
"ship honest, not green" property survived scale.

### 2.5 Additive-change discipline was tracked
Bumping two shipped `.fsi` surfaces, the drift test + committed baselines caught them, and the
migration-posture note (`EV023`/`PM-001`) recorded "additive, no schema migration, non-breaking."
The bookkeeping is sound — the gap is that it's all *manual* (§3.2, §3.4).

---

## 3. What didn't work (new findings from the larger run)

Ordered by cost to the author.

### 3.1 Resolved clarify decisions get no task disposition → `analyze` blocks — **defect, high**
The task generator fans out per-FR and per-plan-decision tasks but emits **no disposition for a
resolved `DEC-###`**. `analyze` then fails with
`missingDisposition … relatedIds: [DEC-001, DEC-002, DEC-003]`. I had to **hand-edit `tasks.yml`**
to add `decisions: ["DEC-00x"]` (+ `sourceIds`) to the relevant FR tasks. Notes:
- Normalizing the decision-tag format (single `[FR-###]`, no `[AC]`) **did not** help — it isn't a
  grammar problem; the generator simply doesn't create decision-dispositioning tasks.
- `002` shipped with those `decisions:` refs present (almost certainly hand-added by the prior
  author); `003` had zero decisions so never hit it. So this bites **exactly when clarify does its
  job** and records real decisions — the worst place to have a papercut.

### 3.2 Mutating an already-shipped public surface has no first-class handling — **gap, high**
This item additively changed `FS.GG.Audio.Core` and `FS.GG.Audio.Host` — both **shipped**. The
tooling treated it as an ordinary work item: no detection that a committed `.fsi` changed, no
prompt to bump the package version, no registry/ADR reconcile, no "who consumes this" flag. The
only record that this is a contract-relevant capability change is **prose I wrote** in the
charter/spec. For a framework whose thesis is contract discipline, *shipped-surface mutation* is
the highest-value event to make first-class, and today it's invisible to the tool.

### 3.3 The disposition failure surfaces two stages late — **ergonomics, medium**
The decisions were authored at `clarify` (stage 3); the `missingDisposition` error only appeared
at `analyze` (stage 7), after `checklist`, `plan`, and `tasks` all reported success. Detecting it
at `tasks` (where the disposition is actually missing) would avoid the backtrack through three
"green" stages.

### 3.4 Baseline sync is still DIY, now across three surfaces — **gap, medium**
Same as report #1 §3.8, worse at scale: I `cp`-ed three `.fsi` files into `docs/api-surface/**`
and wrote the drift test myself. With a new surface *and* two bumped ones, a manual sync is easy
to get subtly wrong. A `fsgg-sdd surface --update` / `--check` (or an MSBuild target) should own
this.

### 3.5 Evidence authoring at scale is heavy and mostly unassisted — **ergonomics, medium**
9 FRs fanned to **26 obligations**. `--from-tests` seeds exactly one thing: a `verification`
source pointing at the single test file. It cannot know that `FR-006`'s proof is the **Core**
files, that `EV021` is a **contract review**, or that `EV019/020/025/026` are **deferrals**. I
authored the entire 26-entry file by hand. The tool already knows each obligation's origin
(`requirementRefs`/`planDecisionRefs`) and which tasks are accepted-deferrals — it could seed
artifacts from a requirement→file map and pre-classify deferral tasks, instead of leaving a
blank-but-blocking graph.

### 3.6 The scaffold's honesty default is inconsistent between runs — **defect, medium**
Report #1 saw `003` scaffold to `result: pass`; `004` scaffolded to `result: missing`. **Same
tool version.** Non-determinism in the honesty-critical default is itself the smell — the default
should be *reliably* the honest `missing`, every time.

### 3.7 Multi-FR / AC decision tags are silently inert — **minor**
My initial `DEC-002` genuinely spanned `FR-007` **and** `FR-001`; `DEC-001` referenced `AC-005`.
The extra tags were neither an error nor used for anything. A decision that legitimately governs
two requirements can't say so.

---

## 4. Report-#1 findings: reproduced or resolved

| Report #1 finding | Status in the `004` run |
|---|---|
| §3.1 `evidence` rewrites `null` → `"null"` on re-run | **Not hit** — I authored `evidence.yml` once and didn't re-run `evidence`. Latent; avoid re-running the stage after hand-edits. |
| §3.2 auto-generated notes assert stale counts | **Avoided** — I wrote notes by hand. Root cause unaddressed by the tool. |
| §3.3 scaffold defaults to `pass` | **Diverged** — `004` defaulted to `missing` (better), but see §3.6: inconsistent. |
| §3.4 `plan`/`tasks` mechanical | **Reproduced** — `PD-001..009` are still "Plan requirement FR-00x through the plan command contract." Mitigated only by the hand-authored contract doc. |
| §3.5 stage-state cache shows stages done early | **Reproduced** — the projected `stages:` line ran ahead of executed reality again. |
| §3.8 baseline drift unenforced | **Reproduced, worse at 3 surfaces** (see §3.4). |
| §3.10 Governance `notEvaluated` | **Reproduced** — and more pointed: an additive **shipped-surface** change (precisely what a protected-boundary gate should inspect) sailed through with Governance un-evaluated. |

---

## 5. Governance-specific observations

- The boundary held: full `init → ship` with no Governance runtime, config `notEvaluated`,
  nothing blocked. Correct default, again.
- **The shipped-surface change sharpens the case for a preview gate.** The single most
  gate-worthy event this run — additively mutating two published contracts — produced a
  `governance-handoff.json` that nothing evaluated. A **`--dry-run` / simulated gate** over
  `ship.json` (report #1 §4) would let a team *see* how a protected-boundary policy reacts to a
  surface bump before adopting the runtime. Without it, the Governance side of this workflow
  remains assessable only in theory.

---

## 6. Possible improvements (prioritized; ★ = new this run)

**P0**
1. ★ **Disposition every resolved clarify decision automatically** in `tasks` (or let `analyze`
   accept clarify-level disposition) — no hand-editing `tasks.yml` (§3.1).
2. ★ **Make the evidence-scaffold default consistently `missing`** — kill the `003`/`004`
   divergence (§3.6).
3. **Fix `null` → `"null"` evidence re-serialization** (report #1 §3.1) — still latent.

**P1**
4. ★ **First-class shipped-surface-mutation handling**: detect a changed committed `.fsi`,
   classify additive vs breaking, and prompt version bump + registry/ADR + consumer flag (§3.2).
5. ★ **`fsgg-sdd surface --update/--check`** to sync and enforce `.fsi` baselines — retire the
   `cp` + DIY drift test (§3.4).
6. ★ **Richer evidence authoring at scale**: seed per-obligation artifacts from the
   requirement→file map and auto-classify accepted-deferral tasks as deferrals (§3.5).
7. **Fix the stage-state projection** so `stages:` reflects executed-vs-expected (report #1 §3.5).

**P2**
8. ★ **Detect missing decision disposition at `tasks`, not `analyze`** (§3.3).
9. ★ **Support multi-FR decision tags** (§3.7).
10. **Enrich `plan`** beyond one boilerplate decision per FR (report #1 §3.4).
11. **Add a Governance dry-run gate** over `ship.json` (report #1 §4, §5 above).

---

## 7. Bottom line

At 10× the size of the first run, the lifecycle's **backbone held**: staged flow, real clarify
decisions, contract-as-design, FR→evidence traceability, and honest deferrals all scaled, and the
verdict stayed truthful (22 satisfied / 4 deferred / 0 synthetic). The work now is to stop making
the **author** absorb the scale: auto-disposition decisions (§3.1), make **shipped-surface
mutation** a first-class event (§3.2), own the **baselines** (§3.4), and **assist evidence
authoring** for large obligation graphs (§3.5). Land those and the framework earns trust not just
for a 6-requirement package but for multi-package changes to already-shipped contracts — which is
where contract discipline matters most.

---

*Grounded in the `004-audio-engine` artifacts (`work/004-audio-engine/**`,
`readiness/004-audio-engine/**`) and the implementation committed in `1a708d5`
(`FS.GG.Audio.Engine` + additive `Core`/`Host` bumps, 33/33 tests headless).*
