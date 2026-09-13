namespace FS.GG.Audio.WebBrowser

open System
open Fable.Core
open FS.GG.Audio.Core

[<RequireQualifiedAccess>]
type WebAudioStatus = Locked | Running | Paused | Disposed

[<RequireQualifiedAccess>]
type WebAudioAssetStatus = Pending | Ready | Failed of string

type WebAudioConfig = { MaxOneShotVoices: int }

[<RequireQualifiedAccess>]
type WebAudioRefusal =
    | Locked
    | Paused
    | AssetUnavailable of string
    | UnknownAssetCompletion of string
    | UnlockFailed of string
    | Disposed

[<RequireQualifiedAccess>]
type WebAudioObservation =
    | RequestUnlock
    | UnlockSucceeded
    | UnlockFailed of string
    | RequestAsset of assetKey: string
    | AssetReady of assetKey: string
    | AssetFailed of assetKey: string * diagnostic: string
    | Dispatch of AudioEffect
    | Pause
    | Resume
    | VoiceEnded of voiceId: uint64
    | Dispose

[<RequireQualifiedAccess>]
type WebAudioPolicyEffect =
    | CreateOrResumeContext
    | DecodeAsset of assetKey: string
    | PlayOneShot of voiceId: uint64 * assetKey: string * bus: Bus * gain: float * pan: float
    | StopVoice of voiceId: uint64
    | StartMusic of assetKey: string * loop: bool
    | StopMusic
    | SetBusGain of bus: Bus * gain: float
    | DuckBus of bus: Bus * amount: float * milliseconds: float
    | SuspendContext
    | ResumeContext
    | StopAll
    | CloseContext
    | Refused of WebAudioRefusal

type WebAudioPolicyState = private {
    Status: WebAudioStatus
    Assets: Map<string, WebAudioAssetStatus>
    Voices: uint64 list
    Music: string option
    NextVoiceId: uint64 }

