// Serves tools/fixtures/chat-line-biz.html for EVERY path (so /<botId>/chat/<userId> works) and /line.js, on localhost only, to
// try line.js in a real browser tab before it meets a real OA:
//   node tools/fixtures/serve-fixture.mjs 5188
//   open http://localhost:5188/Ubbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/chat/U00000000000000000000000000000001
// line.js talks to the extension through chrome.runtime / chrome.storage, which a plain page does not have. In the page's
// DevTools console define a stub, then run the script and call its commands the way the service worker would:
//   window.chrome = { runtime: { onMessage: { addListener: (fn) => (window.__line = fn) } }, storage: { local: { get: async () => ({}) } } };
//   eval(await (await fetch('/line.js')).text());
//   const call = (m) => new Promise((r) => window.__line({ target: 'fbap-content', ...m }, {}, r));
//   await call({ cmd: 'lineChatState' });
import fs from 'node:fs';
import http from 'node:http';

const port = Number(process.argv[2]) || 5188;
const here = (p) => new URL(p, import.meta.url);

http
  .createServer((req, res) => {
    const path = req.url.split('?')[0];
    if (path === '/line.js') {
      res.writeHead(200, { 'Content-Type': 'text/javascript; charset=utf-8', 'Cache-Control': 'no-store' });
      res.end(fs.readFileSync(here('../../line.js')));
      return;
    }
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    res.end(fs.readFileSync(here('./chat-line-biz.html')));
  })
  .listen(port, '127.0.0.1', () => console.log(`fixture on http://localhost:${port}/`));
