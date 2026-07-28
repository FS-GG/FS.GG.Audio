#!/usr/bin/env bash
# The RUNTIME SKILL-ROOT SET this repo declares — asserted on every `Build + test` run, which is a
# REQUIRED check on `main` under enforce_admins, against ADR-0011's two roots.
#
# WHY THIS EXISTS (FS-GG/.github#1720, ADR-0067 §9 phase 4 stage 3). This repo used to commit its
# skills TWICE: the FS.GG.Kit materialize wrote byte-identical copies into `.claude/skills` AND
# `.agents/skills`, and `coordination-coherence` graded both sets against the pin. Phase 4 retired the
# second copy: `.agents/skills` is now a VIEW root (ADR-0065 §A root's three dispositions) whose
# content `scripts/skill-view generate` resolves from `.claude/skills` at checkout. The union of
# `<FsggKitSkillRoots>` and `<FsggKitViewSkillRoots>` is the runtime root set, and it did not change.
#
# WHAT THE RETIREMENT GAVE UP, WHICH IS THE ONLY REASON THIS FILE IS HERE. Before it, a change that
# dropped `.agents/skills` from this repo's runtime contract would have been caught by
# `coordination-coherence`: the root was materialized into, so removing it produced missing files
# against the pin. Now it is not materialized into, and the two gates that could notice both go QUIET
# instead of red. MEASURED ON THIS REPO'S OWN TREE, 2026-07-28, with the root emptied out of
# `<FsggKitViewSkillRoots>` and the directory deleted:
#
#   * `dotnet build .config/kit/FS.GG.Kit.receiver.proj -t:FsggKitMaterialize`
#       -> "FS.GG.Kit: no view skill roots declared (FsggKitViewSkillRoots is empty) — nothing to
#          assert."  Build succeeded, 0 errors.
#   * `coordination-sync --check --against-pin --repo FS-GG/FS.GG.Audio .`
#       -> "OK — all 28 materialized file(s) match the FS.GG.Kit 0.15.0 this tree pins."
#
# Both green, and `.agents/skills` simply gone from the runtime contract. The only observable
# consequence would be that Codex resolves zero skills here and exits 0 saying nothing (ADR-0067 §8's
# measured silent class). That is exactly the trade ADR-0067 §8 forbids — "a rewrite that removes the
# loud failure and adds the quiet one is worse than no rewrite" — so the retirement ships the
# replacement alarm in the same change. This is it.
#
# WHY A STANDALONE SCRIPT AND NOT A COMPOSITION LANE. FS.GG.Templates put the same assertion in
# `tests/composition/lib/`, because it has a `composition` harness that is its required check. This
# repo has no such harness: its required contexts are `Build + test (locked restore, net10.0,
# headless)`, `lock-ranges / lock-ranges` and `kit / coordination-kit`, and only the first is authored
# here. So the alarm is a script this repo owns, invoked as a step in that job. Same assertion, same
# can-fire discipline, wired to the required check this repo actually has.
#
# IT GRADES THE DECLARATION, NOT MSBUILD'S EVALUATION, and that is deliberate rather than lazy. The
# faithful alternative is `dotnet msbuild -getProperty:` on the receiver project, which needs a RESTORE
# of the pinned FS.GG.Kit — a network round-trip added to a REQUIRED check to grade a two-line fact
# this repo authors in its own tree. It would also introduce a second source of truth for the package's
# defaults: a property this repo does NOT declare evaluates to the package default, so a text reader
# would have to restate `.claude/skills;.agents/skills` to interpret an absence, and a restated default
# is the invented-location bug one file over. Requiring BOTH properties to be declared EXPLICITLY
# removes the question: an absence is a RED, not a guess.
#
# Fails CLOSED throughout: an unreadable project, a missing property, a multi-line declaration this
# reader cannot parse, and a union that is not ADR-0011's two are each a failure. "I could not look" is
# never "looked, and fine" (FS-GG/.github#266).

set -euo pipefail

REPO_ROOT="${REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"

# ADR-0011 Decision 1 as amended by ADR-0067 §5 and executed by FS-GG/.github#1636: `.codex/skills` is
# retired, and the runtime root set is these two. SORTED, so the comparison is set equality and not an
# accident of which property each root is declared in — moving a root between the two properties is a
# legal disposition change (ADR-0065) and must NOT red this.
FSGG_RUNTIME_ROOTS_EXPECTED='.agents/skills .claude/skills'

# The receiver project is where both properties live.
FSGG_RECEIVER_PROJ="${FSGG_RECEIVER_PROJ:-$REPO_ROOT/.config/kit/FS.GG.Kit.receiver.proj}"

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); printf '  \xe2\x9c\x93 %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  \xe2\x9c\x97 %s\n' "$1"; }

