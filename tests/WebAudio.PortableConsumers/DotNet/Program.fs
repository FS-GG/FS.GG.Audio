open System.IO

[<EntryPoint>]
let main argv =
    File.WriteAllText(argv[0], WebAudioCorrespondence.run ())
    0
