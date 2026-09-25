namespace FS.GG.Audio.StagingAdapter

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles
open FS.GG.Audio.SkillPolicy
open FS.GG.Audio.SkillPolicy.Staging

/// Linux-only, read-only source capture through no-follow directory and file
/// descriptors. No output directory or package path enters this module.
module LinuxPinnedCapture =
    [<Literal>]
    let private O_NONBLOCK = 0x800
    [<Literal>]
    let private O_DIRECTORY = 0x10000
    [<Literal>]
    let private O_NOFOLLOW = 0x20000
    [<Literal>]
    let private O_CLOEXEC = 0x80000
    [<Literal>]
    let private AT_EMPTY_PATH = 0x1000
    [<Literal>]
    let private maxEntries = 4096
    [<Literal>]
    let private maxFileBytes = 8 * 1024 * 1024
    [<Literal>]
    let private maxTotalBytes = 64 * 1024 * 1024

    [<DllImport("libc", SetLastError = true, EntryPoint = "openat")>]
    extern int private openat(int directory, string path, int flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "statx")>]
    extern int private statx(int directory, string path, int flags, uint32 mask, nativeint buffer)

    let private number (handle: SafeFileHandle) = handle.DangerousGetHandle().ToInt32()

    let private openAt directory name flags =
        if String.IsNullOrEmpty name || name = "." || name = ".." || name.Contains('/') || name.Contains(char 0) then
            invalidOp "source-component-unsafe"
        let descriptor = openat(directory, name, flags ||| O_CLOEXEC ||| O_NOFOLLOW)
        if descriptor < 0 then
            invalidOp $"source-open-no-follow:{name}:{Marshal.GetLastPInvokeError()}"
        new SafeFileHandle(nativeint descriptor, true)

    /// Open every absolute path component from / without following any link.
    let internal openDirectoryPath (path: string) =
        if not (OperatingSystem.IsLinux()) then invalidOp "linux-pinned-capture-unavailable"
        let full = Path.GetFullPath path
        if not (Path.IsPathFullyQualified full) then invalidOp "source-root-not-absolute"
        let root = openat(-100, "/", O_DIRECTORY ||| O_CLOEXEC ||| O_NOFOLLOW)
        if root < 0 then invalidOp "source-root-open-failed"
        let mutable current = new SafeFileHandle(nativeint root, true)
        try
            for segment in full.Split('/', StringSplitOptions.RemoveEmptyEntries) do
                let next = openAt (number current) segment O_DIRECTORY
                current.Dispose()
                current <- next
            current
        with _ ->
            current.Dispose()
            reraise()

    let internal openDirectoryChild (directory: SafeFileHandle) name =
        openAt (number directory) name O_DIRECTORY

    let private descriptorType (handle: SafeFileHandle) =
        let buffer = Marshal.AllocHGlobal 256
        try
            if statx(number handle, "", AT_EMPTY_PATH, 1u, buffer) <> 0 then
                invalidOp "source-descriptor-statx-unavailable"
            (int (uint16 (Marshal.ReadInt16(buffer, 28)))) &&& 0xf000
        finally
            Marshal.FreeHGlobal buffer

    let internal openRegularChild (directory: SafeFileHandle) name =
        let handle = openAt (number directory) name O_NONBLOCK
        try
            if descriptorType handle <> 0x8000 then
                invalidOp $"nonregular-source:{name}"
            handle
        with _ ->
            handle.Dispose()
            reraise()

    let internal readPinnedFile (handle: SafeFileHandle) =
        if descriptorType handle <> 0x8000 then invalidOp "nonregular-source:descriptor"
        use stream = new FileStream(handle, FileAccess.Read)
        use captured = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable count = stream.Read(buffer, 0, buffer.Length)
        while count > 0 do
            if captured.Length + int64 count > int64 maxFileBytes then
                invalidOp "source-file-too-large"
            captured.Write(buffer, 0, count)
            count <- stream.Read(buffer, 0, buffer.Length)
        captured.ToArray()

    let private names (directory: SafeFileHandle) =
        let path = $"/proc/self/fd/{number directory}"
        let entries =
            Directory.EnumerateFileSystemEntries(path)
            |> Seq.truncate (maxEntries + 1)
            |> Seq.map Path.GetFileName
            |> Seq.toArray
        if entries.Length > maxEntries then invalidOp "source-entry-limit"
        entries |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) |> Array.toList

    /// Capture source bytes from pinned descriptors. Enumeration and file content
    /// are not one atomic filesystem snapshot; later output custody remains separate.
    let prepareFromDiskLinux repoRoot (manifestBytes: byte array) : Result<Staging.Plan, string list> =
        if not (OperatingSystem.IsLinux()) then Error [ "linux-pinned-capture-unavailable" ]
        else
            try
                use repository = openDirectoryPath repoRoot
                use template = openDirectoryChild repository "template"
                use products = openDirectoryChild template "product-skills"
                let mutable entryCount = 0
                let mutable totalBytes = 0L
                let rec capture (directory: SafeFileHandle) prefix : SourceEntry list =
                    [ for name in names directory do
                        entryCount <- entryCount + 1
                        if entryCount > maxEntries then invalidOp "source-entry-limit"
                        let relative = if prefix = "" then name else prefix + "/" + name
                        use entry = openAt (number directory) name O_NONBLOCK
                        match descriptorType entry with
                        | 0x4000 ->
                            let children = capture entry relative
                            if List.isEmpty children then
                                yield { Path = relative; Bytes = Array.empty; IsRegular = false; IsSymlink = false }
                            else yield! children
                        | 0x8000 ->
                            let bytes = readPinnedFile entry
                            totalBytes <- totalBytes + int64 bytes.Length
                            if totalBytes > int64 maxTotalBytes then invalidOp "source-total-bytes-limit"
                            yield { Path = relative; Bytes = bytes; IsRegular = true; IsSymlink = false }
                        | _ -> invalidOp $"nonregular-source:{relative}" ]
                let sources : SourceDirectory list =
                    [ for name in names products do
                        entryCount <- entryCount + 1
                        if entryCount > maxEntries then invalidOp "source-entry-limit"
                        use source = openDirectoryChild products name
                        yield { Path = $"template/product-skills/{name}/"
                                IsDirectory = true
                                HasSymlinkComponent = false
                                Entries = capture source "" } ]
                Staging.prepare manifestBytes sources
            with
            | :? IOException as error -> Error [ $"source-io:{error.GetType().Name}" ]
            | :? UnauthorizedAccessException -> Error [ "source-access-denied" ]
            | :? InvalidOperationException as error -> Error [ error.Message ]
            | :? ArgumentException -> Error [ "source-path-invalid" ]
            | :? DllNotFoundException
            | :? EntryPointNotFoundException -> Error [ "linux-pinned-capture-unavailable" ]
