# Provisional Audio skill policy

This nonpackable F# project is an FSC-06 source-only implementation. It is not called by `FS.GG.Audio.Skills`, the manifest generator, a workflow, or a receiver.

`Policy.digest` preserves Audio's existing canonical bytes: remove one leading UTF-8 BOM, then replace each CRLF pair with LF, preserving lone CR and all other bytes. `Policy.checkClosure` compares declared POSIX relative paths and SHA-256 digests with an observed set supplied by a caller. It refuses traversal, duplicate/case-colliding paths, empty or bodyless file sets, undeclared or missing files, nonregular entries and digest mismatch. Non-ASCII paths are provisionally refused because .NET ordinal case comparison cannot prove Python `casefold()` collisions absent; a later adapter needs an exact casefold policy before parity. The caller must classify filesystem entries without following symlinks; this pure reducer does not inspect disk and cannot prove source closure by itself.

`Program.fs` is a read-only black-box test adapter that refuses duplicate JSON keys. Build with `dotnet build src/FS.GG.Audio.SkillPolicy/FS.GG.Audio.SkillPolicy.fsproj -c Release`, then run `python3 tests/FS.GG.Audio.SkillPolicy.Tests/run.py`. The Python suite calculates independent hashes and tests the current committed Audio manifest. Manifest identity, `supplied-by`, source-parent symlinks and source enumeration remain adapter obligations.

Full stager parity depends on the protected acceptance and readback of Audio staging repair PR #312. A later owner item must compare the F# policy against that repaired source and its independent symlink/source-closure corpus, qualify installed package bytes, then separately propose any receiver change. This project makes no such change.
