// Content script injected into the Facebook group tab by the background worker.
// It only performs small atomic DOM actions; all timing/randomness is driven by
// background.js so it keeps working even when the tab is in the background
// (where page timers get throttled).
(() => {
  if (window.__fbapLoaded) return;
  window.__fbapLoaded = true;

  const COMPOSER_PATTERNS = [
    /write something/i,
    /create a public post/i,
    /create a post/i,
    /what's on your mind/i,
    /start a discussion/i,
    /เขียนอะไรสักหน่อย/,
    /เขียนอะไรบางอย่าง/,
    /สร้างโพสต์สาธารณะ/,
    /สร้างโพสต์/,
    /คุณคิดอะไรอยู่/,
    /เริ่มการสนทนา/,
  ];
  const POST_LABELS = ['post', 'โพสต์', 'publish', 'เผยแพร่'];
  const NEXT_LABELS = ['next', 'ถัดไป'];
  const PHOTO_PATTERNS = [/^photo\/video$/i, /^รูปภาพ\/วิดีโอ$/, /^photo$/i, /^รูปภาพ$/];
  const JOIN_LABELS = ['join group', 'เข้าร่วมกลุ่ม'];
  const CLOSE_LABELS = ['close', 'ปิด'];
  const DISCARD_LABELS = ['discard', 'ทิ้ง', 'ละทิ้ง', 'leave', 'leave page', 'ออกจากหน้า'];

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const rnd = (a, b) => a + Math.random() * (b - a);

  function visible(el) {
    if (!el || !el.isConnected) return false;
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none';
  }

  async function waitFor(fn, timeout = 15000, interval = 300) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
      try {
        const v = fn();
        if (v) return v;
      } catch {
        /* keep polling */
      }
      await sleep(interval);
    }
    return null;
  }

  const textOf = (el) => (el.textContent || '').trim();

  function matchesComposer(t) {
    return t.length > 0 && t.length < 90 && COMPOSER_PATTERNS.some((re) => re.test(t));
  }

  function findComposerTrigger() {
    const root = document.querySelector('[role="main"]') || document.body;
    for (const b of root.querySelectorAll('[role="button"]')) {
      if (b.closest('[role="dialog"]')) continue;
      if (matchesComposer(textOf(b)) && visible(b)) return b;
    }
    for (const s of root.querySelectorAll('span')) {
      if (s.closest('[role="dialog"]')) continue;
      if (!matchesComposer(textOf(s))) continue;
      const b = s.closest('[role="button"]') || s;
      if (visible(b)) return b;
    }
    return null;
  }

  // The open "Create post" composer: newest dialog that holds an editable box.
  function findComposer() {
    const dialogs = [...document.querySelectorAll('[role="dialog"]')].reverse();
    for (const d of dialogs) {
      const editor = [...d.querySelectorAll('[contenteditable="true"]')].find(
        (e) => visible(e) && (e.getAttribute('role') === 'textbox' || e.isContentEditable)
      );
      if (editor) return { dialog: d, editor };
    }
    return null;
  }

  function findButton(root, labels) {
    const els = [...root.querySelectorAll('[role="button"], button, [type="submit"]')];
    for (const el of els) {
      const a = (el.getAttribute('aria-label') || '').trim().toLowerCase();
      if (labels.includes(a) && visible(el)) return el;
    }
    for (const el of els) {
      if (labels.includes(textOf(el).toLowerCase()) && visible(el)) return el;
    }
    return null;
  }

  function findButtonByPattern(root, patterns) {
    for (const el of root.querySelectorAll('[role="button"], button, [aria-label]')) {
      const a = (el.getAttribute('aria-label') || textOf(el)).trim();
      if (a && patterns.some((re) => re.test(a)) && visible(el)) return el;
    }
    return null;
  }

  function findInDialogs(labels) {
    for (const d of [...document.querySelectorAll('[role="dialog"]')].reverse()) {
      const b = findButton(d, labels);
      if (b) return b;
    }
    return null;
  }

  const isDisabled = (el) =>
    el.getAttribute('aria-disabled') === 'true' || el.disabled === true;

  // Full pointer/mouse sequence at a random point inside the element.
  async function humanClick(el) {
    el.scrollIntoView({ block: 'center', inline: 'nearest' });
    await sleep(rnd(120, 350));
    const r = el.getBoundingClientRect();
    const x = r.left + r.width * rnd(0.3, 0.7);
    const y = r.top + r.height * rnd(0.3, 0.7);
    const hit = document.elementFromPoint(x, y);
    const t = hit && el.contains(hit) ? hit : el;
    const base = { bubbles: true, cancelable: true, composed: true, clientX: x, clientY: y, button: 0 };
    const ptr = { ...base, pointerId: 1, pointerType: 'mouse', isPrimary: true };
    t.dispatchEvent(new PointerEvent('pointerover', ptr));
    t.dispatchEvent(new MouseEvent('mouseover', base));
    t.dispatchEvent(new PointerEvent('pointermove', ptr));
    t.dispatchEvent(new MouseEvent('mousemove', base));
    await sleep(rnd(60, 200));
    t.dispatchEvent(new PointerEvent('pointerdown', { ...ptr, buttons: 1 }));
    t.dispatchEvent(new MouseEvent('mousedown', { ...base, buttons: 1 }));
    await sleep(rnd(50, 150));
    t.dispatchEvent(new PointerEvent('pointerup', ptr));
    t.dispatchEvent(new MouseEvent('mouseup', base));
    t.dispatchEvent(new MouseEvent('click', base));
  }

  async function scrollPage(dy) {
    const steps = document.hidden ? 1 : 6 + Math.floor(Math.random() * 8);
    for (let i = 0; i < steps; i++) {
      window.scrollBy(0, dy / steps);
      if (steps > 1) await sleep(rnd(18, 55));
    }
    return { ok: true, y: window.scrollY };
  }

  async function scrollTop() {
    const y = window.scrollY;
    if (y > 0) await scrollPage(-y);
    window.scrollTo(0, 0);
    return { ok: true };
  }

  function pageCheck() {
    const path = location.pathname;
    const login =
      /^\/login/.test(path) ||
      !!document.querySelector('#login_form, form[action*="/login"] input[name="pass"]');
    const checkpoint = /^\/checkpoint/.test(path);
    return {
      ok: true,
      url: location.href,
      login,
      checkpoint,
      hasComposer: !!findComposerTrigger(),
      title: document.title,
    };
  }

  // ---------- editor helpers ----------

  function ensureCaret(editor) {
    if (document.activeElement !== editor && !editor.contains(document.activeElement)) {
      editor.focus();
    }
    const sel = window.getSelection();
    if (!sel.rangeCount || !editor.contains(sel.anchorNode)) {
      const range = document.createRange();
      range.selectNodeContents(editor);
      range.collapse(false);
      sel.removeAllRanges();
      sel.addRange(range);
    }
  }

  // Returns true when the page handled the key (called preventDefault).
  function pressKey(target, key, code, keyCode, extra = {}) {
    const opts = {
      key,
      code,
      keyCode,
      which: keyCode,
      bubbles: true,
      cancelable: true,
      composed: true,
      ...extra,
    };
    const notCanceled = target.dispatchEvent(new KeyboardEvent('keydown', opts));
    target.dispatchEvent(new KeyboardEvent('keyup', opts));
    return !notCanceled;
  }

  function pasteText(editor, text) {
    const dt = new DataTransfer();
    dt.setData('text/plain', text);
    const ev = new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true });
    return !editor.dispatchEvent(ev);
  }

  function requireComposer() {
    const c = findComposer();
    if (!c) throw new Error('หน้าต่างเขียนโพสต์ถูกปิดไปแล้ว');
    return c;
  }

  async function openComposer() {
    if (findComposer()) return { ok: true, already: true };
    const trigger = await waitFor(findComposerTrigger, 20000, 500);
    if (!trigger) {
      const join = findButton(document.body, JOIN_LABELS);
      return {
        ok: false,
        error: join
          ? 'ยังไม่ได้เป็นสมาชิกกลุ่มนี้ (มีปุ่มเข้าร่วมกลุ่ม)'
          : 'ไม่พบช่อง "เขียนอะไรสักหน่อย" ในกลุ่มนี้',
      };
    }
    await humanClick(trigger);
    let c = await waitFor(findComposer, 15000, 300);
    if (!c) {
      await humanClick(trigger);
      c = await waitFor(findComposer, 10000, 300);
    }
    if (!c) return { ok: false, error: 'เปิดหน้าต่างเขียนโพสต์ไม่ได้' };
    await sleep(rnd(300, 900));
    await humanClick(c.editor);
    ensureCaret(c.editor);
    return { ok: true };
  }

  function insertText(text) {
    const { editor } = requireComposer();
    ensureCaret(editor);
    const done = document.execCommand('insertText', false, text);
    if (!done) pasteText(editor, text);
    return { ok: true };
  }

  // Facebook's suggestion popup (mentions, hashtags, emoji) is open.
  function suggestionsOpen(editor) {
    if (editor.getAttribute('aria-expanded') === 'true') return true;
    if (editor.getAttribute('aria-activedescendant')) return true;
    return [...document.querySelectorAll('[role="listbox"]')].some(visible);
  }

  // Never presses Enter: with a suggestion popup open, Enter would pick a
  // suggested word instead of breaking the line. Paste a line break instead.
  function newline() {
    const { editor } = requireComposer();
    ensureCaret(editor);
    const suggestions = suggestionsOpen(editor);
    if (!pasteText(editor, '\n')) {
      if (!document.execCommand('insertLineBreak')) document.execCommand('insertParagraph');
    }
    return { ok: true, suggestions };
  }

  function backspace() {
    const { editor } = requireComposer();
    ensureCaret(editor);
    const handled = pressKey(editor, 'Backspace', 'Backspace', 8);
    if (!handled) document.execCommand('delete');
    return { ok: true };
  }

  // Pastes text at the caret (long texts with page tags go in parts).
  function pasteAtCaret(text) {
    const { editor } = requireComposer();
    ensureCaret(editor);
    if (!pasteText(editor, text)) document.execCommand('insertText', false, text);
    return { ok: true };
  }

  const squash = (s) => String(s || '').replace(/\s+/g, ' ').trim().toLowerCase();

  // Visible suggestion of the "@" popup whose text holds the page name.
  function findTagOption(name) {
    const want = squash(name);
    for (const box of document.querySelectorAll('[role="listbox"]')) {
      if (!visible(box)) continue;
      for (const o of box.querySelectorAll('[role="option"]')) {
        if (visible(o) && squash(o.innerText || o.textContent).includes(want)) return o;
      }
    }
    return null;
  }

  // After "@query" was typed: picks the page from Facebook's suggestions, which
  // turns the typed text into the page tag (bold name linking to the page).
  async function pickTag(name) {
    const { editor } = requireComposer();
    const option = await waitFor(() => findTagOption(name), 8000, 400);
    if (!option) return { ok: true, picked: false };
    await humanClick(option);
    await waitFor(() => !findTagOption(name), 3000, 300);
    ensureCaret(editor);
    return { ok: true, clicked: true, picked: squash(editor.innerText).includes(squash(name)) };
  }

  function getText() {
    const c = findComposer();
    if (!c) return { ok: false, open: false, text: '' };
    return { ok: true, open: true, text: c.editor.innerText || c.editor.textContent || '' };
  }

  // Replace the whole editor content (used for long texts = "copy & paste"
  // behaviour, and as a fallback when typing went wrong).
  async function setText(text) {
    const { editor } = requireComposer();
    ensureCaret(editor);
    // Ctrl+A inside the editor; fall back to the native command.
    if (!pressKey(editor, 'a', 'KeyA', 65, { ctrlKey: true })) document.execCommand('selectAll');
    await sleep(150);
    // Paste replaces the selection (keeps line breaks in Facebook's editor).
    if (!pasteText(editor, text)) document.execCommand('insertText', false, text);
    return { ok: true };
  }

  // ---------- images ----------

  function dataUrlToFile(rec, i) {
    const [head, b64] = rec.data.split(',');
    const mime = rec.type || (head.match(/data:([^;]+)/) || [])[1] || 'image/jpeg';
    const bin = atob(b64);
    const arr = new Uint8Array(bin.length);
    for (let k = 0; k < bin.length; k++) arr[k] = bin.charCodeAt(k);
    const ext = (mime.split('/')[1] || 'jpg').replace('jpeg', 'jpg');
    return new File([arr], rec.name || `image_${i + 1}.${ext}`, {
      type: mime,
      lastModified: Date.now() - Math.floor(Math.random() * 86400000),
    });
  }

  // Campaign images are stored as img:<id>; posts from the web app pass full keys (cloudimg:<id>).
  async function loadFiles(ids) {
    const keys = ids.map((id) => (String(id).includes(':') ? String(id) : 'img:' + id));
    const data = await chrome.storage.local.get(keys);
    return keys.map((k) => data[k]).filter(Boolean).map(dataUrlToFile);
  }

  function mediaCount(dialog) {
    let n = 0;
    for (const img of dialog.querySelectorAll('img')) {
      const r = img.getBoundingClientRect();
      if ((img.src || '').startsWith('blob:') || (r.width >= 70 && r.height >= 70)) n++;
    }
    return n + dialog.querySelectorAll('video').length;
  }

  function snapshot(dialog) {
    return { media: mediaCount(dialog), nodes: dialog.querySelectorAll('*').length };
  }

  function grew(dialog, before) {
    const now = snapshot(dialog);
    return (
      now.media > before.media ||
      now.nodes - before.nodes > 15 ||
      !!dialog.querySelector('[role="progressbar"]')
    );
  }

  function findFileInput(dialog) {
    const inputs = [...dialog.querySelectorAll('input[type="file"]')];
    return (
      inputs.find((i) => /image/.test(i.accept || '')) ||
      inputs.find((i) => !i.accept) ||
      inputs[0] ||
      null
    );
  }

  async function attachImages(ids) {
    const c = requireComposer();
    const files = await loadFiles(ids || []);
    if (!files.length) return { ok: false, error: 'ไม่พบไฟล์รูปในที่เก็บข้อมูล' };
    const makeDT = () => {
      const dt = new DataTransfer();
      files.forEach((f) => dt.items.add(f));
      return dt;
    };
    const before = snapshot(c.dialog);
    const attached = () => waitFor(() => grew(c.dialog, before), 20000, 500);

    // 1) hidden file input inside the composer (click "Photo/video" first if needed)
    let input = findFileInput(c.dialog);
    if (!input) {
      const photoBtn = findButtonByPattern(c.dialog, PHOTO_PATTERNS);
      if (photoBtn) {
        await humanClick(photoBtn);
        input = await waitFor(() => findFileInput(c.dialog), 6000, 300);
      }
    }
    if (input) {
      input.files = makeDT().files;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
      if (await attached()) return { ok: true, method: 'input' };
    }

    // 2) paste the images into the editor
    ensureCaret(c.editor);
    c.editor.dispatchEvent(
      new ClipboardEvent('paste', { clipboardData: makeDT(), bubbles: true, cancelable: true })
    );
    if (await attached()) return { ok: true, method: 'paste' };

    // 3) drag & drop onto the editor
    const dt = makeDT();
    const r = c.editor.getBoundingClientRect();
    const at = { bubbles: true, cancelable: true, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 };
    for (const type of ['dragenter', 'dragover', 'drop']) {
      c.editor.dispatchEvent(new DragEvent(type, { ...at, dataTransfer: dt }));
      await sleep(rnd(80, 200));
    }
    if (await attached()) return { ok: true, method: 'drop' };

    return { ok: false, error: 'แนบรูปไม่สำเร็จ (ไม่พบช่องอัปโหลดรูป)' };
  }

  // ---------- submit ----------

  function postState() {
    const c = findComposer();
    if (!c) return { ok: true, open: false };
    const btn = findButton(c.dialog, POST_LABELS);
    return {
      ok: true,
      open: true,
      hasPost: !!btn,
      postEnabled: !!btn && !isDisabled(btn),
      hasNext: !btn && !!findButton(c.dialog, NEXT_LABELS),
      uploading: !!c.dialog.querySelector('[role="progressbar"]'),
      media: mediaCount(c.dialog),
      blocked: blockCheck().blocked,
    };
  }

  async function clickPost() {
    const c = requireComposer();
    let btn = findButton(c.dialog, POST_LABELS);
    if (!btn) {
      const next = findButton(c.dialog, NEXT_LABELS);
      if (next && !isDisabled(next)) {
        await humanClick(next);
        btn = await waitFor(() => {
          const cc = findComposer();
          return (cc && findButton(cc.dialog, POST_LABELS)) || findInDialogs(POST_LABELS);
        }, 10000, 400);
      }
    }
    if (!btn) return { ok: false, error: 'ไม่พบปุ่ม "โพสต์"' };
    const enabled = await waitFor(() => !isDisabled(btn), 15000, 500);
    if (!enabled) return { ok: false, error: 'ปุ่ม "โพสต์" ยังกดไม่ได้' };
    await humanClick(btn);
    return { ok: true };
  }

  // ---------- Facebook warnings ----------

  // Facebook's own wording of "slow down / blocked / restricted" notices.
  // Only whole phrases Facebook uses: ordinary words like "เร็วเกินไป" or
  // "ถูกบล็อก" in a chat or in group rules must not pause posting.
  const BLOCK_RE = new RegExp(
    [
      "you(?:'|’)re temporarily blocked",
      'you(?:\'|’)ve been temporarily blocked',
      'temporarily blocked from',
      "you(?:'|’)re temporarily restricted",
      "you can(?:'|’)t (?:post|use this feature) right now",
      'misus(?:e|ing) this feature by going too fast',
      'we limit how often you can',
      'your (?:post|content) looks like spam',
      'goes against our community standards',
      'your account (?:has been|is) restricted',
      'restricted from posting',
      'คุณถูกบล็อ[กค](?:ชั่วคราว|ไม่ให้)',
      'ถูกบล็อ[กค]ชั่วคราว(?:ไม่ให้|จากการ)',
      'บล็อ[กค]คุณชั่วคราว',
      'ใช้(?:ฟีเจอร์|คุณสมบัติ)นี้(?:ในทางที่ผิด|เร็วเกินไป)',
      'คุณไม่สามารถ(?:โพสต์|ใช้ฟีเจอร์นี้|ใช้คุณสมบัตินี้)ได้(?:ในขณะนี้|ในตอนนี้|ตอนนี้)',
      'เราจำกัด(?:ความถี่|จำนวนครั้ง)',
      'บัญชีของคุณถูกจำกัด',
      'ถูกจำกัดไม่ให้โพสต์',
      'ขัดต่อมาตรฐานชุมชนของเรา',
    ].join('|'),
    'i'
  );

  // Text of dialogs/alerts only (never the feed, never what we typed).
  function noticeText(root) {
    const parts = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let n;
    let size = 0;
    while ((n = walker.nextNode()) && size < 3000) {
      if (n.parentElement && n.parentElement.closest('[contenteditable="true"]')) continue;
      const t = n.nodeValue.trim();
      if (t) {
        parts.push(t);
        size += t.length;
      }
    }
    return parts.join(' ');
  }

  function blockCheck() {
    const roots = document.querySelectorAll('[role="dialog"], [role="alertdialog"], [role="alert"]');
    for (const r of roots) {
      // Screen-reader alerts may be tiny but are rendered; display:none ones are not.
      const shown = r.getAttribute('role') === 'alert' ? r.getClientRects().length > 0 : visible(r);
      if (!shown) continue;
      const text = noticeText(r);
      const m = text.match(BLOCK_RE);
      if (m) {
        const at = Math.max(0, m.index - 60);
        return { ok: true, blocked: true, text: text.slice(at, at + 220) };
      }
    }
    return { ok: true, blocked: false };
  }

  async function discard() {
    const c = findComposer();
    if (!c) return { ok: true };
    const close = findButton(c.dialog, CLOSE_LABELS);
    if (close) {
      await humanClick(close);
      const confirm = await waitFor(() => findInDialogs(DISCARD_LABELS), 4000, 300);
      if (confirm) await humanClick(confirm);
    }
    return { ok: true };
  }

  const handlers = {
    ping: async () => ({ ok: true }),
    check: async () => pageCheck(),
    scroll: async (m) => scrollPage(Number(m.dy) || 0),
    scrollTop,
    openComposer,
    insert: async (m) => insertText(String(m.text || '')),
    newline: async () => newline(),
    backspace: async () => backspace(),
    getText: async () => getText(),
    setText: async (m) => setText(String(m.text || '')),
    paste: async (m) => pasteAtCaret(String(m.text || '')),
    pickTag: async (m) => pickTag(String(m.name || '')),
    attachImages: async (m) => attachImages(m.imageIds),
    postState: async () => postState(),
    clickPost,
    discard,
    blockCheck: async () => blockCheck(),
  };

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (!msg || msg.target !== 'fbap-content') return false;
    const h = handlers[msg.cmd];
    if (!h) {
      sendResponse({ ok: false, error: 'unknown command: ' + msg.cmd });
      return false;
    }
    Promise.resolve()
      .then(() => h(msg))
      .then(sendResponse, (e) => sendResponse({ ok: false, error: String((e && e.message) || e) }));
    return true;
  });
})();