# msbuild_property <file> <name>
# Echo the text of a single-line `<name>value</name>` element; echo nothing and return 1 when the
# element is absent, empty, or not on one line. Deliberately NOT an XML parser: the one thing this
# needs to distinguish is "declared with a value" from "anything else", and every "anything else"
# lands on the same red. A declaration this cannot read is a declaration a reviewer should reformat.
msbuild_property() {
  local file="$1" name="$2" value
  [[ -r "$file" ]] || return 1
  value="$(sed -n "s|^[[:space:]]*<${name}>\(.*\)</${name}>[[:space:]]*$|\1|p" "$file" | head -1)"
  [[ -n "$value" ]] || return 1
  printf '%s' "$value"
}

# runtime_root_union <file>
# Echo the sorted, space-separated union of <FsggKitSkillRoots> and <FsggKitViewSkillRoots>. Returns 1
# with nothing on stdout when either property is not declared — an undeclared property is the failure
# this alarm exists for, so it must not be silently treated as an empty contribution.
runtime_root_union() {
  local file="$1" live view
  live="$(msbuild_property "$file" FsggKitSkillRoots)"     || return 1
  view="$(msbuild_property "$file" FsggKitViewSkillRoots)" || return 1
  printf '%s;%s' "$live" "$view" | tr ';' '\n' \
    | sed 's|[[:space:]]||g; s|/*$||' | grep -v '^$' | sort -u | paste -sd' ' -
}

# assert_runtime_roots <lane>
assert_runtime_roots() {
  local lane="$1" union
  if ! union="$(runtime_root_union "$FSGG_RECEIVER_PROJ")"; then
    bad "$lane: cannot read the runtime root set from $FSGG_RECEIVER_PROJ — both <FsggKitSkillRoots> and <FsggKitViewSkillRoots> must be declared, each on ONE line. ADR-0067 §9 phase 4 made this repo's second runtime root a generated VIEW, and no other gate can see it leave the contract (see this file's header)."
    return
  fi
  if [[ "$union" == "$FSGG_RUNTIME_ROOTS_EXPECTED" ]]; then
    ok "$lane: runtime skill roots are ADR-0011's two ($union) — the union of <FsggKitSkillRoots> and <FsggKitViewSkillRoots>"
  else
    bad "$lane: this repo's runtime skill roots are '$union', not '$FSGG_RUNTIME_ROOTS_EXPECTED'. A root that leaves this union leaves the runtime contract, and BOTH kit gates stay green while it does: coordination-coherence looks only at <FsggKitSkillRoots>, and FsggKitCheckSkillView reports 'nothing to assert' for an empty <FsggKitViewSkillRoots>. Codex would then resolve zero skills here and exit 0 saying nothing (ADR-0067 §8). If the root set is genuinely meant to change, that is an ADR-0065 §Retiring a root contract migration — amend the record and this constant in the same change."
  fi
}

