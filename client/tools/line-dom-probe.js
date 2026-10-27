(() => {
  const cut = (s, n = 40) => String(s || '').replace(/\s+/g, ' ').trim().slice(0, n);
  const redact = (s) => String(s || '').replace(/U[0-9a-f]{32}/gi, '<userId>').replace(/(chat\.line\.biz\/)[^/?#]+/i, '$1<botId>');
  const desc = (el, sens) => ({
    tag: el.tagName.toLowerCase(),
    id: el.id || undefined,
    cls: cut(el.className && el.className.baseVal !== undefined ? el.className.baseVal : el.className, 120) || undefined,
    role: el.getAttribute('role') || undefined,
    type: el.getAttribute('type') || undefined,
    name: el.getAttribute('name') || undefined,
    aria: sens ? (el.hasAttribute('aria-label') ? '<present>' : undefined) : el.getAttribute('aria-label') || undefined,
    placeholder: el.getAttribute('placeholder') || undefined,
    title: sens ? (el.hasAttribute('title') ? '<present>' : undefined) : el.getAttribute('title') || undefined,
    disabled: el.disabled === true || el.getAttribute('aria-disabled') === 'true' || undefined,
    contenteditable: el.getAttribute('contenteditable') || undefined,
    data: Object.fromEntries([...el.attributes].filter((a) => a.name.startsWith('data-')).map((a) => [a.name, sens ? '<value>' : cut(redact(a.value), 30)])),
    rect: (() => { const r = el.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)]; })(),
  });
  const path = (el) => { const out = []; for (let e = el; e && e !== document.body && out.length < 8; e = e.parentElement) out.push(e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.classList[0] ? '.' + e.classList[0] : '')); return out.join(' < '); };
  const vis = (el) => { const r = el.getBoundingClientRect(); return r.width > 1 && r.height > 1; };

  const out = { url: redact(location.href), lang: document.documentElement.lang };

  // Where is the chat list and the message pane? Anchors that end in /chat/<id> are chat rows.
  const rows = [...document.querySelectorAll('a[href*="/chat/"]')].filter(vis);
  out.chatRows = { count: rows.length, sample: rows.slice(0, 2).map((a) => ({ ...desc(a, true), href: redact(a.getAttribute('href')), childTags: [...a.querySelectorAll('*')].slice(0, 12).map((c) => c.tagName.toLowerCase() + (c.classList[0] ? '.' + c.classList[0] : '')) })) };
  const list = rows[0] && (() => { let e = rows[0].parentElement; while (e && e.scrollHeight <= e.clientHeight + 4) e = e.parentElement; return e; })();
  out.listScroller = list ? { ...desc(list), path: path(list), scrollHeight: list.scrollHeight, clientHeight: list.clientHeight } : null;
  const search = [...document.querySelectorAll('input')].filter(vis);
  out.inputs = search.map((i) => ({ ...desc(i), path: path(i) }));

  // Composer candidates: textarea / contenteditable / textbox.
  const comp = [...document.querySelectorAll('textarea, [contenteditable="true"], [role="textbox"]')].filter(vis);
  out.composerCandidates = comp.map((c) => ({ ...desc(c), path: path(c), valueLength: (c.value ?? c.innerText ?? '').length }));
  out.fileInputs = [...document.querySelectorAll('input[type=file]')].map((i) => ({ ...desc(i), accept: i.accept, multiple: i.multiple, path: path(i) }));

  // Buttons outside the list and message pane: labels are UI text, not customer text.
  const pane = comp[0] && (() => { let e = comp[0].parentElement; for (let i = 0; i < 6 && e && e.parentElement; i++) e = e.parentElement; return e; })();
  const btns = [...document.querySelectorAll('button, [role=button], a.btn, input[type=button], input[type=submit]')].filter(vis).filter((b) => !rows.some((r) => r.contains(b)));
  out.buttons = btns.slice(0, 80).map((b) => ({ ...desc(b), label: cut(b.innerText || b.value), inComposerArea: pane ? pane.contains(b) : undefined, path: path(b) }));

  // Overlays / banners (the auto-reply bar and the cover over the composer).
  out.overlays = [...document.querySelectorAll('[class*=overlay], [class*=cover], [class*=banner], [class*=alert], [role=alert], [role=dialog]')].filter(vis).slice(0, 12).map((o) => ({ ...desc(o), label: cut(o.innerText, 60), path: path(o) }));

  // Message pane: structure of bubbles WITHOUT text. Bubbles are guessed as elements near the composer's column that contain time labels.
  const timeEls = [...document.querySelectorAll('time, [class*=time], [class*=date]')].filter(vis).slice(0, 6).map((t) => ({ ...desc(t), pattern: cut(t.textContent, 24).replace(/\d/g, '9') }));
  out.timeLabels = timeEls;
  const mid = comp[0] ? comp[0].getBoundingClientRect() : null;
  const bubbles = mid ? [...document.querySelectorAll('div, li, p')].filter((e) => { const r = e.getBoundingClientRect(); return vis(e) && r.bottom < mid.top && r.left > mid.left - 40 && e.children.length < 6 && e.innerText && e.innerText.length > 0 && e.innerText.length < 400; }).slice(-8) : [];
  out.bubbleSamples = bubbles.map((b) => ({ ...desc(b, true), textLength: b.innerText.length, parentCls: cut(b.parentElement && b.parentElement.className, 80), alignGuess: (() => { const r = b.getBoundingClientRect(); return r.left + r.width / 2 > (mid.left + mid.width / 2) ? 'right' : 'left'; })() }));

  // Header of the open chat: the element above the message pane that contains a heading-like node.
  out.headings = [...document.querySelectorAll('h1,h2,h3,h4,[class*=title],[class*=name]')].filter(vis).filter((h) => !rows.some((r) => r.contains(h))).slice(0, 8).map((h) => ({ ...desc(h, true), textLength: (h.innerText || '').length, path: path(h) }));

  console.log(JSON.stringify(out, null, 2));
  return 'probe done: copy the JSON above';
})();
