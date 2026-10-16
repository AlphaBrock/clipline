// 把 demo.html 逐帧录成 MP4。
//   npm i puppeteer-core    （任意目录装好后用 NODE_PATH 指过去即可）
//   node docs/demo/record.js [输出.mp4] [fps]
// 需要本机的 Edge 或 Chrome，以及 PATH 里的 ffmpeg。

const http = require("http");
const fs = require("fs");
const path = require("path");
const { spawn } = require("child_process");
const puppeteer = require("puppeteer-core");

const root = path.resolve(__dirname, "../..");
const out = path.resolve(process.argv[2] || path.join(__dirname, "clipline-demo.mp4"));
const fps = +(process.argv[3] || 60);
const browsers = [
  process.env.BROWSER,
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
].filter(Boolean);

const types = { ".html": "text/html; charset=utf-8", ".png": "image/png", ".js": "text/javascript" };
const server = http.createServer((req, res) => {
  const file = path.join(root, decodeURIComponent(new URL(req.url, "http://x").pathname));
  if (!file.startsWith(root) || !fs.existsSync(file)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { "Content-Type": types[path.extname(file)] || "application/octet-stream" });
  fs.createReadStream(file).pipe(res);
});

(async () => {
  await new Promise(r => server.listen(0, "127.0.0.1", r));
  const port = server.address().port;
  // 设了 BROWSER_URL（如 http://127.0.0.1:9333）就连到已用 --remote-debugging-port 启动的浏览器
  const browser = process.env.BROWSER_URL
    ? await puppeteer.connect({ browserURL: process.env.BROWSER_URL })
    : await puppeteer.launch({
      executablePath: browsers.find(b => fs.existsSync(b)),
      headless: true,
      args: ["--force-device-scale-factor=1", "--hide-scrollbars"],
    });
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080 });
  await page.goto(`http://127.0.0.1:${port}/docs/demo/demo.html?capture&fps=${fps}`);
  await page.evaluate(() => window.ready);
  const total = Math.round(await page.evaluate(() => window.duration) * fps);

  const ff = spawn("ffmpeg", [
    "-y", "-loglevel", "error", "-f", "image2pipe", "-framerate", String(fps), "-i", "-",
    "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", out,
  ], { stdio: ["pipe", "inherit", "inherit"] });

  const started = Date.now();
  for (let i = 0; i < total; i++) {
    const url = await page.evaluate(() => { window.frame(); return document.getElementById("c").toDataURL("image/png"); });
    const buf = Buffer.from(url.slice(url.indexOf(",") + 1), "base64");
    if (!ff.stdin.write(buf)) await new Promise(r => ff.stdin.once("drain", r));
    if (i % fps === 0) process.stdout.write(`\r${i}/${total} 帧  ${((Date.now() - started) / 1000).toFixed(0)}s`);
  }
  ff.stdin.end();
  await new Promise(r => ff.on("close", r));
  if (process.env.BROWSER_URL) { await page.close(); browser.disconnect(); } else await browser.close();
  server.close();
  console.log(`\n完成：${out}`);
})().catch(e => { console.error(e); process.exit(1); });