# assert_runtime_roots_can_fire <lane>
# "Demonstrated, not asserted" (FS-GG/.github#1611 category D: a gate that never fires and a gate that
# always passes are indistinguishable from outside). Entirely offline, entirely local: five fixture
# projects in a temp dir plus one path that does not exist, driving the ASSERTION rather than only the
# predicate, with the counters snapshotted and restored. Driving the assertion is the part that
# matters — a demo that exercises only the predicate survives a mutation of the `bad` arm.
assert_runtime_roots_can_fire() {
  local lane="$1" tmp saved_pass saved_fail proj
  tmp="$(mktemp -d)"
  saved_pass="$PASS" saved_fail="$FAIL"

  local ok_cases=0 fired=0

  # (1) the shape this repo ships: both declared, union is the two roots -> PASS
  proj="$tmp/good.proj"
  printf '<Project>\n  <FsggKitSkillRoots>.claude/skills</FsggKitSkillRoots>\n  <FsggKitViewSkillRoots>.agents/skills</FsggKitViewSkillRoots>\n</Project>\n' > "$proj"
  PASS=0 FAIL=0; FSGG_RECEIVER_PROJ="$proj" assert_runtime_roots "$lane" >/dev/null
  [[ "$FAIL" -eq 0 && "$PASS" -eq 1 ]] && ok_cases=$((ok_cases + 1))

  # (2) the disposition swap: same union, roots declared the other way round -> PASS. This is a legal
  #     ADR-0065 move and reddening it would make the alarm an obstacle to the contract it protects.
  proj="$tmp/swapped.proj"
  printf '<Project>\n  <FsggKitSkillRoots>.agents/skills</FsggKitSkillRoots>\n  <FsggKitViewSkillRoots>.claude/skills</FsggKitViewSkillRoots>\n</Project>\n' > "$proj"
  PASS=0 FAIL=0; FSGG_RECEIVER_PROJ="$proj" assert_runtime_roots "$lane" >/dev/null
  [[ "$FAIL" -eq 0 && "$PASS" -eq 1 ]] && ok_cases=$((ok_cases + 1))

  # (3) THE REGRESSION THIS FILE EXISTS FOR: the view root emptied. Every kit gate is green on that
  #     tree — measured, see the header — and this must not be.
  proj="$tmp/emptied.proj"
  printf '<Project>\n  <FsggKitSkillRoots>.claude/skills</FsggKitSkillRoots>\n  <FsggKitViewSkillRoots></FsggKitViewSkillRoots>\n</Project>\n' > "$proj"
  PASS=0 FAIL=0; FSGG_RECEIVER_PROJ="$proj" assert_runtime_roots "$lane" >/dev/null
  [[ "$FAIL" -eq 1 ]] && fired=$((fired + 1))

  # (4) the property deleted outright -> RED. An absent property must never read as an empty
  #     contribution to the union, which would make the deletion the very thing it silently allows.
  proj="$tmp/deleted.proj"
  printf '<Project>\n  <FsggKitSkillRoots>.claude/skills</FsggKitSkillRoots>\n</Project>\n' > "$proj"
  PASS=0 FAIL=0; FSGG_RECEIVER_PROJ="$proj" assert_runtime_roots "$lane" >/dev/null
  [[ "$FAIL" -eq 1 ]] && fired=$((fired + 1))

  # (5) a THIRD root added without a contract migration -> RED. The alarm is set equality, not a
  #     minimum: ADR-0065 governs adding a root exactly as it governs removing one. `.codex/skills` is
  #     the realistic mistake here — it is retired (ADR-0067 §5) and this repo still holds 16 of its
  #     OWN skills there, which is not the same thing as it being a runtime root.
  proj="$tmp/extra.proj"
  printf '<Project>\n  <FsggKitSkillRoots>.claude/skills;.codex/skills</FsggKitSkillRoots>\n  <FsggKitViewSkillRoots>.agents/skills</FsggKitViewSkillRoots>\n</Project>\n' > "$proj"
  PASS=0 FAIL=0; FSGG_RECEIVER_PROJ="$proj" assert_runtime_roots "$lane" >/dev/null
  [[ "$FAIL" -eq 1 ]] && fired=$((fired + 1))

  # (6) an unreadable project -> RED. "I could not look" is never "looked, and fine".
  PASS=0 FAIL=0; FSGG_RECEIVER_PROJ="$tmp/does-not-exist.proj" assert_runtime_roots "$lane" >/dev/null
  [[ "$FAIL" -eq 1 ]] && fired=$((fired + 1))

  PASS="$saved_pass" FAIL="$saved_fail"
  rm -rf "$tmp"

  if [[ "$ok_cases" -eq 2 && "$fired" -eq 4 ]]; then
    ok "$lane: the runtime-root alarm can fire — 4 of 4 regressions RED (emptied view root, deleted property, extra root, unreadable project) and 2 of 2 legal shapes GREEN"
  else
    bad "$lane: the runtime-root alarm is NOT demonstrably live — $ok_cases/2 legal shapes passed and $fired/4 regressions fired. A gate that cannot fire is not a gate (FS-GG/.github#1611 category D)."
  fi
}

# THE VIEW IS ACTUALLY THERE, not merely declared. The declaration check above cannot see a checkout
# whose view root was never generated; this can. It is scoped to the case where the root exists on
# disk, because `Build + test` runs on a bare checkout that has NOT run the kit materialize — so an
# absent view root here is the NORMAL state of that job, not a regression, and reddening it would make
# this alarm fire on every green build. When the root IS present (a developer tree, or any job that
# materialized first) it must resolve to the live root and expose the same skills.
assert_view_resolves() {
  local lane="$1" view="$REPO_ROOT/.agents/skills" live="$REPO_ROOT/.claude/skills" live_n view_n
  if [[ ! -e "$view" ]]; then
    ok "$lane: no generated view root on this checkout (expected on a bare clone — the kit materialize generates it); declaration graded above"
    return
  fi
  if [[ ! -d "$view" ]]; then
    bad "$lane: .agents/skills exists but is not a directory — a DANGLING view link resolves to zero skills and BOTH runtimes exit 0 saying nothing (ADR-0067 §8)."
    return
  fi
  live_n="$(find "$live" -mindepth 1 -maxdepth 1 -type d | wc -l)"
  view_n="$(find -L "$view" -mindepth 1 -maxdepth 1 -type d | wc -l)"
  if [[ "$live_n" -gt 0 && "$live_n" -eq "$view_n" ]]; then
    ok "$lane: the generated view exposes all $view_n skill(s) the live root holds"
  else
    bad "$lane: the generated view exposes $view_n skill(s) but the live root holds $live_n. A partly-visible view root is ADR-0067 §8's silent class."
  fi
}

printf 'skill-view-roots: the runtime skill-root contract (ADR-0011 / ADR-0065 / ADR-0067 §8)\n'
assert_runtime_roots          "roots"
assert_runtime_roots_can_fire "can-fire"
assert_view_resolves          "view"

printf 'skill-view-roots: %d passed, %d failed\n' "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]] || exit 1
