# Provisional Linux pinned source capture

Run from the repository root:

```sh
dotnet restore tests/FS.GG.Audio.PinnedCapture.Tests/FS.GG.Audio.PinnedCapture.Tests.fsproj --locked-mode
dotnet run --project tests/FS.GG.Audio.PinnedCapture.Tests/FS.GG.Audio.PinnedCapture.Tests.fsproj -c Release --no-restore
```

`LinuxPinnedCapture.prepareFromDiskLinux` opens each checkout/source directory
component with `openat` and `O_NOFOLLOW`. It lists entries through the pinned
directory descriptor, opens each child with `O_NOFOLLOW | O_NONBLOCK`, checks
its type through `statx` on that descriptor, and reads regular file bytes from
the opened descriptor. A file is capped at 8 MiB, total captured bytes at
64 MiB, and entries at 4096. It passes those bytes to the existing pure
staging plan. This path has no output argument and never stages files.

The fixture deterministically reproduces the earlier path-check/read gap:
after `GetAttributes` accepts a regular file, a swap to a symlink makes
`ReadAllBytes` read outside bytes. It then proves an already opened file and
directory descriptor retain the original source after the same swaps. It also
refuses static source/file/directory symlinks and a FIFO without blocking.

A separate red-before control added an undeclared file after the held source
directory's one-pass name scan. The former capture returned a valid plan while
the extra file remained present. The reader now compares opened-directory
identity, mtime, and ctime around two scans and again after child capture.
Disposable controls refuse that late file, a same-name rename/restore, and a
product root added after its first scan.

This is Linux-only source evidence. Descriptor pinning closes the pathname
swap for captured bytes. Repeated enumeration and stamps detect observed
roster changes but do not establish an atomic tree snapshot: ABA, filesystem
timestamp limits, mutation after the final check, and concurrent in-place
file writes remain possible. No output rollback, package
bytes, other-platform special-file classification, or installed receiver
parity is proved here.
