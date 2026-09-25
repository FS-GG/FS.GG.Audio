open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open FS.GG.Audio.SkillPolicy
open FS.GG.Audio.StagingAdapter

[<DllImport("libc", EntryPoint = "mkfifo")>]
extern int mkfifo(string path, uint32 mode)

let bytes (text: string) = Encoding.UTF8.GetBytes text
let original = bytes "SOURCE\n"
let outsideBytes = bytes "OUTSIDE\n"
let digest = Policy.digest original
let manifest =
    $"{{\"schemaVersion\":2,\"skills\":[{{\"id\":\"audio\",\"scope\":\"product\",\"supplied-by\":\"template/product-skills/audio/\",\"sha256\":\"{digest}\",\"files\":[{{\"path\":\"SKILL.md\",\"sha256\":\"{digest}\"}}]}}]}}"
    |> bytes
let mutable count = 0
let check label condition =
    count <- count + 1
    if not condition then failwithf "%s failed" label

let withTree action =
    let root = Path.Combine(Path.GetTempPath(), "audio-pinned-" + Guid.NewGuid().ToString("N"))
    let source = Path.Combine(root, "template", "product-skills", "audio")
    let outside = Path.Combine(root, "outside")
    let output = Path.Combine(root, "output")
    Directory.CreateDirectory source |> ignore
    Directory.CreateDirectory outside |> ignore
    Directory.CreateDirectory output |> ignore
    File.WriteAllBytes(Path.Combine(source, "SKILL.md"), original)
    File.WriteAllBytes(Path.Combine(outside, "SKILL.md"), outsideBytes)
    File.WriteAllText(Path.Combine(output, "keep.txt"), "untouched")
    try action root source outside output
    finally Directory.Delete(root, true)

let preserved output = File.ReadAllText(Path.Combine(output, "keep.txt")) = "untouched"
let refused label prefix root output =
    let result = LinuxPinnedCapture.prepareFromDiskLinux root manifest
    check label (match result with Error issues -> issues |> List.exists (fun issue -> issue.StartsWith(prefix, StringComparison.Ordinal)) | Ok _ -> false)
    check (label + " preserves output") (preserved output)