[<RequireQualifiedAccess>]
module WebAudioPolicy =
    let private validate config =
        if config.MaxOneShotVoices < 1 || config.MaxOneShotVoices > 1024 then
            invalidArg (nameof config) "one-shot voice limit must be between 1 and 1,024"

    let soundKey (SoundId id) = "sound:" + id
    let trackKey (TrackId id) = "track:" + id

    let initialize config =
        validate config
        { Status = WebAudioStatus.Locked
          Assets = Map.empty
          Voices = []
          Music = None
          NextVoiceId = 0UL }

    let observe state =
        state.Status,
        state.Assets.Count,
        (state.Assets |> Map.toSeq |> Seq.sumBy (fun (_, status) -> match status with WebAudioAssetStatus.Ready -> 1 | _ -> 0)),
        state.Voices.Length,
        state.Music.IsSome,
        state.NextVoiceId

    let private ready key state = state.Assets |> Map.tryFind key = Some WebAudioAssetStatus.Ready

    let private playOne config key bus gain pan state =
        if not (ready key state) then state, [ WebAudioPolicyEffect.Refused(WebAudioRefusal.AssetUnavailable key) ]
        elif state.NextVoiceId = UInt64.MaxValue then invalidOp "Web Audio voice identity space is exhausted."
        else
            let stolen, retained =
                if state.Voices.Length >= config.MaxOneShotVoices then Some state.Voices.Head, state.Voices.Tail
                else None, state.Voices
            let voiceId = state.NextVoiceId
            let accepted = { state with Voices = retained @ [ voiceId ]; NextVoiceId = voiceId + 1UL }
            let effects =
                [ match stolen with Some id -> WebAudioPolicyEffect.StopVoice id | None -> ()
                  WebAudioPolicyEffect.PlayOneShot(voiceId, key, bus, Audio.clampVolume gain, max -1.0 (min 1.0 pan)) ]
            accepted, effects

    let update config observation state =
        validate config
        if state.Status = WebAudioStatus.Disposed then
            match observation with
            | WebAudioObservation.Dispose -> state, []
            | _ -> state, [ WebAudioPolicyEffect.Refused WebAudioRefusal.Disposed ]
        else
            match observation with
            | WebAudioObservation.RequestUnlock when state.Status = WebAudioStatus.Locked ->
                state, [ WebAudioPolicyEffect.CreateOrResumeContext ]
            | WebAudioObservation.RequestUnlock -> state, []
            | WebAudioObservation.UnlockSucceeded when state.Status = WebAudioStatus.Locked ->
                { state with Status = WebAudioStatus.Running }, []
            | WebAudioObservation.UnlockSucceeded -> state, []
            | WebAudioObservation.UnlockFailed diagnostic ->
                state, [ WebAudioPolicyEffect.Refused(WebAudioRefusal.UnlockFailed diagnostic) ]
            | WebAudioObservation.RequestAsset _ when state.Status = WebAudioStatus.Locked ->
                state, [ WebAudioPolicyEffect.Refused WebAudioRefusal.Locked ]
            | WebAudioObservation.RequestAsset key ->
                let accepted = { state with Assets = state.Assets.Add(key, WebAudioAssetStatus.Pending) }
                accepted, [ WebAudioPolicyEffect.DecodeAsset key ]
            | WebAudioObservation.AssetReady key when state.Assets |> Map.tryFind key = Some WebAudioAssetStatus.Pending ->
                { state with Assets = state.Assets.Add(key, WebAudioAssetStatus.Ready) }, []
            | WebAudioObservation.AssetFailed(key, diagnostic) when state.Assets.ContainsKey key ->
                { state with Assets = state.Assets.Add(key, WebAudioAssetStatus.Failed diagnostic) }, []
            | WebAudioObservation.AssetReady key
            | WebAudioObservation.AssetFailed(key, _) ->
                state, [ WebAudioPolicyEffect.Refused(WebAudioRefusal.UnknownAssetCompletion key) ]
            | WebAudioObservation.Dispatch _ when state.Status = WebAudioStatus.Locked ->
                state, [ WebAudioPolicyEffect.Refused WebAudioRefusal.Locked ]
            | WebAudioObservation.Dispatch _ when state.Status = WebAudioStatus.Paused ->
                state, [ WebAudioPolicyEffect.Refused WebAudioRefusal.Paused ]
            | WebAudioObservation.Dispatch effect ->
                match effect with
                | PlaySfx(sound, gain) -> playOne config (soundKey sound) Sfx gain 0.0 state
                | PlaySfx3D(sound, x, _, z, gain) ->
                    let distance = sqrt (x * x + z * z)
                    let pan = if Double.IsNaN distance || Double.IsInfinity distance || distance <= 0.0 then 0.0 else x / distance
                    playOne config (soundKey sound) Sfx gain pan state
                | PlayMusic(track, loop) ->
                    let key = trackKey track
                    if not (ready key state) then state, [ WebAudioPolicyEffect.Refused(WebAudioRefusal.AssetUnavailable key) ]
                    else
                        { state with Music = Some key },
                        [ if state.Music.IsSome then WebAudioPolicyEffect.StopMusic
                          WebAudioPolicyEffect.StartMusic(key, loop) ]
                | StopMusic -> { state with Music = None }, [ WebAudioPolicyEffect.StopMusic ]
                | SetMasterVolume gain -> state, [ WebAudioPolicyEffect.SetBusGain(Master, Audio.clampVolume gain) ]
                | SetBusVolume(bus, gain) -> state, [ WebAudioPolicyEffect.SetBusGain(bus, Audio.clampVolume gain) ]
                | Duck(bus, amount, milliseconds) ->
                    let duration = if Double.IsNaN milliseconds || Double.IsInfinity milliseconds || milliseconds <= 0.0 then 0.0 else milliseconds
                    state, [ WebAudioPolicyEffect.DuckBus(bus, Audio.clampVolume amount, duration) ]
            | WebAudioObservation.Pause when state.Status = WebAudioStatus.Running ->
                { state with Status = WebAudioStatus.Paused }, [ WebAudioPolicyEffect.SuspendContext ]
            | WebAudioObservation.Pause -> state, []
            | WebAudioObservation.Resume when state.Status = WebAudioStatus.Paused ->
                { state with Status = WebAudioStatus.Running }, [ WebAudioPolicyEffect.ResumeContext ]
            | WebAudioObservation.Resume -> state, []
            | WebAudioObservation.VoiceEnded id ->
                { state with Voices = state.Voices |> List.filter ((<>) id) }, []
            | WebAudioObservation.Dispose ->
                { state with Status = WebAudioStatus.Disposed; Voices = []; Music = None },
                [ WebAudioPolicyEffect.StopAll; WebAudioPolicyEffect.CloseContext ]

