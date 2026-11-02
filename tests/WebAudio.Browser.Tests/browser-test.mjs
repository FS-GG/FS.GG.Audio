import { createServer } from "node:http";
import { existsSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { extname, join, normalize } from "node:path";
import { chromium, firefox, webkit } from "playwright-core";

const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i >= 0 ? process.argv[i + 1] : fallback; };
const family = arg("--browser", "chromium");
const output = arg("--out", `web-audio.${family}.json`);
const root = new URL("./dist/", import.meta.url).pathname;
const mime = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css" };
const server = createServer((request, response) => {
  const relative = request.url === "/" ? "index.html" : request.url.slice(1);
  const path = normalize(join(root, relative));
  if (!path.startsWith(root) || !existsSync(path) || !statSync(path).isFile()) { response.writeHead(404).end(); return; }
  response.setHeader("content-type", mime[extname(path)] || "application/octet-stream"); response.end(readFileSync(path));
});
await new Promise((done) => server.listen(0, "127.0.0.1", done));
const browserType = { chromium, firefox, webkit }[family];
const launchOptions = family === "firefox"
  ? { headless: true, firefoxUserPrefs: { "media.autoplay.default": 0, "media.autoplay.blocking_policy": 0, "media.autoplay.block-webaudio": false } }
  : { headless: true };
const browser = await browserType.launch(launchOptions);
const page = await browser.newPage();
const errors = [];
page.on("pageerror", error => errors.push(error.stack || error.message));
await page.addInitScript(() => {
  window.lifecycleControlErrors = [];
  addEventListener("error", event => window.lifecycleControlErrors.push(event.message));
  addEventListener("unhandledrejection", event => window.lifecycleControlErrors.push(String(event.reason)));
});

const prepareUnlockedHost = async target => {
  await target.evaluate(() => window.webAudio.mount());
  await target.click("#unlock");
  await target.waitForFunction(() => window.webAudio.observe().status === "Running");
};

// Keep a real AudioContext, but hold its operation's returned promise until after
// disposal. This controls the browser completion clock instead of relying on a race.
const disposalControls = async wave => {
  const results = [];
  for (const { operation, delayedState = false } of [
    { operation: "resume" }, { operation: "suspend" }, { operation: "unlock" },
    { operation: "resume", delayedState: true }
  ]) {
    if (operation === "unlock") await page.evaluate(() => window.webAudio.mount());
    else await prepareUnlockedHost(page);
    if (operation === "resume") {
      await page.evaluate(() => window.webAudio.pause());
      await page.waitForFunction(() => window.webAudio.observe().contextState === "suspended");
    }
    await page.evaluate(operation => {
      const prototype = (globalThis.AudioContext || globalThis.webkitAudioContext).prototype;
      const method = operation === "unlock" ? "resume" : operation;
      const original = prototype[method];
      const control = { reject: null, context: null, errorCount: window.lifecycleControlErrors.length, restore: () => { prototype[method] = original; } };
      prototype[method] = function (...args) {
        control.context = this;
        Promise.resolve(original.apply(this, args)).catch(() => {});
        return new Promise((_, reject) => { control.reject = reject; });
      };
      window.disposalControl = control;
    }, operation);
    if (operation === "unlock") await page.click("#unlock");
    else await page.evaluate(operation => window.webAudio[operation === "suspend" ? "pause" : "resume"](), operation);
    const result = await page.evaluate(async ({ operation, delayedState }) => {
      const disposed = window.webAudio.dispose();
      const control = window.disposalControl;
      control.restore();
      if (!control.reject) throw new Error(`${operation} promise control was not reached`);
      const descriptor = Object.getOwnPropertyDescriptor(control.context, "state");
      // Firefox may deliver the rejection before the observable state changes.
      if (delayedState) Object.defineProperty(control.context, "state", { configurable: true, get: () => "suspended" });
      try {
        control.reject(new DOMException(`controlled ${operation} closed before completion`, "InvalidStateError"));
        await new Promise(resolve => setTimeout(resolve, 0));
        const unexpected = window.lifecycleControlErrors.slice(control.errorCount);
        if (unexpected.length) throw new Error(`${operation}${delayedState ? "-closing-state" : ""}/dispose rejected without an owner: ${unexpected.join("\n")}`);
      } finally {
        if (delayedState) {
          if (descriptor) Object.defineProperty(control.context, "state", descriptor);
          else delete control.context.state;
        }
      }
      const after = window.webAudio.observe();
      if (!after.disposed || after.events.filter(value => value === "disposed").length !== 1 || after.events.includes("refused:disposed")) {
        throw new Error(`late ${operation} completion changed disposed ownership: ${JSON.stringify(after)}`);
      }
      return { operation: delayedState ? "resume-closing-state" : operation, disposed: disposed.disposed, nativeState: control.context.state };
    }, { operation, delayedState });
    results.push(result);
  }
  await prepareUnlockedHost(page);
  await page.evaluate(url => window.webAudio.loadSound("voice-control", url), wave);
  await page.waitForFunction(() => window.webAudio.observe().ready === 1);
  const ended = await page.evaluate(() => {
    const prototype = (globalThis.AudioContext || globalThis.webkitAudioContext).prototype;
    const original = prototype.createBufferSource;
    let source;
    prototype.createBufferSource = function (...args) { source = original.apply(this, args); return source; };
    try {
      window.webAudio.play("voice-control");
      const completion = source?.onended;
      if (typeof completion !== "function") throw new Error("voice completion control was not reached");
      window.webAudio.dispose();
      completion();
      const after = window.webAudio.observe();
      if (!after.disposed || after.events.includes("refused:disposed")) throw new Error(`late voice completion changed disposed ownership: ${JSON.stringify(after)}`);
      return { operation: "voice-ended", disposed: after.disposed, nativeState: source.context.state };
    } finally { prototype.createBufferSource = original; }
  });
  results.push(ended);
  return results;
};