if OperatingSystem.IsLinux() then
    withTree (fun root source _ output ->
        match LinuxPinnedCapture.prepareFromDiskLinux root manifest with
        | Error issues -> failwithf "valid pinned source refused: %A" issues
        | Ok plan ->
            check "valid source captured" (plan.Entries.Head.Bytes = original)
            File.WriteAllBytes(Path.Combine(source, "SKILL.md"), outsideBytes)
            check "later source edit cannot mutate pinned plan" (plan.Entries.Head.Bytes = original)
            check "valid capture preserves output" (preserved output))

    // Deterministic red-before form of #316's GetAttributes/ReadAllBytes gap.
    withTree (fun _ source outside output ->
        let path = Path.Combine(source, "SKILL.md")
        let checkedAttributes = File.GetAttributes path
        check "red-before regular path check" (not (checkedAttributes.HasFlag FileAttributes.ReparsePoint))
        File.Move(path, path + ".parked")
        File.CreateSymbolicLink(path, Path.Combine(outside, "SKILL.md")) |> ignore
        check "red-before pathname read follows swapped link" (File.ReadAllBytes path = outsideBytes)
        check "red-before control preserves output" (preserved output))

    withTree (fun root source outside output ->
        use repository = LinuxPinnedCapture.openDirectoryPath root
        use template = LinuxPinnedCapture.openDirectoryChild repository "template"
        use products = LinuxPinnedCapture.openDirectoryChild template "product-skills"
        use sourceHandle = LinuxPinnedCapture.openDirectoryChild products "audio"
        use fileHandle = LinuxPinnedCapture.openRegularChild sourceHandle "SKILL.md"
        let path = Path.Combine(source, "SKILL.md")
        File.Move(path, path + ".parked")
        File.CreateSymbolicLink(path, Path.Combine(outside, "SKILL.md")) |> ignore
        check "pinned file keeps original bytes after path swap" (LinuxPinnedCapture.readPinnedFile fileHandle = original)
        check "pinned file control preserves output" (preserved output))

    withTree (fun root source outside output ->
        use repository = LinuxPinnedCapture.openDirectoryPath root
        use template = LinuxPinnedCapture.openDirectoryChild repository "template"
        use products = LinuxPinnedCapture.openDirectoryChild template "product-skills"
        use sourceHandle = LinuxPinnedCapture.openDirectoryChild products "audio"
        Directory.Move(source, source + ".parked")
        Directory.CreateSymbolicLink(source, outside) |> ignore
        use fileHandle = LinuxPinnedCapture.openRegularChild sourceHandle "SKILL.md"
        check "pinned directory keeps original child after source swap" (LinuxPinnedCapture.readPinnedFile fileHandle = original)
        check "pinned directory control preserves output" (preserved output))

    withTree (fun root source _ output ->
        let extra = Path.Combine(source, "late.md")
        let hook path =
            if path = "" then File.WriteAllBytes(extra, outsideBytes)
        let result = LinuxPinnedCapture.prepareWithNamesHook hook root manifest
        check "late entry exists" (File.Exists extra)
        let unstable =
            match result with
            | Error issues ->
                issues |> List.exists (fun issue -> issue.StartsWith("source-directory-unstable:", StringComparison.Ordinal))
            | Ok _ -> false
        check "late entry after first scan refuses" unstable
        check "late entry control preserves output" (preserved output))

    withTree (fun root source _ output ->
        let skill = Path.Combine(source, "SKILL.md")
        let hook path =
            if path = "" then
                File.Move(skill, skill + ".parked")
                File.Move(skill + ".parked", skill)
                Directory.SetLastWriteTimeUtc(source, DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        let result = LinuxPinnedCapture.prepareWithNamesHook hook root manifest
        let unstable =
            match result with
            | Error issues -> issues |> List.contains "source-directory-unstable:"
            | Ok _ -> false
        check "same-name rename/restore refuses" unstable
        check "rename/restore control preserves output" (preserved output))

    withTree (fun root _ _ output ->
        let lateRoot = Path.Combine(root, "template", "product-skills", "late")
        let hook path =
            if path = "template/product-skills" then
                Directory.CreateDirectory lateRoot |> ignore
        let result = LinuxPinnedCapture.prepareWithNamesHook hook root manifest
        check "late product root exists" (Directory.Exists lateRoot)
        let unstable =
            match result with
            | Error issues -> issues |> List.contains "source-directory-unstable:template/product-skills"
            | Ok _ -> false
        check "late product root after scan refuses" unstable
        check "late product-root control preserves output" (preserved output))

    withTree (fun root source _ output ->
        let skill = Path.Combine(source, "SKILL.md")
        let changed = bytes "MUTATE\n"
        check "overwrite fixture preserves file length" (changed.Length = original.Length)
        let hook path =
            if path = "SKILL.md" then File.WriteAllBytes(skill, changed)
        let result = LinuxPinnedCapture.prepareWithReadHook hook root manifest
        check "source changed after first fd read" (File.ReadAllBytes skill = changed)
        let unstable =
            match result with
            | Error issues -> issues |> List.contains "source-file-unstable:SKILL.md"
            | Ok _ -> false
        check "in-place overwrite after first pass refuses" unstable
        check "content control preserves output" (preserved output))

    withTree (fun root source _ output ->
        let skill = Path.Combine(source, "SKILL.md")
        let hook path =
            if path = "SKILL.md" then
                File.WriteAllBytes(skill, original)
                File.SetLastWriteTimeUtc(skill, DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        let result = LinuxPinnedCapture.prepareWithReadHook hook root manifest
        let unstable =
            match result with
            | Error issues -> issues |> List.contains "source-file-unstable:SKILL.md"
            | Ok _ -> false
        check "same-byte rewrite after first pass refuses" unstable
        check "same-byte control preserves output" (preserved output))

    withTree (fun root source outside output ->
        let path = Path.Combine(source, "SKILL.md")
        File.Delete path
        File.CreateSymbolicLink(path, Path.Combine(outside, "SKILL.md")) |> ignore
        refused "no-follow file link" "source-open-no-follow:" root output)

    withTree (fun root source outside output ->
        Directory.Move(source, source + ".parked")
        Directory.CreateSymbolicLink(source, outside) |> ignore
        refused "no-follow source link" "source-open-no-follow:" root output)

    withTree (fun root source outside output ->
        Directory.CreateSymbolicLink(Path.Combine(source, "notes"), outside) |> ignore
        refused "no-follow nested directory link" "source-open-no-follow:" root output)

    withTree (fun root _ _ output ->
        let linkedRoot = root + "-link"
        Directory.CreateSymbolicLink(linkedRoot, root) |> ignore
        try refused "no-follow checkout-root link" "source-open-no-follow:" linkedRoot output
        finally Directory.Delete(linkedRoot))

    withTree (fun root source _ output ->
        check "FIFO fixture created" (mkfifo(Path.Combine(source, "pipe"), 0o600u) = 0)
        refused "nonregular FIFO" "nonregular-source:" root output)

    withTree (fun root source _ output ->
        File.WriteAllText(Path.Combine(source, "extra.md"), "extra")
        refused "closed file set" "audio:undeclared:" root output)

    withTree (fun root source _ output ->
        File.WriteAllBytes(Path.Combine(source, "oversize.bin"), Array.zeroCreate<byte> (8 * 1024 * 1024 + 1))
        refused "bounded file read" "source-file-too-large" root output)

    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let currentManifest = File.ReadAllBytes(Path.Combine(repoRoot, "template", "skill-manifest", "skill-manifest.json"))
    match LinuxPinnedCapture.prepareFromDiskLinux repoRoot currentManifest with
    | Error issues -> failwithf "current Audio source refused: %A" issues
    | Ok plan -> check "current committed source captured read-only" (plan.Entries.Length > 0)
else
    check "Linux-only capture refuses on this host"
        (LinuxPinnedCapture.prepareFromDiskLinux "/unused" manifest = Error [ "linux-pinned-capture-unavailable" ])

printfn "Audio Linux pinned capture: %d controls passed" count
