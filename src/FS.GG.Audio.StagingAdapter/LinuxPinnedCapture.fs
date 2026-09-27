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

    [<DllImport("libc", SetLastError = true, EntryPoint = "dup")>]
    extern int private dup(int descriptor)

    [<DllImport("libc", SetLastError = true, EntryPoint = "lseek")>]
    extern int64 private lseek(int descriptor, int64 offset, int origin)

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

    let private descriptorStamp label (handle: SafeFileHandle) =
        let buffer = Marshal.AllocHGlobal 256
        try
            if statx(number handle, "", AT_EMPTY_PATH, 0x7ffu, buffer) <> 0 then
                invalidOp $"{label}-stamp-unavailable"
            let bytes = Array.zeroCreate<byte> 256
            Marshal.Copy(buffer, bytes, 0, bytes.Length)
            // Require nlink, inode, size, mtime, and ctime; compare them
            // from the opened descriptor rather than a later pathname lookup.
            if BitConverter.ToUInt32(bytes, 0) &&& 0x3c4u <> 0x3c4u then
                invalidOp $"{label}-stamp-unavailable"
            [| bytes.[16..19]; bytes.[32..47]; bytes.[96..127]; bytes.[136..143] |]
            |> Array.concat
        finally
            Marshal.FreeHGlobal buffer

    let private directoryStamp handle = descriptorStamp "source-directory" handle
    let private fileStamp handle = descriptorStamp "source-file" handle

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
        if lseek(number handle, 0L, 0) < 0L then invalidOp "source-file-seek-failed"
        let copy = dup(number handle)
        if copy < 0 then invalidOp "source-file-dup-failed"
        use copyHandle = new SafeFileHandle(nativeint copy, true)
        use stream = new FileStream(copyHandle, FileAccess.Read)
        use captured = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable count = stream.Read(buffer, 0, buffer.Length)
        while count > 0 do
            if captured.Length + int64 count > int64 maxFileBytes then
                invalidOp "source-file-too-large"
            captured.Write(buffer, 0, count)
            count <- stream.Read(buffer, 0, buffer.Length)
        captured.ToArray()

    let private readPinnedFileStable afterFirstPass relative (handle: SafeFileHandle) =
        let before = fileStamp handle
        let first = readPinnedFile handle
        afterFirstPass relative
        let middle = fileStamp handle
        if before <> middle then invalidOp $"source-file-unstable:{relative}"
        let repeated = readPinnedFile handle
        let after = fileStamp handle
        if middle <> after || first <> repeated then
            invalidOp $"source-file-unstable:{relative}"
        first

    let private names (directory: SafeFileHandle) =
        let path = $"/proc/self/fd/{number directory}"
        let entries =
            Directory.EnumerateFileSystemEntries(path)
            |> Seq.truncate (maxEntries + 1)
            |> Seq.map Path.GetFileName
            |> Seq.toArray
        if entries.Length > maxEntries then invalidOp "source-entry-limit"
        entries |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) |> Array.toList

    /// Capture source bytes and the physical manifest through pinned descriptors.
    /// Rechecks detect observed drift, not an atomic cross-root snapshot.
    let private prepareCore (afterNames: string -> unit) (afterRead: string -> unit)
                            (afterSources: unit -> unit)
                            repoRoot (manifestBytes: byte array) : Result<Staging.Plan, string list> =
        if not (OperatingSystem.IsLinux()) then Error [ "linux-pinned-capture-unavailable" ]
        elif obj.ReferenceEquals(manifestBytes, null) then Error [ "source-manifest-null" ]
        else
            try
                let selectedManifest = Array.copy manifestBytes
                use repository = openDirectoryPath repoRoot
                let repositoryStamp = directoryStamp repository
                use template = openDirectoryChild repository "template"
                let templateStamp = directoryStamp template
                use manifestDirectory = openDirectoryChild template "skill-manifest"
                let manifestDirectoryStamp = directoryStamp manifestDirectory
                use manifestFile = openRegularChild manifestDirectory "skill-manifest.json"
                let manifestFileStamp = fileStamp manifestFile
                let capturedManifest =
                    readPinnedFileStable ignore "template/skill-manifest/skill-manifest.json" manifestFile
                if capturedManifest <> selectedManifest then invalidOp "source-manifest-mismatch"
                use products = openDirectoryChild template "product-skills"
                let mutable entryCount = 0
                let mutable totalBytes = 0L
                let namesAt prefix directory =
                    let before = directoryStamp directory
                    let found = names directory
                    afterNames prefix
                    let middle = directoryStamp directory
                    if before <> middle then invalidOp $"source-directory-unstable:{prefix}"
                    let repeated = names directory
                    let after = directoryStamp directory
                    if middle <> after || found <> repeated then
                        invalidOp $"source-directory-unstable:{prefix}"
                    found
                let rec capture (directory: SafeFileHandle) prefix : SourceEntry list =
                    let before = directoryStamp directory
                    let found =
                      [ for name in namesAt prefix directory do
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
                            let bytes = readPinnedFileStable afterRead relative entry
                            totalBytes <- totalBytes + int64 bytes.Length
                            if totalBytes > int64 maxTotalBytes then invalidOp "source-total-bytes-limit"
                            yield { Path = relative; Bytes = bytes; IsRegular = true; IsSymlink = false }
                        | _ -> invalidOp $"nonregular-source:{relative}" ]
                    if directoryStamp directory <> before then
                        invalidOp $"source-directory-unstable:{prefix}"
                    found
                let productsBefore = directoryStamp products
                let sources : SourceDirectory list =
                    [ for name in namesAt "template/product-skills" products do
                        entryCount <- entryCount + 1
                        if entryCount > maxEntries then invalidOp "source-entry-limit"
                        use source = openDirectoryChild products name
                        yield { Path = $"template/product-skills/{name}/"
                                IsDirectory = true
                                HasSymlinkComponent = false
                                Entries = capture source "" } ]
                if directoryStamp products <> productsBefore then
                    invalidOp "source-directory-unstable:template/product-skills"
                afterSources()
                let heldManifest =
                    readPinnedFileStable ignore "template/skill-manifest/skill-manifest.json" manifestFile
                if fileStamp manifestFile <> manifestFileStamp || heldManifest <> selectedManifest then
                    invalidOp "source-manifest-unstable"
                // Reopen the visible path from the checkout root. A held fd alone
                // could still point to a renamed, formerly visible manifest.
                use currentRepository = openDirectoryPath repoRoot
                use currentTemplate = openDirectoryChild currentRepository "template"
                use currentManifestDirectory = openDirectoryChild currentTemplate "skill-manifest"
                use currentManifestFile = openRegularChild currentManifestDirectory "skill-manifest.json"
                if directoryStamp currentRepository <> repositoryStamp
                   || directoryStamp currentTemplate <> templateStamp
                   || directoryStamp currentManifestDirectory <> manifestDirectoryStamp
                   || fileStamp currentManifestFile <> manifestFileStamp then
                    invalidOp "source-manifest-unstable"
                let currentManifest =
                    readPinnedFileStable ignore "template/skill-manifest/skill-manifest.json" currentManifestFile
                if currentManifest <> selectedManifest then invalidOp "source-manifest-unstable"
                Staging.prepare selectedManifest sources
            with
            | :? IOException as error -> Error [ $"source-io:{error.GetType().Name}" ]
            | :? UnauthorizedAccessException -> Error [ "source-access-denied" ]
            | :? InvalidOperationException as error -> Error [ error.Message ]
            | :? ArgumentException -> Error [ "source-path-invalid" ]
            | :? DllNotFoundException
            | :? EntryPointNotFoundException -> Error [ "linux-pinned-capture-unavailable" ]

    let prepareFromDiskLinux repoRoot manifestBytes = prepareCore ignore ignore ignore repoRoot manifestBytes

    // Test seam after a held-directory scan, before the discovered children are opened.
    let internal prepareWithNamesHook afterNames repoRoot manifestBytes =
        prepareCore afterNames ignore ignore repoRoot manifestBytes

    // Test seam after the first held-fd byte pass, before the plan is made.
    let internal prepareWithReadHook afterRead repoRoot manifestBytes =
        prepareCore ignore afterRead ignore repoRoot manifestBytes

    // Test seam after all product roots have been captured, before the plan is made.
    let internal prepareWithSourcesHook afterSources repoRoot manifestBytes =
        prepareCore ignore ignore afterSources repoRoot manifestBytes
