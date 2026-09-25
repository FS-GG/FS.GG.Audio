namespace FS.GG.Audio.SkillPolicy

open System
open System.Collections.Generic
open System.Security.Cryptography

module Policy =
    type DeclaredFile = { Path: string; Sha256: string }
    type ObservedFile = { Path: string; Bytes: byte array; IsRegular: bool }

    // Audio's existing generator/stager remove one UTF-8 BOM and fold CRLF only.
    let canonicalBytes (bytes: byte array) =
        let offset =
            if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then 3 else 0

        let result = ResizeArray<byte>(bytes.Length - offset)
        let mutable index = offset

        while index < bytes.Length do
            if bytes[index] = 0x0Duy && index + 1 < bytes.Length && bytes[index + 1] = 0x0Auy then
                result.Add 0x0Auy
                index <- index + 2
            else
                result.Add bytes[index]
                index <- index + 1

        result.ToArray()

    let digest bytes =
        SHA256.HashData(canonicalBytes bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let validatePath path =
        if String.IsNullOrEmpty path then
            Error "empty-path"
        elif path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal) then
            Error "absolute-or-trailing-slash"
        elif path.Contains('\\') || path.Contains(':') then
            Error "non-posix-path"
        elif path |> Seq.exists Char.IsControl then
            Error "control-character"
        elif path.Split('/') |> Array.exists (fun part -> part = "" || part = "." || part = "..") then
            Error "unsafe-component"
        else
            Ok path

    let checkClosure (declared: DeclaredFile list) (observed: ObservedFile list) =
        let errors = ResizeArray<string>()
        let declaredPaths = HashSet<string>(StringComparer.Ordinal)
        let declaredFolded = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let observedPaths = HashSet<string>(StringComparer.Ordinal)
        let observedFolded = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for entry in declared do
            match validatePath entry.Path with
            | Error code -> errors.Add($"declared:{code}:{entry.Path}")
            | Ok _ -> ()
            if not (declaredPaths.Add entry.Path) then errors.Add($"duplicate-declared:{entry.Path}")
            if not (declaredFolded.Add entry.Path) then errors.Add($"case-collision-declared:{entry.Path}")
            if isNull entry.Sha256 || entry.Sha256.Length <> 64 || entry.Sha256 |> Seq.exists (fun c -> not (Uri.IsHexDigit c)) then
                errors.Add($"invalid-digest:{entry.Path}")

        for entry in observed do
            match validatePath entry.Path with
            | Error code -> errors.Add($"observed:{code}:{entry.Path}")
            | Ok _ -> ()
            if not entry.IsRegular then errors.Add($"not-regular:{entry.Path}")
            if not (observedPaths.Add entry.Path) then errors.Add($"duplicate-observed:{entry.Path}")
            if not (observedFolded.Add entry.Path) then errors.Add($"case-collision-observed:{entry.Path}")
            if not (declaredPaths.Contains entry.Path) then errors.Add($"undeclared:{entry.Path}")

        for entry in declared do
            if not (observedPaths.Contains entry.Path) then errors.Add($"missing:{entry.Path}")
            for actual in observed do
                if actual.Path = entry.Path && actual.IsRegular && digest actual.Bytes <> entry.Sha256 then
                    errors.Add($"digest-mismatch:{entry.Path}")

        errors |> Seq.distinct |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b)) |> Seq.toList
