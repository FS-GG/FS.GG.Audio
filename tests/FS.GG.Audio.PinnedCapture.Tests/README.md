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

This is Linux-only source evidence. Descriptor pinning closes the pathname
swap for captured bytes, but directory enumeration and concurrent in-place
file writes are not an atomic tree snapshot. No output rollback, package
bytes, other-platform special-file classification, or installed receiver
parity is proved here.