[<RequireQualifiedAccess>]
type WebAudioHostEvent =
    | AssetReady of string
    | AssetFailed of string * string
    | Refused of WebAudioRefusal
    | EffectDispatched of AudioEffect
    | Disposed

type WebAudioHostObservation =
    { Status: WebAudioStatus
      AssetCount: int
      ReadyAssetCount: int
      ActiveVoiceCount: int
      HasMusic: bool
      ContextCreated: bool
      ContextState: string
      GraphVoiceCount: int
      GraphHasMusic: bool
      MasterGain: float
      LastPan: float
      IsDisposed: bool }

module private WebAudioDom =
    [<Emit("({ctx:null,buffers:new Map(),voices:new Map(),music:null,buses:null,master:null,lastPan:0})")>]
    let createGraph () : obj = jsNative

    [<Emit("(function(g,ok,fail){try{if(!g.ctx){const C=globalThis.AudioContext||globalThis.webkitAudioContext;if(!C)throw new Error('Web Audio unavailable');g.ctx=new C();g.master=g.ctx.createGain();g.master.connect(g.ctx.destination);g.buses=new Map();for(const n of ['Music','Sfx','Ui','Ambient']){const x=g.ctx.createGain();x.connect(g.master);g.buses.set(n,x);}g.buses.set('Master',g.master);}const pending=g.ctx.resume();ok();Promise.resolve(pending).catch(e=>fail(String(e?.message||e)));}catch(e){fail(String(e?.message||e));}})($0,$1,$2)")>]
    let unlock (_graph: obj) (_ok: unit -> unit) (_fail: string -> unit) : unit = jsNative

    [<Emit("(function(g,key,url,ok,fail){fetch(url).then(r=>{if(!r.ok)throw new Error('HTTP '+r.status);return r.arrayBuffer();}).then(b=>g.ctx.decodeAudioData(b)).then(b=>{g.buffers.set(key,b);ok();}).catch(e=>fail(String(e?.message||e)));})($0,$1,$2,$3,$4)")>]
    let load (_graph: obj) (_key: string) (_url: string) (_ok: unit -> unit) (_fail: string -> unit) : unit = jsNative

    [<Emit("(function(g,id,key,bus,gain,pan,ended){const src=g.ctx.createBufferSource(),vol=g.ctx.createGain(),p=g.ctx.createStereoPanner();src.buffer=g.buffers.get(key);vol.gain.value=gain;p.pan.value=pan;g.lastPan=pan;src.connect(vol);vol.connect(p);p.connect(g.buses.get(bus));src.onended=()=>{try{src.disconnect();vol.disconnect();p.disconnect();}finally{g.voices.delete(String(id));ended();}};g.voices.set(String(id),src);src.start();})($0,$1,$2,$3,$4,$5,$6)")>]
    let playOneShot (_graph: obj) (_id: uint64) (_key: string) (_bus: string) (_gain: float) (_pan: float) (_ended: unit -> unit) : unit = jsNative

    [<Emit("(function(g,id){const x=g.voices.get(String(id));if(x){x.stop();g.voices.delete(String(id));}})($0,$1)")>]
    let stopVoice (_graph: obj) (_id: uint64) : unit = jsNative

    [<Emit("(function(g,key,loop){if(g.music){try{g.music.stop();}catch{}g.music=null;}const src=g.ctx.createBufferSource();src.buffer=g.buffers.get(key);src.loop=loop;src.connect(g.buses.get('Music'));src.onended=()=>{if(g.music===src)g.music=null;};g.music=src;src.start();})($0,$1,$2)")>]
    let startMusic (_graph: obj) (_key: string) (_loop: bool) : unit = jsNative

    [<Emit("(function(g){if(g.music){try{g.music.stop();}catch{}g.music=null;}})($0)")>]
    let stopMusic (_graph: obj) : unit = jsNative

    [<Emit("(function(g,bus,gain){const x=g.buses.get(bus);if(x)x.gain.value=gain;})($0,$1,$2)")>]
    let setBusGain (_graph: obj) (_bus: string) (_gain: float) : unit = jsNative

    [<Emit("(function(g,bus,amount,ms){const x=g.buses.get(bus);if(!x)return;const now=g.ctx.currentTime,v=x.gain.value,half=Math.max(0,ms)/2000;x.gain.cancelScheduledValues(now);x.gain.setValueAtTime(v,now);x.gain.linearRampToValueAtTime(v*(1-amount),now+half);x.gain.linearRampToValueAtTime(v,now+half*2);})($0,$1,$2,$3)")>]
    let duck (_graph: obj) (_bus: string) (_amount: float) (_milliseconds: float) : unit = jsNative

    [<Emit("$0.ctx?.suspend()")>]
    let suspend (_graph: obj) : unit = jsNative
    [<Emit("$0.ctx?.resume()")>]
    let resume (_graph: obj) : unit = jsNative
    [<Emit("(function(g){for(const x of g.voices.values())try{x.stop();}catch{}g.voices.clear();if(g.music)try{g.music.stop();}catch{}g.music=null;})($0)")>]
    let stopAll (_graph: obj) : unit = jsNative
    [<Emit("(function(g){const c=g.ctx;g.ctx=null;g.buses=null;g.master=null;return c?.close();})($0)")>]
    let close (_graph: obj) : unit = jsNative
    [<Emit("$0.ctx != null")>]
    let contextCreated (_graph: obj) : bool = jsNative
    [<Emit("$0.ctx?.state || 'none'")>]
    let contextState (_graph: obj) : string = jsNative
    [<Emit("$0.voices.size")>]
    let voiceCount (_graph: obj) : int = jsNative
    [<Emit("$0.music != null")>]
    let hasMusic (_graph: obj) : bool = jsNative
    [<Emit("$0.master?.gain.value ?? 1")>]
    let masterGain (_graph: obj) : float = jsNative
    [<Emit("$0.lastPan")>]
    let lastPan (_graph: obj) : float = jsNative

