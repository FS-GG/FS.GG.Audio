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
  evidence={result:"pass",family,mounted,locked,unlocked,loaded,positional,bounded,mixed,music,paused,resumed,failed,disposed,listening:{status:"disclosed-manual-fixture",automatedAudibilityClaim:false},runnerAudioState:unlocked.contextState};
} finally { await browser.close(); await new Promise(done => server.close(done)); }
if(errors.length) throw new Error(errors.join("\n"));
writeFileSync(output,JSON.stringify(evidence,null,2)+"\n");
console.log(`web-audio-browser: ${family}=passed output=${output}`);
