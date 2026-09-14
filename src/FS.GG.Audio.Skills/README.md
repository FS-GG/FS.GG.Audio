# FS.GG.Audio.Skills

This content-only package transports FS.GG.Audio's owner-authored browser product guidance. Consumers use
`$(FsggAudioSkillsContentDir)` from the auto-imported build props to locate `skill-manifest.json` and the
closed `skills/` tree. The SDD receiver selects rows by their manifest predicates, verifies every declared
file digest, and materializes the selected union.

