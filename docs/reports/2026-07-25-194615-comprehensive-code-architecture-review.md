# Comprehensive code and architecture review

- Repository: `FS-GG/FS.GG.Audio`
- Reviewed revision: `8dc2daa0e57a0c78b0ddc0adcea5f3165efb373c`
- Review completed: 2026-07-25 19:46:15 UTC (21:46:15 CEST)
- Scope: Core, Host, Engine, Elmish integration, public behavior, tests, workflows, and current GitHub checks

## Executive assessment

Audio has a clean layered dependency direction and all current Debug-oriented GitHub checks are green. A full Release run found one failing Elmish architecture assertion: compiler optimization removes the assembly reference that the test expects. The production package still declares Elmish correctly, so this is a test/gate defect rather than missing functionality. Two previously reported runtime design risks remain: unbounded NullBackend command retention and cross-fade resetting a target bus to unity gain.

Overall risk: **medium**. The architecture is understandable and most behavior is well tested, but headless/degraded operation can retain memory indefinitely and Release configuration is not currently represented in CI.

## Architecture

The dependency chain is appropriately one-way: Core contracts feed Host/backend integration, Engine behavior, and finally Elmish effects. Backend selection and diagnostics are explicit, allowing OpenAL operation to degrade to a null backend. That degradation path is production behavior, so its resource semantics matter.

## Evidence

| Suite, Release | Result |
|---|---|
| Core | 10 passed |
| Engine | 26 passed |
| Host | 44 passed |
| Elmish | 8 passed, 1 failed |
| Current-revision GitHub checks | 4 succeeded, 0 failed |
| Failed Elmish test, Debug/filter | Passed |

The failing Release assertion checks assembly-reference metadata, not effect execution. The current shared headless test action runs Debug. This review did not test physical audio devices across platforms or measure real-time latency.

## Findings

### 1. High — the null/degraded backend retains every effect indefinitely

`Host.fs` stores observed effects in a `ResizeArray` until an external caller invokes `Clear`. `NullBackend` is not only a test double; it is also the fallback when OpenAL/device initialization degrades.

A long-running headless server or machine with device failure can therefore accumulate all audio commands for process lifetime.

Recommendation: separate an observing test backend from the production no-op backend. Make production NullBackend stateless or bounded; if diagnostics require history, use a fixed-size ring buffer with dropped-count telemetry.

### 2. Medium — Release tests are not part of the ordinary CI signal

`ElmishTests.fs` asserts that the compiled effect assembly references an assembly whose name contains `Elmish`. In optimized Release output, the direct metadata reference can be removed even though the package reference and behavior are valid. The assertion passes in Debug, matching current CI.

Recommendation: add a Release test job and replace metadata-presence assertions with behavioral/API contract tests or inspect the package/project dependency contract directly. Do not force a useless runtime reference solely to satisfy the test.

### 3. Medium — cross-fade overwrites configured target-bus gain

The cross-fade path ramps the target bus to `1.0`, rather than its configured base gain. A bus intentionally configured below unity changes loudness after a transition.

Recommendation: define cross-fade relative to captured source/target base gains and preserve those gains after completion. Add tests for non-unity and muted targets.

### 4. Low — backend fallback needs bounded diagnostic semantics

Fallback diagnostics are valuable, but observation/history ownership is currently entangled with no-op execution.

Recommendation: document whether effects are audit events or transient commands, then expose bounded diagnostics separately from backend execution.

## Strengths

- Clear Core → Host → Engine → Elmish layering.
- Backend degradation and diagnostics are explicit.
- 84 of 85 Release tests passed; all current live checks are green.
- Engine, host, and effect behavior have focused tests.
- Prior review findings remain traceable rather than disappearing from documentation.

## Recommended order

1. Make the production NullBackend stateless or bounded.
2. Add Release CI and replace the brittle assembly-metadata assertion.
3. Preserve configured gains through cross-fades.
4. Separate diagnostic history from backend command execution.
