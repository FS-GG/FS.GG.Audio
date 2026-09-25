namespace FS.GG.Audio.SkillPolicy

module Policy =
    type DeclaredFile = { Path: string; Sha256: string }
    type ObservedFile = { Path: string; Bytes: byte array; IsRegular: bool }

    val canonicalBytes: byte array -> byte array
    val digest: byte array -> string
    val validatePath: string -> Result<string, string>
    val checkClosure: DeclaredFile list -> ObservedFile list -> string list
