namespace FS.GG.Audio.SkillPolicy

open System
open System.Collections.Generic
open System.Text.Json

/// A read-only staging plan. Filesystem facts must come from an adapter that observes entries
/// without following symlinks; this module never opens a path or stages bytes.
module Staging =
    type SourceEntry = {
        Path: string
        Bytes: byte array
        IsRegular: bool
        IsSymlink: bool
    }

    type SourceDirectory = {
        Path: string
        IsDirectory: bool
        HasSymlinkComponent: bool
        Entries: SourceEntry list
    }

    /// A validated file snapshot. Returning a copy keeps a later staging adapter from
    /// changing the bytes without changing the digest that this plan recorded.
    type StageEntry internal (path: string, bytes: byte array, sha256: string) =
        let snapshot = Array.copy bytes
        member _.Path = path
        member _.Bytes = Array.copy snapshot
        member _.Sha256 = sha256

    type Plan internal (manifestBytes: byte array, entries: StageEntry list) =
        let snapshot = Array.copy manifestBytes
        member _.ManifestBytes = Array.copy snapshot
        member _.Entries = entries

    let private field (item: JsonElement) (name: string) = item.GetProperty(name)
    let private string (item: JsonElement) (name: string) = (field item name).GetString()

    let private invalidPath path =
        match Policy.validatePath path with
        | Error _ -> true
        | Ok _ -> false

    let rec private duplicateProperties (item: JsonElement) =
        match item.ValueKind with
        | JsonValueKind.Object ->
            let names = HashSet<string>(StringComparer.Ordinal)
            item.EnumerateObject()
            |> Seq.exists (fun property -> not (names.Add property.Name) || duplicateProperties property.Value)
        | JsonValueKind.Array -> item.EnumerateArray() |> Seq.exists duplicateProperties
        | _ -> false

    let private selectedRows (raw: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
        let root = document.RootElement
        if duplicateProperties root then invalidArg "raw" "duplicate JSON property"
        if (field root "schemaVersion").GetInt32() <> 2 then
            Error [ "schema-version" ]
        else
            let rows = field root "skills"
            if rows.ValueKind <> JsonValueKind.Array then Error [ "skills-shape" ]
            else
                let selected =
                    rows.EnumerateArray()
                    |> Seq.map (fun row ->
                        if row.ValueKind <> JsonValueKind.Object then invalidArg "raw" "skill row is not an object"
                        row)
                    |> Seq.filter (fun row -> string row "scope" = "product")
                    |> Seq.map (fun row ->
                        let files =
                            (field row "files").EnumerateArray()
                            |> Seq.map (fun file -> ({ Path = string file "path"; Sha256 = string file "sha256" }: Policy.DeclaredFile))
                            |> Seq.toList
                        string row "id", string row "supplied-by", string row "sha256", files)
                    |> Seq.toList
                if List.isEmpty selected then Error [ "no-product-skills" ] else Ok selected

    /// The manifest is parsed from the exact byte buffer returned in the plan. No caller can
    /// supply a different decoded row set. All product files must be present and closed.
    let prepare (manifestBytes: byte array) (sources: SourceDirectory list) : Result<Plan, string list> =
        try
            // Snapshot caller-owned arrays before parsing, checking, or constructing the
            // plan. Both input arrays and returned getters are otherwise mutable aliases.
            let manifestSnapshot = Array.copy manifestBytes
            let sourceSnapshots =
                sources
                |> List.map (fun source ->
                    { source with
                        Entries =
                            source.Entries
                            |> List.map (fun entry ->
                                { entry with Bytes = if isNull entry.Bytes then null else Array.copy entry.Bytes }) })
            match selectedRows manifestSnapshot with
            | Error issues -> Error issues
            | Ok rows ->
                let errors = ResizeArray<string>()
                let seenIds = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let seenSources = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let entries = ResizeArray<StageEntry>()
                for (id, suppliedBy, bodyDigest, files) in rows do
                    if String.IsNullOrEmpty id || id.Contains('/') || invalidPath id then
                        errors.Add($"invalid-id:{id}")
                    elif not (seenIds.Add id) then
                        errors.Add($"duplicate-id:{id}")
                    if String.IsNullOrEmpty suppliedBy || not (suppliedBy.EndsWith("/", StringComparison.Ordinal)) || invalidPath (suppliedBy.TrimEnd('/')) then
                        errors.Add($"invalid-source:{id}")
                    elif not (seenSources.Add suppliedBy) then
                        errors.Add($"duplicate-source:{suppliedBy}")
                    match files |> List.tryFind (fun item -> item.Path = "SKILL.md") with
                    | None -> errors.Add($"missing-body:{id}")
                    | Some body when body.Sha256 <> bodyDigest -> errors.Add($"body-digest-mismatch:{id}")
                    | Some _ -> ()
                    let matching = sourceSnapshots |> List.filter (fun source -> source.Path = suppliedBy)
                    match matching with
                    | [] -> errors.Add($"missing-source:{id}")
                    | [ source ] ->
                        if not source.IsDirectory || source.HasSymlinkComponent then
                            errors.Add($"unsafe-source:{id}")
                        let observed =
                            source.Entries
                            |> List.map (fun item ->
                                if item.IsSymlink then errors.Add($"symlink:{id}:{item.Path}")
                                if obj.ReferenceEquals(item.Bytes, null) then errors.Add($"missing-bytes:{id}:{item.Path}")
                                ({ Path = item.Path; Bytes = item.Bytes; IsRegular = item.IsRegular && not item.IsSymlink }: Policy.ObservedFile))
                        if not (source.Entries |> List.exists (fun item -> obj.ReferenceEquals(item.Bytes, null))) then
                            Policy.checkClosure files observed
                            |> List.iter (fun issue -> errors.Add($"{id}:{issue}"))
                        if errors.Count = 0 then
                            for item in source.Entries do
                                let digest = Policy.digest item.Bytes
                                entries.Add(StageEntry($"skills/{id}/{item.Path}", item.Bytes, digest))
                    | _ -> errors.Add($"duplicate-source-fact:{id}")
                for source in sourceSnapshots do
                    if not (rows |> List.exists (fun (_, path, _, _) -> path = source.Path)) then
                        errors.Add($"undeclared-source:{source.Path}")
                if errors.Count > 0 then
                    errors |> Seq.distinct |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b)) |> Seq.toList |> Error
                else
                    entries |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.Path, b.Path)) |> Seq.toList
                    |> fun planned -> Ok(Plan(manifestSnapshot, planned))
        with
        | :? JsonException
        | :? InvalidOperationException
        | :? KeyNotFoundException
        | :? ArgumentException
        | :? NullReferenceException -> Error [ "invalid-manifest" ]
