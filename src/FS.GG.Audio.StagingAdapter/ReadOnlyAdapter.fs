namespace FS.GG.Audio.StagingAdapter

open System
open System.IO
open FS.GG.Audio.SkillPolicy

/// Read-only physical observation for the provisional in-memory staging plan.
/// This does not stage, replace, or delete output and is not a no-follow atomic reader.
module ReadOnlyAdapter =
    let private sourceRoot = [ "template"; "product-skills" ]

    let private attributes (path: string) = File.GetAttributes(path)
    let private isLink (value: FileAttributes) = value.HasFlag(FileAttributes.ReparsePoint)
    let private isDirectory (value: FileAttributes) = value.HasFlag(FileAttributes.Directory)

    let private checkedRoot repoRoot =
        let root = Path.GetFullPath repoRoot
        let components = [ root; Path.Combine(root, sourceRoot[0]); Path.Combine(root, sourceRoot[0], sourceRoot[1]) ]
        for path in components do
            let value = attributes path
            if isLink value || not (isDirectory value) then
                invalidOp $"unsafe-source-root:{path}"
        components[2]

    let private snapshotSource (sourcePath: string) =
        let sourceName = Path.GetFileName sourcePath
        let relativeSource = $"template/product-skills/{sourceName}/"
        let sourceAttributes = attributes sourcePath
        let sourceIsSafe = isDirectory sourceAttributes && not (isLink sourceAttributes)
        let entries = ResizeArray<Staging.SourceEntry>()
        if sourceIsSafe then
            let rec visit directory =
                for path in Directory.EnumerateFileSystemEntries(directory) |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b)) do
                    let value = attributes path
                    let relative = Path.GetRelativePath(sourcePath, path).Replace('\\', '/')
                    if isLink value then
                        entries.Add { Path = relative; Bytes = Array.empty; IsRegular = false; IsSymlink = true }
                    elif isDirectory value then
                        let children = Directory.EnumerateFileSystemEntries(path) |> Seq.toArray
                        if children.Length = 0 then
                            entries.Add { Path = relative; Bytes = Array.empty; IsRegular = false; IsSymlink = false }
                        else
                            visit path
                    else
                        // Read only after classifying this path. A later activation must
                        // use a no-follow handle and revalidate inode identity to close
                        // the observation/use race before any output write.
                        let bytes = File.ReadAllBytes path
                        let after = attributes path
                        if isLink after || isDirectory after then
                            invalidOp $"source-changed-during-read:{path}"
                        entries.Add { Path = relative; Bytes = bytes; IsRegular = true; IsSymlink = false }
            visit sourcePath
        { Path = relativeSource
          IsDirectory = sourceIsSafe
          HasSymlinkComponent = isLink sourceAttributes
          Entries = List.ofSeq entries }: Staging.SourceDirectory

    /// Enumerate the entire product source root, including undeclared child entries,
    /// then bind the exact supplied manifest bytes to the existing pure plan.
    let prepareFromDisk repoRoot (manifestBytes: byte array) : Result<Staging.Plan, string list> =
        try
            let root = checkedRoot repoRoot
            let sources =
                Directory.EnumerateFileSystemEntries(root)
                |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b))
                |> Seq.map snapshotSource
                |> Seq.toList
            Staging.prepare manifestBytes sources
        with
        | :? IOException as error -> Error [ $"source-io:{error.GetType().Name}" ]
        | :? UnauthorizedAccessException -> Error [ "source-access-denied" ]
        | :? InvalidOperationException as error -> Error [ error.Message ]
