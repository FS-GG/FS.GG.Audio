namespace FS.GG.Audio.SkillPolicy

open System
open System.Collections.Generic
open System.IO
open System.Text.Json

module Program =
    let private readString (value: JsonElement) (key: string) = value.GetProperty(key).GetString()
    let private readBool (value: JsonElement) (key: string) = value.GetProperty(key).GetBoolean()

    let rec private duplicateProperties (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let names = HashSet<string>(StringComparer.Ordinal)
            value.EnumerateObject()
            |> Seq.exists (fun item -> not (names.Add item.Name) || duplicateProperties item.Value)
        | JsonValueKind.Array -> value.EnumerateArray() |> Seq.exists duplicateProperties
        | _ -> false

    let private check file =
        use document = JsonDocument.Parse(File.ReadAllBytes file)
        let root = document.RootElement
        if duplicateProperties root then invalidArg (nameof file) "duplicate JSON property"
        let declared =
            root.GetProperty("declared").EnumerateArray()
            |> Seq.map (fun row ->
                ({ Path = readString row "path"; Sha256 = readString row "sha256" }: Policy.DeclaredFile))
            |> Seq.toList
        let observed =
            root.GetProperty("observed").EnumerateArray()
            |> Seq.map (fun row ->
                ({ Path = readString row "path";
                  Bytes = Convert.FromHexString(readString row "hex");
                  IsRegular = readBool row "regular" }: Policy.ObservedFile))
            |> Seq.toList
        let diagnostics = Policy.checkClosure declared observed
        Console.WriteLine(JsonSerializer.Serialize({| ok = List.isEmpty diagnostics; diagnostics = diagnostics |}))
        if List.isEmpty diagnostics then 0 else 2

    [<EntryPoint>]
    let main args =
        try
            match args with
            | [| "digest"; hex |] ->
                Console.WriteLine(Policy.digest (Convert.FromHexString hex))
                0
            | [| "check-path"; path |] ->
                match Policy.validatePath path with
                | Ok _ -> Console.WriteLine "ok"; 0
                | Error code -> Console.WriteLine($"error:{code}"); 2
            | [| "check-closure"; file |] -> check file
            | _ -> Console.Error.WriteLine "usage: digest HEX | check-path PATH | check-closure JSON"; 2
        with
        | :? ArgumentException
        | :? FormatException
        | :? JsonException
        | :? InvalidOperationException
        | :? Collections.Generic.KeyNotFoundException
        | :? IOException ->
            Console.WriteLine("{\"ok\":false,\"diagnostics\":[\"invalid-input\"]}")
            2
