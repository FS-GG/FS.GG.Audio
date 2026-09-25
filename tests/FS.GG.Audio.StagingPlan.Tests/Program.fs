open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Audio.SkillPolicy

let bytes (value: string) = Encoding.UTF8.GetBytes value
let expected = SHA256.HashData(bytes "A\n") |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
let suppliedBy = "template/product-skills/fs-gg-audio/"
let raw = Array.concat [ [| 0xEFuy; 0xBBuy; 0xBFuy |]; bytes "A\r\n" ]

let manifest id source bodyDigest fileDigest =
    $"{{\"schemaVersion\":2,\"skills\":[{{\"id\":\"{id}\",\"scope\":\"product\",\"supplied-by\":\"{source}\",\"sha256\":\"{bodyDigest}\",\"files\":[{{\"path\":\"SKILL.md\",\"sha256\":\"{fileDigest}\"}}]}}]}}"
    |> bytes

let goodManifest = manifest "fs-gg-audio" suppliedBy expected expected
let goodEntry: Staging.SourceEntry = { Path = "SKILL.md"; Bytes = raw; IsRegular = true; IsSymlink = false }
let goodSource: Staging.SourceDirectory = { Path = suppliedBy; IsDirectory = true; HasSymlinkComponent = false; Entries = [ goodEntry ] }

let assertTrue name condition =
    if not condition then failwithf "%s failed" name
    printfn "PASS %s" name

let refuse name prefix manifestBytes sources =
    match Staging.prepare manifestBytes sources with
    | Ok _ -> failwithf "%s: unexpectedly accepted" name
    | Error issues -> assertTrue name (issues |> List.exists (fun issue -> issue.StartsWith(prefix, StringComparison.Ordinal)))

[<EntryPoint>]
let main _ =
    match Staging.prepare goodManifest [ goodSource ] with
    | Error issues -> failwithf "valid plan rejected: %A" issues
    | Ok plan ->
        assertTrue "manifest bytes bound to plan" (plan.ManifestBytes = goodManifest)
        assertTrue "original BOM and CRLF bytes retained" (plan.Entries.Head.Bytes = raw)
        assertTrue "canonical digest in stage plan" (plan.Entries.Head.Sha256 = expected)
        assertTrue "destination derived" (plan.Entries.Head.Path = "skills/fs-gg-audio/SKILL.md")
    let manifestInput = Array.copy goodManifest
    let bodyInput = Array.copy raw
    let snapshotSource = { goodSource with Entries = [ { goodEntry with Bytes = bodyInput } ] }
    let snapshot =
        match Staging.prepare manifestInput [ snapshotSource ] with
        | Ok plan -> plan
        | Error issues -> failwithf "snapshot fixture rejected: %A" issues
    manifestInput.[0] <- 0uy
    bodyInput.[3] <- byte 'Z'
    assertTrue "plan manifest does not alias mutable input" (snapshot.ManifestBytes = goodManifest)
    assertTrue "stage bytes do not alias mutable input" (snapshot.Entries.Head.Bytes = raw)
    let exposedManifest = snapshot.ManifestBytes
    let exposedBody = snapshot.Entries.Head.Bytes
    exposedManifest.[0] <- 0uy
    exposedBody.[3] <- byte 'Z'
    assertTrue "plan getters do not expose mutable backing bytes" (snapshot.ManifestBytes = goodManifest && snapshot.Entries.Head.Bytes = raw)
    refuse "missing source" "missing-source:" goodManifest []
    refuse "extra source" "undeclared-source:" goodManifest [ goodSource; { goodSource with Path = "template/product-skills/extra/" } ]
    refuse "missing declared file" "fs-gg-audio:missing:" goodManifest [ { goodSource with Entries = [] } ]
    refuse "extra file" "fs-gg-audio:undeclared:" goodManifest [ { goodSource with Entries = [ goodEntry; { goodEntry with Path = "extra.md" } ] } ]
    refuse "source symlink" "unsafe-source:" goodManifest [ { goodSource with HasSymlinkComponent = true } ]
    refuse "file symlink" "symlink:" goodManifest [ { goodSource with Entries = [ { goodEntry with IsSymlink = true } ] } ]
    refuse "nonregular file" "fs-gg-audio:not-regular:" goodManifest [ { goodSource with Entries = [ { goodEntry with IsRegular = false } ] } ]
    refuse "digest mismatch" "fs-gg-audio:digest-mismatch:" goodManifest [ { goodSource with Entries = [ { goodEntry with Bytes = bytes "wrong" } ] } ]
    refuse "legacy body digest mismatch" "body-digest-mismatch:" (manifest "fs-gg-audio" suppliedBy "bad" expected) [ goodSource ]
    refuse "declared file digest mismatch" "fs-gg-audio:digest-mismatch:" (manifest "fs-gg-audio" suppliedBy expected (String.replicate 64 "0")) [ goodSource ]
    refuse "invalid supplied path" "invalid-source:" (manifest "fs-gg-audio" "../escape/" expected expected) [ goodSource ]
    refuse "case-colliding file" "fs-gg-audio:case-collision-observed:" goodManifest [ { goodSource with Entries = [ goodEntry; { goodEntry with Path = "skill.md" } ] } ]
    refuse "unsupported schema" "schema-version" (bytes "{\"schemaVersion\":1,\"skills\":[]}") []
    refuse "malformed manifest" "invalid-manifest" (bytes "{") []
    refuse "duplicate source facts" "duplicate-source-fact:" goodManifest [ goodSource; goodSource ]
    let secondDigest = SHA256.HashData(bytes "B\n") |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    let twoFiles =
        $"{{\"schemaVersion\":2,\"skills\":[{{\"id\":\"fs-gg-audio\",\"scope\":\"product\",\"supplied-by\":\"{suppliedBy}\",\"sha256\":\"{expected}\",\"files\":[{{\"path\":\"SKILL.md\",\"sha256\":\"{expected}\"}},{{\"path\":\"examples/B.md\",\"sha256\":\"{secondDigest}\"}}]}}]}}"
        |> bytes
    let second = { goodEntry with Path = "examples/B.md"; Bytes = bytes "B\n" }
    match Staging.prepare twoFiles [ { goodSource with Entries = [ second; goodEntry ] } ] with
    | Error issues -> failwithf "two-file plan rejected: %A" issues
    | Ok plan -> assertTrue "stage entries sorted by relative path" (plan.Entries |> List.map _.Path = [ "skills/fs-gg-audio/SKILL.md"; "skills/fs-gg-audio/examples/B.md" ])

    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let currentManifest = File.ReadAllBytes(Path.Combine(repoRoot, "template/skill-manifest/skill-manifest.json"))
    use document = JsonDocument.Parse currentManifest
    let row = document.RootElement.GetProperty("skills").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("scope").ToString() = "product")
    let currentSource = row.GetProperty("supplied-by").ToString()
    let currentEntries: Staging.SourceEntry list =
        row.GetProperty("files").EnumerateArray()
        |> Seq.map (fun item ->
            let relative = item.GetProperty("path").ToString()
            let path = Path.Combine(repoRoot, currentSource, relative)
            ({ Path = relative;
              Bytes = File.ReadAllBytes path;
              IsRegular = File.Exists path;
              IsSymlink = File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) }: Staging.SourceEntry))
        |> Seq.toList
    let current = { goodSource with Path = currentSource; Entries = currentEntries }
    match Staging.prepare currentManifest [ current ] with
    | Error issues -> failwithf "current manifest rejected: %A" issues
    | Ok plan -> assertTrue "current Audio manifest plans" (plan.Entries.Length = currentEntries.Length)
    0