// Unexpected active-operation and close failures must remain observable. These
// dedicated pages expect one named fault; the main journey's pageerror gate stays intact.
const unexpectedFailureControls = async () => {
  const results = [];
  for (const { operation, errorKind } of [
    { operation: "resume", errorKind: "InvalidStateError" },
    { operation: "close", errorKind: "Error" },
    { operation: "close", errorKind: "InvalidStateError" }
  ]) {
    const target = await browser.newPage();
    const observed = [];
    target.on("pageerror", error => observed.push(error.message));
    try {
      await target.goto(`http://127.0.0.1:${server.address().port}/`);
      await prepareUnlockedHost(target);
      if (operation === "resume") {
        await target.evaluate(() => window.webAudio.pause());
        await target.waitForFunction(() => window.webAudio.observe().contextState === "suspended");
      }
      const fault = `unexpected-${operation}-${errorKind}-control`;
      const reported = target.waitForEvent("pageerror");
      await target.evaluate(({ operation, fault, errorKind }) => {
        const prototype = (globalThis.AudioContext || globalThis.webkitAudioContext).prototype;
        const original = prototype[operation];
        prototype[operation] = function (...args) {
          Promise.resolve(original.apply(this, args)).catch(() => {});
          return Promise.reject(errorKind === "InvalidStateError" ? new DOMException(fault, errorKind) : new Error(fault));
        };
        try { window.webAudio[operation === "close" ? "dispose" : "resume"](); }
        finally { prototype[operation] = original; }
      }, { operation, fault, errorKind });
      const failure = await reported;
      if (!failure.message.includes(fault) || observed.length !== 1) throw new Error(`unexpected ${operation} failure was masked or duplicated: ${JSON.stringify(observed)}`);
      await target.evaluate(() => window.webAudio.dispose());
      results.push({ operation, errorKind, faultObserved: true });
    } finally { await target.close(); }
  }
  return results;
};

const makeWaveUrl = async () => page.evaluate(() => {
  const rate=8000, seconds=1, samples=rate*seconds, bytes=new ArrayBuffer(44+samples*2), view=new DataView(bytes);
  const text=(offset,value)=>{ for(let i=0;i<value.length;i++) view.setUint8(offset+i,value.charCodeAt(i)); };
  text(0,"RIFF"); view.setUint32(4,36+samples*2,true); text(8,"WAVEfmt "); view.setUint32(16,16,true); view.setUint16(20,1,true); view.setUint16(22,1,true); view.setUint32(24,rate,true); view.setUint32(28,rate*2,true); view.setUint16(32,2,true); view.setUint16(34,16,true); text(36,"data"); view.setUint32(40,samples*2,true);
  for(let i=0;i<samples;i++) view.setInt16(44+i*2,Math.sin(2*Math.PI*220*i/rate)*1200,true);
  return URL.createObjectURL(new Blob([bytes],{type:"audio/wav"}));
});

