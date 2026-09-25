open System
open System.IO
open System.Text
open FS.GG.Audio.SkillPolicy
open FS.GG.Audio.StagingAdapter

let bytes (text: string) = Encoding.UTF8.GetBytes text
let original = Array.concat [ [| 0xEFuy; 0xBBuy; 0xBFuy |]; bytes "A\r\n" ]
let digest = Policy.digest original
let manifest id source filePath =
    $"{{\"schemaVersion\":2,\"skills\":[{{\"id\":\"{id}\",\"scope\":\"product\",\"supplied-by\":\"{source}\",\"sha256\":\"{digest}\",\"files\":[{{\"path\":\"{filePath}\",\"sha256\":\"{digest}\"}}]}}]}}"
    |> bytes
let good = manifest "audio" "template/product-skills/audio/" "SKILL.md"
let assertTrue label condition =
    if not condition then failwithf "%s failed" label
    printfn "PASS %s" label
let withTree action =
    let root = Path.Combine(Path.GetTempPath(), "audio-adapter-" + Guid.NewGuid().ToString("N"))
    let source = Path.Combine(root, "template", "product-skills", "audio")
    let output = Path.Combine(root, "output")
    Directory.CreateDirectory source |> ignore
    Directory.CreateDirectory output |> ignore
    File.WriteAllBytes(Path.Combine(source, "SKILL.md"), original)
    File.WriteAllText(Path.Combine(output, "keep.txt"), "untouched")
    try action root source output
    finally Directory.Delete(root, true)
let preserved output = File.ReadAllText(Path.Combine(output, "keep.txt")) = "untouched"
let refuse label prefix manifestBytes mutation =
    withTree (fun root source output ->
        mutation root source
        match ReadOnlyAdapter.prepareFromDisk root manifestBytes with
        | Ok _ -> failwithf "%s: unexpectedly accepted" label
        | Error errors ->
            assertTrue label (errors |> List.exists (fun error -> error.StartsWith(prefix, StringComparison.Ordinal)))
            assertTrue (label + " preserves output") (preserved output))

withTree (fun root source output ->
    match ReadOnlyAdapter.prepareFromDisk root good with
    | Error errors -> failwithf "valid source rejected: %A" errors
    | Ok plan ->
        assertTrue "original bytes snapshotted" (plan.Entries.Head.Bytes = original)
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "changed")
        assertTrue "later source edit cannot mutate plan" (plan.Entries.Head.Bytes = original)
        assertTrue "valid read preserves output" (preserved output))
refuse "missing declared file" "audio:missing:" good (fun _ source -> File.Delete(Path.Combine(source, "SKILL.md")))
refuse "extra file" "audio:undeclared:" good (fun _ source -> File.WriteAllText(Path.Combine(source, "extra.md"), "extra"))
refuse "case collision" "audio:case-collision-observed:" good (fun _ source -> File.WriteAllText(Path.Combine(source, "skill.md"), "extra"))
refuse "file symlink" "symlink:audio:" good (fun root source ->
    File.Delete(Path.Combine(source, "SKILL.md"))
    File.CreateSymbolicLink(Path.Combine(source, "SKILL.md"), Path.Combine(root, "output", "keep.txt")) |> ignore)
refuse "source symlink" "unsafe-source:" good (fun root source ->
    Directory.Delete(source, true)
    Directory.CreateSymbolicLink(source, Path.Combine(root, "output")) |> ignore)
refuse "empty extra directory" "audio:not-regular:" good (fun _ source -> Directory.CreateDirectory(Path.Combine(source, "empty")) |> ignore)
refuse "extra source" "undeclared-source:" good (fun root _ -> Directory.CreateDirectory(Path.Combine(root, "template", "product-skills", "other")) |> ignore)
refuse "escaping declaration" "invalid-source:" (manifest "audio" "../escape/" "SKILL.md") (fun _ _ -> ())
refuse "duplicate JSON" "invalid-manifest" (Encoding.UTF8.GetString(good).Replace("\"schemaVersion\":2", "\"schemaVersion\":1,\"schemaVersion\":2") |> bytes) (fun _ _ -> ())
refuse "source-root parent symlink" "unsafe-source-root:" good (fun root source ->
    let template = Path.Combine(root, "template")
    let physical = Path.Combine(root, "physical-template")
    Directory.Move(template, physical)
    Directory.CreateSymbolicLink(template, physical) |> ignore)
let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let currentManifest = File.ReadAllBytes(Path.Combine(repoRoot, "template", "skill-manifest", "skill-manifest.json"))
match ReadOnlyAdapter.prepareFromDisk repoRoot currentManifest with
| Error errors -> failwithf "current Audio source rejected: %A" errors
| Ok plan -> assertTrue "current committed Audio source plans read-only" (plan.Entries.Length > 0)
printfn "12 read-only adapter cases passed"