[<Sealed>]
type WebAudioHost(config: WebAudioConfig, onEvent: WebAudioHostEvent -> unit) =
    let graph = WebAudioDom.createGraph ()
    let urls = System.Collections.Generic.Dictionary<string, string>()
    let mutable state = WebAudioPolicy.initialize config

    let busKey = function
        | Bus.Master -> "Master"
        | Bus.Music -> "Music"
        | Bus.Sfx -> "Sfx"
        | Bus.Ui -> "Ui"
        | Bus.Ambient -> "Ambient"

    let rec apply observation =
        let next, effects = WebAudioPolicy.update config observation state
        state <- next
        let accepted = effects |> List.exists (function WebAudioPolicyEffect.Refused _ -> true | _ -> false) |> not
        for effect in effects do
            match effect with
            | WebAudioPolicyEffect.CreateOrResumeContext ->
                WebAudioDom.unlock graph (fun () -> apply WebAudioObservation.UnlockSucceeded |> ignore) (fun error -> apply (WebAudioObservation.UnlockFailed error) |> ignore)
            | WebAudioPolicyEffect.DecodeAsset key ->
                match urls.TryGetValue key with
                | true, url ->
                    WebAudioDom.load graph key url
                        (fun () -> apply (WebAudioObservation.AssetReady key) |> ignore; onEvent (WebAudioHostEvent.AssetReady key))
                        (fun error -> apply (WebAudioObservation.AssetFailed(key, error)) |> ignore; onEvent (WebAudioHostEvent.AssetFailed(key, error)))
                | _ -> apply (WebAudioObservation.AssetFailed(key, "asset URL unavailable")) |> ignore
            | WebAudioPolicyEffect.PlayOneShot(id, key, bus, gain, pan) ->
                WebAudioDom.playOneShot graph id key (busKey bus) gain pan (fun () -> apply (WebAudioObservation.VoiceEnded id) |> ignore)
            | WebAudioPolicyEffect.StopVoice id -> WebAudioDom.stopVoice graph id
            | WebAudioPolicyEffect.StartMusic(key, loop) -> WebAudioDom.startMusic graph key loop
            | WebAudioPolicyEffect.StopMusic -> WebAudioDom.stopMusic graph
            | WebAudioPolicyEffect.SetBusGain(bus, gain) -> WebAudioDom.setBusGain graph (busKey bus) gain
            | WebAudioPolicyEffect.DuckBus(bus, amount, milliseconds) -> WebAudioDom.duck graph (busKey bus) amount milliseconds
            | WebAudioPolicyEffect.SuspendContext -> WebAudioDom.suspend graph
            | WebAudioPolicyEffect.ResumeContext -> WebAudioDom.resume graph
            | WebAudioPolicyEffect.StopAll -> WebAudioDom.stopAll graph
            | WebAudioPolicyEffect.CloseContext -> WebAudioDom.close graph
            | WebAudioPolicyEffect.Refused reason -> onEvent (WebAudioHostEvent.Refused reason)
        accepted

    member _.UnlockFromGesture() = apply WebAudioObservation.RequestUnlock |> ignore
    member _.LoadSound(sound, url) =
        let key = WebAudioPolicy.soundKey sound
        urls[key] <- url
        apply (WebAudioObservation.RequestAsset key) |> ignore
    member _.LoadTrack(track, url) =
        let key = WebAudioPolicy.trackKey track
        urls[key] <- url
        apply (WebAudioObservation.RequestAsset key) |> ignore
    member _.Dispatch effect =
        if apply (WebAudioObservation.Dispatch effect) then onEvent (WebAudioHostEvent.EffectDispatched effect)
    member _.Pause() = apply WebAudioObservation.Pause |> ignore
    member _.Resume() = apply WebAudioObservation.Resume |> ignore
    member _.Observe() =
        let status, assets, ready, voices, music, _ = WebAudioPolicy.observe state
        { Status = status
          AssetCount = assets
          ReadyAssetCount = ready
          ActiveVoiceCount = voices
          HasMusic = music
          ContextCreated = WebAudioDom.contextCreated graph
          ContextState = WebAudioDom.contextState graph
          GraphVoiceCount = WebAudioDom.voiceCount graph
          GraphHasMusic = WebAudioDom.hasMusic graph
          MasterGain = WebAudioDom.masterGain graph
          LastPan = WebAudioDom.lastPan graph
          IsDisposed = status = WebAudioStatus.Disposed }
    interface IDisposable with
        member _.Dispose() =
            let status, _, _, _, _, _ = WebAudioPolicy.observe state
            if status <> WebAudioStatus.Disposed then
                apply WebAudioObservation.Dispose |> ignore
                urls.Clear()
                onEvent WebAudioHostEvent.Disposed

[<RequireQualifiedAccess>]
module WebAudioHost =
    let defaultConfig = { MaxOneShotVoices = 32 }
