open Fable.Core
open Fable.Core.JsInterop

[<Emit("process.stdout.write($0)")>]
let writeStdout (_value: string) : unit = jsNative

writeStdout (WebAudioCorrespondence.run())
