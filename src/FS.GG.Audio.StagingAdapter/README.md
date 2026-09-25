# Provisional read-only Audio staging adapter

`ReadOnlyAdapter.prepareFromDisk` enumerates the whole `template/product-skills`
root and snapshots file bytes before passing facts to `Staging.prepare`. It refuses
symlinked source roots, source directories and entries, missing and extra files,
empty extra directories, case collisions, duplicate manifest JSON and unsafe
declared paths through the existing plan. On Linux, a `statx` type check refuses
static FIFOs and other nonregular file entries before `ReadAllBytes`, then
rechecks after the read. Its tests also keep a preexisting
output sentinel unchanged on every refusal.

This is source-only characterization. The adapter does not take an output path,
stage files, replace a directory or publish a package. `File.GetAttributes` and
`File.ReadAllBytes` do not provide an atomic no-follow handle; a later activation
must close that observation/use race, prove special-file classification and
rollback across supported platforms, and compare against the accepted Python
gate. No staging write should
use these observations as authority until those separate controls pass.

Run the focused controls with a locked restore and Release run:

```sh
dotnet restore tests/FS.GG.Audio.StagingAdapter.Tests/FS.GG.Audio.StagingAdapter.Tests.fsproj --locked-mode
dotnet run --project tests/FS.GG.Audio.StagingAdapter.Tests/FS.GG.Audio.StagingAdapter.Tests.fsproj -c Release --no-restore
```