let evidence;
try {
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.waitForFunction(() => window.webAudio);
  const mounted = await page.evaluate(() => window.webAudio.mount());
  const locked = await page.evaluate(() => window.webAudio.play("tone"));
  if (mounted.status !== "Locked" || mounted.context || !locked.events.includes("refused:locked")) throw new Error(`locked contract failed: ${JSON.stringify({mounted,locked})}`);
  await page.click("#unlock");
  await page.waitForFunction(() => window.webAudio.observe().status === "Running");
  const unlocked = await page.evaluate(() => window.webAudio.observe());
  if (!unlocked.context || !["running","suspended"].includes(unlocked.contextState)) throw new Error(`gesture unlock failed: ${JSON.stringify(unlocked)}`);
  const wave = await makeWaveUrl();
  await page.evaluate(url => { window.webAudio.loadSound("tone",url); window.webAudio.loadTrack("theme",url); }, wave);
  await page.waitForFunction(() => window.webAudio.observe().ready === 2);
  const loaded = await page.evaluate(() => window.webAudio.observe());
  const positional = await page.evaluate(() => window.webAudio.playAt("tone"));
  if (positional.graphVoices !== 1 || Math.abs(positional.lastPan-Math.SQRT1_2) > 0.01) throw new Error(`positional dispatch failed: ${JSON.stringify(positional)}`);
  const bounded = await page.evaluate(() => { window.webAudio.play("tone"); window.webAudio.play("tone"); return window.webAudio.observe(); });
  if (bounded.voices !== 2 || bounded.graphVoices !== 2) throw new Error(`voice limit failed: ${JSON.stringify(bounded)}`);
  const mixed = await page.evaluate(() => { window.webAudio.master(0.25); window.webAudio.bus(0.4); window.webAudio.duck(); return window.webAudio.observe(); });
  if (Math.abs(mixed.masterGain-0.25) > 0.001) throw new Error(`bus/mute graph failed: ${JSON.stringify(mixed)}`);
  const music = await page.evaluate(() => window.webAudio.music("theme"));
  if (!music.music || !music.graphMusic) throw new Error(`loop lifecycle failed: ${JSON.stringify(music)}`);
  const paused = await page.evaluate(() => window.webAudio.pause());
  await page.waitForFunction(() => window.webAudio.observe().contextState === "suspended");
  const resumed = await page.evaluate(() => window.webAudio.resume());
  await page.waitForFunction(() => window.webAudio.observe().status === "Running");
  await page.evaluate(() => window.webAudio.loadSound("missing","/missing.wav"));
  await page.waitForFunction(() => window.webAudio.observe().events.some(value => value.startsWith("asset-failed:sound:missing")));
  const failed = await page.evaluate(() => window.webAudio.observe());
  const disposed = await page.evaluate(() => window.webAudio.dispose());
  if (!disposed.disposed || disposed.context || disposed.graphVoices !== 0 || disposed.graphMusic) throw new Error(`disposal failed: ${JSON.stringify(disposed)}`);
  const disposalRaces = await disposalControls(wave);
  const unexpectedFailures = await unexpectedFailureControls();
  evidence={result:"pass",family,mounted,locked,unlocked,loaded,positional,bounded,mixed,music,paused,resumed,failed,disposed,disposalRaces,unexpectedFailures,listening:{status:"disclosed-manual-fixture",automatedAudibilityClaim:false},runnerAudioState:unlocked.contextState};
} finally { await browser.close(); await new Promise(done => server.close(done)); }
if(errors.length) throw new Error(errors.join("\n"));
writeFileSync(output,JSON.stringify(evidence,null,2)+"\n");
console.log(`web-audio-browser: ${family}=passed output=${output}`);
console.log(`web-audio-lifecycle: ${family} disposal-controls=${evidence.disposalRaces.length} unexpected-failure-controls=${evidence.unexpectedFailures.length}`);
