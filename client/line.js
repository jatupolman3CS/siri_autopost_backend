// Content script injected into the chat.line.biz tab (LINE Official Account Manager -> Chats) by the background worker, exactly
// like content.js is injected into a Facebook tab. It speaks the same message protocol ({ target: 'fbap-content', cmd, ... }
// -> { ok, ... }) for the commands the typing/attach code already uses (check, scroll, scrollTop, insert, newline, backspace,
// getText, setText, paste, attachImages, blockCheck) plus the LINE-only ones (lineListChats, lineClickRow, ...). It only
// performs small atomic DOM actions; all timing and randomness is driven by lib/line-flow.js in the service worker.
//
// !! Nobody has seen the real chat.line.biz DOM yet (spec: LINE_OA_SPEC.md section 3). Everything that depends on the page
// !! lives in SELECTORS / LABELS / BLOCK_PATTERNS below, as candidate lists tried in order; WP0 (tools/line-dom-probe.js, run
// !! by the shop owner) and the manual pilot refine them. Where no selector is known the code falls back on the page's
// !! geometry. A lookup that fails reports a Thai error that names the missing element, never a generic one.
//
// Privacy: the only personal datum this script ever returns is a customer's display name (chat list rows, chat header). It
// never returns the text of a message, not even in a log or an error (lineChatState carries counts, flags and a time).
(() => {
  if (window.__fbapLineLoaded) return;
  window.__fbapLineLoaded = true;

  // ---------- everything that depends on LINE's page ----------
  //
  // An entry of a candidate list is a CSS selector string, or { css?, label: RegExp } = elements of `css` (default: buttons,
  // links and anything with an aria-label/title) whose aria-label, title or short own text matches the pattern.
  const SELECTORS = {
    // The chat list (middle column) or contacts table (รายชื่อติดต่อ).
    chatRow: [
      '.list-group-item-chat[data-fbap-uid]', // the real chat list; the id is copied there by line-flow.js (stampChatRows)
      'a[href*="/chat/"]',
      'table tbody tr',
      '[role="row"]',
      '[class*="chat-item" i]',
      '[class*="contact-item" i]',
      '[class*="row" i]:has(button, a)'
    ],
    listScroller: [
      'table tbody',
      'table',
      '[class*="table-container" i]',
      '[class*="table-responsive" i]',
      '[class*="chatlist" i]',
      '[class*="chat-list" i]',
      '[class*="ChatList"]',
      '[class*="list-group"]',
      '[class*="contact" i]',
      '[class*="contacts" i]'
    ],
    rowName: ['[class*="name" i]', '[class*="title" i]', 'strong', 'h3', 'h4', 'h5', 'td:nth-child(1)', 'td:nth-child(2)'],
    rowLabel: ['[class*="tag" i]', '[class*="label" i]', '[class*="chip" i]', '[class*="badge" i]'],
    rowTime: ['time', '[class*="time" i]', '[class*="date" i]'],
    rowUnread: ['[class*="unread" i]', '[class*="badge" i]'],
    search: ['input[type="search"]', 'input[placeholder*="ค้นหา"]', 'input[placeholder*="search" i]', 'input[type="text"]'],
    listFilter: ['[class*="filter" i]', 'select'],
    // The open chat (right column).
    messagePane: ['[class*="messages" i]', '[class*="message-list" i]', '[class*="chat-body" i]', '[class*="chatroom" i]'],
    headerName: [
      '[class*="chat-header" i] [class*="name" i]',
      '[class*="chatheader" i] [class*="name" i]',
      'header h1', 'header h2', 'header h3',
      '[class*="header" i] [class*="name" i]',
      '[class*="header" i] [class*="title" i]',
      '[class*="chat-title" i]',
      '[class*="room-name" i]',
      'h4', 'h5' // the real page: the customer's name is an <h4> over the conversation (checked 2026-10-27)
    ],
    oaName: ['[class*="account" i] [class*="name" i]', '[class*="bot" i] [class*="name" i]', 'header [class*="name" i]', '[class*="gnb" i] [class*="name" i]'],
    // The message box. Assumption A2: a <textarea>; a contenteditable is the fallback.
    composer: [
      'textarea',
      '[contenteditable="true"][role="textbox"]',
      '[contenteditable="true"]',
      'div[role="textbox"]',
      '[class*="composer" i] [contenteditable]',
      '[class*="editor" i]'
    ],
    composerOverlay: ['[class*="overlay" i]', '[class*="cover" i]', '[class*="mask" i]', '[class*="disabled" i]'],
    // Assumption A3: the green "ส่ง" button; Enter is never pressed.
    sendButton: [
      '.send-group input[type="submit"]', // the real page: a green <input type="submit"> (checked 2026-10-27)
      { label: /^\s*(?:ส่ง|send)\s*$/i },
      'button[type="submit"]',
      'button[class*="send" i]',
      '[class*="send" i] button',
      'button[aria-label*="ส่ง" i]',
      'button[title*="ส่ง" i]',
      'button[aria-label*="send" i]',
      'button[title*="send" i]',
      'button.btn-primary',
      'button[data-testid*="send" i]'
    ],
    // Assumption A7: the attach icon opens / reveals a hidden <input type=file>.
    attachButton: [
      { label: /แนบ|attach|file|ไฟล์|รูป|image|photo/i },
      '[class*="attach" i]',
      '[class*="file" i]',
      '[class*="clip" i]',
      '[class*="image" i]',
      '[class*="upload" i]',
      '[aria-label*="แนบ" i]',
      '[title*="แนบ" i]',
      '[aria-label*="รูป" i]',
      '[title*="รูป" i]'
    ],
    fileInput: ['input[type="file"]'],
    // Assumption A6: outgoing bubbles on the right, incoming on the left; a failed send shows a red marker / resend control.
    bubbleOut: ['[class*="bubble" i][class*="outgoing" i]', '[class*="message" i][class*="outgoing" i]', '[class*="bubble" i][class*="right" i]', '[class*="message" i][class*="right" i]', '[class*="message" i][class*="mine" i]', '[class*="message" i][class*="self" i]'],
    bubbleIn: ['[class*="bubble" i][class*="incoming" i]', '[class*="message" i][class*="incoming" i]', '[class*="bubble" i][class*="left" i]', '[class*="message" i][class*="left" i]', '[class*="message" i][class*="received" i]', '[class*="message" i][class*="customer" i]'],
    bubbleTime: ['time', '[class*="time" i]'],
    dateDivider: ['[class*="divider" i]', '[class*="date-label" i]', '[class*="datetime" i]'],
    sendFailed: ['[class*="error" i]', '[class*="fail" i]', '[class*="resend" i]', { label: /ส่งไม่สำเร็จ|resend|failed to send|ส่งอีกครั้ง/i }],
    uploading: ['[role="progressbar"]', 'progress', '[class*="uploading" i]', '[class*="progress" i]', '[class*="spinner" i]', '[class*="loading" i]'],
    // Roots that may carry a rate-limit / captcha notice. The message pane and the chat list are never read for warnings.
    noticeRoots: ['[role="dialog"]', '[role="alertdialog"]', '[role="alert"]', '[class*="modal" i]', '[class*="toast" i]', '[class*="snackbar" i]', '[class*="notification" i]', '[class*="captcha" i]'],
    dialog: ['[role="dialog"]', '[role="alertdialog"]', '[class*="modal" i]', '[class*="dialog" i]'],
  };

  const LABELS = {
    // The button of the grey "auto-reply is active" bar / the overlay over the composer (screenshot: "แชทแบบแมนนวล").
    manualButton: /^\s*(?:แชทแบบแมนนวล|แชทแมนนวล|manual chat)\s*$/i,
    // The text of that bar and of the overlay (never a button).
    autoReplyBar: /กำลังใช้ข้อความตอบกลับอัตโนมัติ|using auto-?response|auto-?reply is on/i,
    overlayText: /อยู่ในเวลาตอบข้อความ|โปรดคลิก\s*[""“]?แชทแบบแมนนวล|click\s*[""“]?manual chat/i,
    // The confirm button of the dialog that may follow the click on the manual button (assumption A4).
    confirm: /^\s*(?:ตกลง|ยืนยัน|ใช่|ok|yes|confirm)\s*$/i,
    // A notice where the composer should be saying the customer cannot be written to (blocked the OA, chat deleted). A guess.
    cannotSend: /ไม่สามารถ(?:ส่งข้อความ|แชท)|ผู้ใช้นี้(?:ได้)?บล็อก|ถูกบล็อก|เลิกเป็นเพื่อน|unable to (?:send|chat)|has blocked/i,
    // The chat list's filter when it shows everything.
    filterAll: /^\s*(?:ทั้งหมด|all)\s*$/i,
    today: /^\s*(?:วันนี้|today)\s*$/i,
    yesterday: /^\s*(?:เมื่อวาน|yesterday)\s*$/i,
    // Assumption A11: login lost = a redirect to one of these hosts or a login form.
    loginHosts: ['account.line.biz', 'access.line.me', 'accounts.line.me', 'id.line.me', 'lineml.line.me'],
    loginPath: /^\/(?:login|signin|sign-in|auth)(?:[/?#]|$)/i,
  };

  // Conservative on purpose (D12): a hit rests the WHOLE device for 24-48 hours, so prefer a miss to a false positive.
  // Whole phrases of a captcha / rate-limit / restriction notice, Thai and English. Tested only against dialogs, toasts and
  // alerts, and against a bare error page; never against the chat list, the message pane or the composer.
  const BLOCK_PATTERNS = [
    /captcha/i,
    /verify (?:that )?you(?:'|’)?re (?:a )?human/i,
    /too many requests/i,
    /rate[- ]?limit(?:ed)? exceeded|you(?:'|’)ve been rate[- ]limited/i,
    /(?:account|access) (?:has been |is )?(?:temporarily )?(?:restricted|suspended|blocked)/i,
    /unusual (?:activity|traffic)/i,
    /ส่งข้อความ(?:บ่อย|เร็ว)เกินไป/,
    /คำขอ(?:มาก|บ่อย)เกินไป/,
    /ตรวจพบ(?:การใช้งาน|กิจกรรม)ที่ผิดปกติ/,
    /ถูกจำกัดการใช้งาน(?:ชั่วคราว)?/,
    /ยืนยัน(?:ว่า)?(?:คุณ)?ไม่ใช่(?:บอท|หุ่นยนต์|โปรแกรมอัตโนมัติ)/,
  ];

  // Assumption A1 (the same patterns as LineChatUrl in the API and lineChatTarget in lib/shared.js).
  const BOT_ID_RE = /^[A-Za-z0-9_-]{5,64}$/;
  const USER_ID_RE = /^[A-Za-z0-9_-]{10,64}$/;
  const CHAT_PATH_RE = /^\/([A-Za-z0-9_-]{5,64})\/chat\/([A-Za-z0-9_-]{10,64})/;
  // OA ids on the account chooser (chat.line.biz/): "U" + 32 hex; any other single path segment is a weaker guess.
  const OA_HEX_RE = /^U[0-9a-f]{32}$/i;
  const RESERVED_SEGMENTS = new Set(['login', 'logout', 'settings', 'setting', 'help', 'terms', 'privacy', 'account', 'accounts', 'static', 'assets', 'api', 'chat', 'chats', 'manager', 'home']);

  const MAX_ROUNDS = 300; // chat-list scroll rounds in one roster scan (the spec's 120 is too few for a shop with thousands of chats)
  const MAX_CHATS = 5000; // the API's limit per sync
  const TEXT_FIELD_LIMIT = 200;

  // ---------- helpers copied from content.js (a content script cannot import modules) ----------

  // "Photo/video" in the post composer; "Attach a photo or video" in a comment box. (LINE's attach icon uses SELECTORS.attachButton.)
  const PHOTO_PATTERNS = [
    /^photo\/video$/i, /^รูปภาพ\/วิดีโอ$/, /^photo$/i, /^รูปภาพ$/,
    /attach a photo/i, /^แนบรูปภาพ/, /^แนบรูป/,
  ];

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const rnd = (a, b) => a + Math.random() * (b - a);

  // The real message box is <textarea-ex id="editor"> with its <textarea> inside an OPEN shadow root (checked on the real page
  // 2026-10-27). querySelectorAll does not cross that border, so the inner field is searched through every open shadow root,
  // `contains` on the outer elements never sees it (compare with shellOf), and document.activeElement is the host.
  const shellOf = (el) => {
    const r = el && el.getRootNode ? el.getRootNode() : null;
    return r && r.host ? r.host : el;
  };

  function deepActive() {
    let a = document.activeElement;
    while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement;
    return a;
  }

  function shadowTextareas() {
    const out = [];
    for (const h of document.querySelectorAll('*')) {
      if (h.tagName.includes('-') && h.shadowRoot) out.push(...h.shadowRoot.querySelectorAll('textarea'));
    }
    return out;
  }

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

  function findButtonByPattern(root, patterns) {
    for (const el of root.querySelectorAll('[role="button"], button, [aria-label]')) {
      const a = (el.getAttribute('aria-label') || textOf(el)).trim();
      if (a && patterns.some((re) => re.test(a)) && visible(el)) return el;
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

  function ensureCaret(editor) {
    const active = deepActive();
    if (active !== editor && !editor.contains(active)) {
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

  // ---------- candidate lookup (the one place that reads SELECTORS) ----------

  const LABEL_DEFAULT_CSS = 'button, [role="button"], a[href], input[type="button"], input[type="submit"], [aria-label], [title]';

  function labelOf(el) {
    return (el.getAttribute('aria-label') || el.getAttribute('title') || el.value || (el.textContent || '')).replace(/\s+/g, ' ').trim();
  }

  // Every element one entry matches (no visibility filter).
  function matches(entry, root = document) {
    if (typeof entry === 'string') {
      try {
        return [...root.querySelectorAll(entry)];
      } catch {
        return []; // a selector this browser rejects must not break the others
      }
    }
    const out = [];
    for (const el of root.querySelectorAll(entry.css || LABEL_DEFAULT_CSS)) {
      const l = labelOf(el);
      if (l && l.length <= 60 && entry.label.test(l)) out.push(el);
    }
    return out;
  }

  // First visible element of the first entry that yields one; `ok` filters further.
  function first(list, root = document, ok = null) {
    for (const entry of list) {
      for (const el of matches(entry, root)) {
        if (visible(el) && (!ok || ok(el))) return el;
      }
    }
    return null;
  }

  // Visible elements of every entry, without duplicates.
  function every(list, root = document, ok = null) {
    const seen = new Set();
    for (const entry of list) {
      for (const el of matches(entry, root)) {
        if (!seen.has(el) && visible(el) && (!ok || ok(el))) seen.add(el);
      }
    }
    return [...seen];
  }

  // A button-like element whose label matches `re` (text of buttons only; never the message pane).
  function buttonLike(re, root = document) {
    for (const el of root.querySelectorAll('button, [role="button"], a, input[type="button"], input[type="submit"], [aria-label]')) {
      const l = labelOf(el);
      if (l && l.length <= 40 && re.test(l) && visible(el)) return el;
    }
    for (const el of root.querySelectorAll('span, div')) {
      if (el.childElementCount > 0) continue; // a bare text element styled as a button
      const l = labelOf(el);
      if (l && l.length <= 40 && re.test(l) && visible(el)) return el;
    }
    return null;
  }

  // ---------- the page: where things are ----------

  function currentChat() {
    const m = CHAT_PATH_RE.exec(location.pathname);
    if (m) return { botId: m[1], userId: m[2] };
    if (typeof URLSearchParams !== 'undefined') {
      try {
        const urlParams = new URLSearchParams(location.search);
        const qUser = urlParams.get('chatId') || urlParams.get('userId') || urlParams.get('u');
        if (qUser && USER_ID_RE.test(qUser)) return { botId: currentBotId(), userId: qUser };
      } catch {
        /* ignore */
      }
    }
    return null;
  }

  function currentBotId() {
    if (!/^(chat|manager)\.line\.biz$/i.test(location.hostname)) return '';
    const parts = location.pathname.split('/').filter(Boolean);
    if (!parts.length) return '';
    if (parts[0].toLowerCase() === 'account' && parts[1]) {
      const clean = parts[1].replace(/^@/, '');
      if (BOT_ID_RE.test(clean)) return clean;
    }
    const seg = parts[0];
    return BOT_ID_RE.test(seg) && !RESERVED_SEGMENTS.has(seg.toLowerCase()) ? seg : '';
  }

  const userIdOfHref = (href) => {
    const m = /\/chat\/([A-Za-z0-9_-]{10,64})/.exec(String(href || ''));
    return m ? m[1] : '';
  };

  const userIdOfEl = (el) => {
    if (!el) return '';
    const fromHref = userIdOfHref(el.getAttribute ? el.getAttribute('href') : '');
    if (fromHref) return fromHref;
    const fromData = el.getAttribute ? (el.getAttribute('data-fbap-uid') || el.getAttribute('data-user-id') || el.getAttribute('data-id') || el.getAttribute('data-chat-id')) : '';
    if (fromData && USER_ID_RE.test(fromData)) return fromData;
    const childLink = el.querySelector && el.querySelector('a[href*="/chat/"], [data-fbap-uid], [data-user-id], [data-id]');
    if (childLink) {
      const u = userIdOfHref(childLink.getAttribute('href')) || childLink.getAttribute('data-fbap-uid') || childLink.getAttribute('data-user-id') || childLink.getAttribute('data-id');
      if (u && USER_ID_RE.test(u)) return u;
    }
    return '';
  };

  const sameId = (a, b) => !!a && !!b && String(a).toLowerCase() === String(b).toLowerCase();

  function chatRows() {
    const fromSel = every(SELECTORS.chatRow, document, (a) => USER_ID_RE.test(userIdOfEl(a)));
    if (fromSel.length) return fromSel;
    const tableRows = [...document.querySelectorAll('table tbody tr, [role="row"]')].filter(visible);
    return tableRows.filter((tr) => !!(buttonLike(/^(?:แชท|chat)$/i, tr) || tr.querySelector('a[href*="/chat/"]')));
  }

  function rowFor(userId) {
    return chatRows().find((a) => sameId(userIdOfEl(a), userId) || sameId(userIdOfHref(a.getAttribute ? a.getAttribute('href') : ''), userId)) || null;
  }

  // Styled to scroll (a short conversation does not overflow yet, but its pane is the scroller all the same).
  const scrollStyle = (e) => /(auto|scroll|overlay)/.test(getComputedStyle(e).overflowY);

  function scrollable(e) {
    if (!e || e.scrollHeight <= e.clientHeight + 4) return false;
    return scrollStyle(e);
  }

  function scrollerOf(el) {
    for (let e = el && el.parentElement; e && e !== document.documentElement; e = e.parentElement) {
      if (scrollable(e)) return e;
    }
    return document.scrollingElement || document.documentElement;
  }

  const atBottom = (sc) => sc.scrollTop + sc.clientHeight >= sc.scrollHeight - 4;

  // Moves a scroller and tells the page's listeners at once. A browser draws (and so fires `scroll`) only for a window it
  // shows; the worker window may be covered or minimised, and a list that loads its next page on `scroll` would never grow
  // there: the scan would then take a half-loaded list for the whole roster.
  function scrollListBy(sc, dy) {
    if (sc === document.scrollingElement || sc === document.documentElement) window.scrollBy(0, dy);
    else sc.scrollTop += dy;
    try {
      (sc === document.scrollingElement || sc === document.documentElement ? document : sc).dispatchEvent(new Event('scroll'));
    } catch {
      /* the real event follows anyway */
    }
  }

  function listScroller() {
    const rows = chatRows();
    const bySelector = first(SELECTORS.listScroller, document, (e) => rows.some((r) => e.contains(r)) && scrollable(e));
    if (bySelector) return bySelector;
    return rows.length ? scrollerOf(rows[0]) : null;
  }

  function findSearch() {
    return first(SELECTORS.search);
  }

  // The area around a composer that holds the send button, the attach icon, previews and the overlay.
  function composerArea(composer) {
    if (!composer) return null;
    let e = shellOf(composer);
    for (let i = 0; i < 5 && e.parentElement; i++) {
      const p = e.parentElement;
      if (p.getBoundingClientRect().height > window.innerHeight * 0.45) break;
      e = p;
    }
    return e;
  }

  // The message box: a visible textarea / contenteditable that is not the search box and not inside the chat list. The page
  // may hold other text areas (a customer note, say): the one with the send button around it wins, then the lowest one.
  function findComposer() {
    const search = findSearch();
    const deep = shadowTextareas().filter((e) => visible(e) && !e.disabled);
    const list = deep.length ? deep : every(SELECTORS.composer, document, (e) => e !== search && e.tagName !== 'INPUT' && !e.closest('a[href*="/chat/"]'));
    if (list.length <= 1) return list[0] || null;
    const withSend = list.filter((c) => !!first(SELECTORS.sendButton, composerArea(c)));
    const pool = withSend.length ? withSend : list;
    return pool.sort((a, b) => b.getBoundingClientRect().bottom - a.getBoundingClientRect().bottom)[0];
  }

  // The scrollable column of messages above the composer.
  function messagePane(composer) {
    const bySelector = first(SELECTORS.messagePane, document, (e) => !e.querySelector('a[href*="/chat/"]') && (!composer || !e.contains(shellOf(composer))) && scrollStyle(e));
    if (bySelector) return bySelector;
    if (!composer) return null;
    const cr = composer.getBoundingClientRect();
    let best = null;
    let bestH = 0;
    for (const e of document.querySelectorAll('div, section, main, ul, ol')) {
      if (!scrollStyle(e) || !visible(e)) continue;
      const r = e.getBoundingClientRect();
      if (!(r.bottom <= cr.top + 12 && r.right > cr.left && r.left < cr.right)) continue; // above the composer, same column
      if (e.contains(shellOf(composer)) || e.querySelector('a[href*="/chat/"]')) continue; // not the list, not a wrapper
      if (r.height > bestH) {
        best = e;
        bestH = r.height;
      }
    }
    return best;
  }

  const isTextField = (el) => !!el && (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT');

  // What insert/newline/backspace/getText/setText/paste type into: the composer, or the list's search box between
  // lineFocusSearch and lineFocusComposer (the customer's name is typed there with the same pacing as any text).
  let typingTarget = 'composer';

  function editor() {
    if (typingTarget === 'search') {
      const s = findSearch();
      if (s) return s;
    }
    return findComposer();
  }

  function requireEditor() {
    const e = editor();
    if (!e) throw new Error(typingTarget === 'search' ? 'ไม่พบช่องค้นหาของรายชื่อแชท LINE' : 'ไม่พบช่องพิมพ์ข้อความของแชท LINE');
    return e;
  }

  function valueOf(el) {
    return isTextField(el) ? el.value : el.innerText || el.textContent || '';
  }

  function focusEditor(el) {
    if (deepActive() !== el) el.focus();
    if (isTextField(el)) {
      const n = el.value.length;
      if (el.selectionStart === 0 && el.selectionEnd === 0 && n > 0) el.setSelectionRange(n, n);
    } else {
      ensureCaret(el);
    }
  }

  // The framework behind the page listens for `input`; assigning .value alone would not reach its model.
  function setNativeValue(el, value) {
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    setter.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }

  // ---------- time labels ----------

  const MONTHS = {
    'ม.ค.': 1, 'มกราคม': 1, jan: 1, january: 1, 'ก.พ.': 2, 'กุมภาพันธ์': 2, feb: 2, february: 2, 'มี.ค.': 3, 'มีนาคม': 3, mar: 3, march: 3,
    'เม.ย.': 4, 'เมษายน': 4, apr: 4, april: 4, 'พ.ค.': 5, 'พฤษภาคม': 5, may: 5, 'มิ.ย.': 6, 'มิถุนายน': 6, jun: 6, june: 6,
    'ก.ค.': 7, 'กรกฎาคม': 7, jul: 7, july: 7, 'ส.ค.': 8, 'สิงหาคม': 8, aug: 8, august: 8, 'ก.ย.': 9, 'กันยายน': 9, sep: 9, sept: 9, september: 9,
    'ต.ค.': 10, 'ตุลาคม': 10, oct: 10, october: 10, 'พ.ย.': 11, 'พฤศจิกายน': 11, nov: 11, november: 11, 'ธ.ค.': 12, 'ธันวาคม': 12, dec: 12, december: 12,
  };
  const WEEKDAYS = {
    'อาทิตย์': 0, 'อา.': 0, sun: 0, sunday: 0, 'จันทร์': 1, 'จ.': 1, mon: 1, monday: 1, 'อังคาร': 2, 'อ.': 2, tue: 2, tues: 2, tuesday: 2,
    'พุธ': 3, 'พ.': 3, wed: 3, wednesday: 3, 'พฤหัสบดี': 4, 'พฤหัส': 4, 'พฤ.': 4, thu: 4, thur: 4, thurs: 4, thursday: 4,
    'ศุกร์': 5, 'ศ.': 5, fri: 5, friday: 5, 'เสาร์': 6, 'ส.': 6, sat: 6, saturday: 6,
  };
  const TIME_RE = /^\s*(\d{1,2})\s*[.:]\s*(\d{2})\s*(?:น\.?)?\s*(am|pm)?\s*$/i;

  const pad2 = (n) => String(n).padStart(2, '0');

  // "11.34 น." -> "11:34" (a label that is only a time of day), else null.
  function timeLabelToHHmm(label) {
    const m = TIME_RE.exec(String(label || ''));
    if (!m) return null;
    let h = Number(m[1]);
    const min = Number(m[2]);
    if (m[3]) {
      const pm = m[3].toLowerCase() === 'pm';
      if (h === 12) h = pm ? 12 : 0;
      else if (pm) h += 12;
    }
    return h > 23 || min > 59 ? null : `${pad2(h)}:${pad2(min)}`;
  }

  const startOfDay = (d) => new Date(d.getFullYear(), d.getMonth(), d.getDate());

  function normYear(y, now) {
    if (y == null) return now.getFullYear();
    let n = Number(y);
    if (n < 100) n += 2000;
    if (n > 2400) n -= 543; // Buddhist era
    return n;
  }

  // The time label of a chat-list row -> ISO-8601 (UTC) best effort, else null:
  //   "11.34 น." = today at that time, "เมื่อวาน" = yesterday 00:00, a weekday name = the latest such weekday 00:00,
  //   a date = that date 00:00. Anything unparseable = null (the API treats it as "unknown").
  function timeLabelToIso(label, nowDate = new Date()) {
    const s = String(label || '').replace(/\s+/g, ' ').trim();
    if (!s) return null;
    const today = startOfDay(nowDate);
    const hhmm = timeLabelToHHmm(s);
    if (hhmm) {
      const [h, m] = hhmm.split(':').map(Number);
      const d = new Date(today.getFullYear(), today.getMonth(), today.getDate(), h, m);
      return (d > nowDate ? nowDate : d).toISOString();
    }
    if (LABELS.today.test(s)) return today.toISOString();
    if (LABELS.yesterday.test(s)) return new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1).toISOString();
    const key = s.toLowerCase().replace(/[,\s]+$/, '');
    if (key in WEEKDAYS) {
      let back = (today.getDay() - WEEKDAYS[key] + 7) % 7;
      if (back === 0) back = 7; // today's weekday would have shown a time of day: it is a week ago
      return new Date(today.getFullYear(), today.getMonth(), today.getDate() - back).toISOString();
    }
    let m = /^(\d{1,2})\s*([^\d\s/.-][^\d\s/]*\.?)\s*(\d{2,4})?$/.exec(s); // 12 ต.ค. 2569
    if (m) {
      const mon = MONTHS[m[2].toLowerCase()] || MONTHS[m[2].toLowerCase().replace(/\.$/, '')];
      if (mon) return dateOrNull(normYear(m[3], nowDate), mon, Number(m[1]), nowDate, !m[3]);
    }
    m = /^(\d{4})[/-](\d{1,2})[/-](\d{1,2})$/.exec(s); // 2026-10-12
    if (m) return dateOrNull(normYear(m[1], nowDate), Number(m[2]), Number(m[3]), nowDate, false);
    m = /^(\d{1,2})[/.-](\d{1,2})(?:[/.-](\d{2,4}))?$/.exec(s); // 12/10/2026, 12/10
    if (m) return dateOrNull(normYear(m[3], nowDate), Number(m[2]), Number(m[1]), nowDate, !m[3]);
    return null;
  }

  function dateOrNull(year, month, day, nowDate, yearGuessed) {
    if (month < 1 || month > 12 || day < 1 || day > 31) return null;
    let d = new Date(year, month - 1, day);
    if (d.getMonth() !== month - 1) return null;
    if (yearGuessed && d > nowDate) d = new Date(year - 1, month - 1, day); // a date without a year is never in the future
    return d.toISOString();
  }

  // ---------- chat list rows (names, labels and times only; the preview of the last message is never read) ----------

  const firstTextNode = (root) => {
    const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let n;
    while ((n = w.nextNode())) {
      const t = (n.nodeValue || '').replace(/\s+/g, ' ').trim();
      if (t) return t;
    }
    return '';
  };

  // A short text node of the row that looks like a time label (such nodes are never a message preview).
  function shortTimeLikeText(root) {
    const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let n;
    let seen = 0;
    while ((n = w.nextNode()) && seen++ < 40) {
      const t = (n.nodeValue || '').replace(/\s+/g, ' ').trim();
      if (t && t.length <= 24 && timeLabelToIso(t) !== null) return t;
    }
    return '';
  }

  // A small filled, rounded box (a pill or a dot): how a count, a dot or a tag chip is drawn, whatever the page calls it.
  function filledPill(el, minRadius) {
    const cs = getComputedStyle(el);
    return rgbaAlpha(cs.backgroundColor) > 0.1 && (parseFloat(cs.borderTopLeftRadius) || 0) >= minRadius;
  }

  const leafElements = (row) => [...row.querySelectorAll('span, div, i, em, b, small')].filter((el) => el.childElementCount === 0 && visible(el));

  function rowUnread(row) {
    if (/unread/i.test(row.className || '')) return true;
    for (const entry of SELECTORS.rowUnread) {
      for (const el of matches(entry, row)) {
        if (!visible(el)) continue;
        if (/unread/i.test(el.className || '')) return true;
        const t = textOf(el);
        if (/^\d+\+?$/.test(t) || (t === '' && el.getBoundingClientRect().width <= 20)) return true; // a count or a dot
      }
    }
    // Geometry: a count in a filled pill (a few digits), or a small filled dot with no text.
    for (const el of leafElements(row)) {
      const t = textOf(el);
      const r = el.getBoundingClientRect();
      if (/^\d{1,3}\+?$/.test(t) && filledPill(el, 6)) return true;
      if (!t && r.width >= 4 && r.width <= 14 && r.height >= 4 && r.height <= 14 && filledPill(el, r.width / 2 - 1)) return true;
    }
    return false;
  }

  function rowContainerOf(el) {
    return (el && el.closest && el.closest('tr, [role="row"], li, [class*="item" i], [class*="card" i]')) || el;
  }

  function parseRow(a) {
    const container = rowContainerOf(a);
    const userId = userIdOfEl(a) || userIdOfEl(container) || userIdOfHref(a.getAttribute ? a.getAttribute('href') : '');
    let name = '';
    for (const entry of SELECTORS.rowName) {
      const el = matches(entry, container).find((e) => visible(e) && textOf(e));
      if (el) {
        const val = firstTextNode(el);
        if (val && !/^(?:แชท|chat|ดูรายละเอียด|view details)$/i.test(val)) {
          name = val;
          break;
        }
      }
    }
    if (!name) {
      const val = firstTextNode(container);
      if (val && !/^(?:แชท|chat|ดูรายละเอียด|view details)$/i.test(val)) name = val;
    }
    if (!name) name = firstTextNode(a);
    const labels = [];
    for (const entry of SELECTORS.rowLabel) {
      for (const el of matches(entry, container)) {
        const val = textOf(el).replace(/\s+/g, ' ');
        if (visible(el) && val && val.length <= 40 && !labels.includes(val) && val !== name && !/^(?:แชท|chat)$/i.test(val)) labels.push(val);
      }
    }
    if (!labels.length) {
      // No known class for a tag chip: a short text in a small filled box that is neither a count nor a time is one.
      for (const el of leafElements(container)) {
        const val = textOf(el).replace(/\s+/g, ' ');
        if (!val || val.length > 30 || val === name || /^\d{1,3}\+?$/.test(val) || timeLabelToIso(val) !== null || labels.includes(val) || /^(?:แชท|chat)$/i.test(val)) continue;
        if (filledPill(el, 3)) labels.push(val);
      }
    }
    let when = '';
    for (const entry of SELECTORS.rowTime) {
      const el = matches(entry, container).find((e) => visible(e) && textOf(e) && textOf(e).length <= 24);
      if (el) {
        when = textOf(el);
        break;
      }
    }
    if (!when || timeLabelToIso(when) === null) when = shortTimeLikeText(container) || when;
    return {
      userId,
      name: name.slice(0, TEXT_FIELD_LIMIT),
      labels: labels.slice(0, 10),
      lastMessageAt: timeLabelToIso(when),
      unread: rowUnread(container) || rowUnread(a),
    };
  }

  // ---------- roster scan ----------

  let roster = null;

  function clearInput(input) {
    focusEditor(input);
    if (!document.execCommand('selectAll') || !document.execCommand('delete')) setNativeValue(input, '');
    if (input.value) setNativeValue(input, '');
  }

  function collectRows() {
    for (const a of chatRows()) {
      const id = userIdOfEl(a);
      if (!id) continue;
      const key = id.toLowerCase();
      if (!roster.map.has(key) && roster.map.size < MAX_CHATS) roster.map.set(key, parseRow(a));
      else if (roster.map.has(key)) {
        // A virtualised list re-renders rows: keep the most complete reading.
        const old = roster.map.get(key);
        const now = parseRow(a);
        if (!old.name && now.name) old.name = now.name;
        if (!old.lastMessageAt && now.lastMessageAt) old.lastMessageAt = now.lastMessageAt;
        if (!old.labels.length && now.labels.length) old.labels = now.labels;
        old.unread = old.unread || now.unread;
      }
    }
  }

  // Scrolls the chat list a few rounds (a burst) and remembers every row it saw; the caller pauses between bursts.
  async function listChats(m) {
    if (m.reset || !roster) {
      roster = { map: new Map(), rounds: 0, idle: 0, done: false, complete: false, filtered: false, note: '' };
      const search = findSearch();
      if (search && search.value) {
        clearInput(search); // a filtered list would be taken for the whole roster
        await sleep(rnd(800, 1600));
      }
      const filter = first(SELECTORS.listFilter, document, (e) => !e.contains(document.querySelector('a[href*="/chat/"]')));
      const ft = filter ? (filter.tagName === 'SELECT' ? filter.options[filter.selectedIndex]?.textContent || '' : textOf(filter)) : '';
      roster.filtered = !!ft.trim() && ft.trim().length <= 30 && !LABELS.filterAll.test(ft);
      const sc = listScroller();
      if (sc) sc.scrollTop = 0;
      await sleep(rnd(300, 700));
    }
    const burst = Math.max(1, Math.min(Number(m.rounds) || 25, MAX_ROUNDS));
    collectRows();
    for (let i = 0; i < burst && !roster.done; i++) {
      const sc = listScroller();
      if (!sc) {
        roster.done = true;
        roster.note = 'ไม่พบรายการแชทในหน้า LINE OA Manager';
        break;
      }
      const before = roster.map.size;
      scrollListBy(sc, Math.max(60, Math.round(sc.clientHeight * rnd(0.6, 0.95))));
      await sleep(rnd(200, 500));
      const grew = await waitFor(() => {
        collectRows();
        return roster.map.size > before;
      }, 1200, 200);
      roster.rounds++;
      // A round without a new row only counts once the list is at its end: before that the scroll is still on its way down
      // (a list that loads its next page near the bottom needs a few steps to get there).
      roster.idle = grew ? 0 : atBottom(sc) ? roster.idle + 1 : 0;
      if (roster.idle >= 3) {
        roster.done = true;
        roster.complete = atBottom(sc) && !roster.filtered;
        if (!atBottom(sc)) roster.note = 'รายการแชทหยุดโหลดก่อนถึงท้ายรายการ';
        else if (roster.filtered) roster.note = 'ตัวกรองของรายการแชทไม่ใช่ "ทั้งหมด" จึงยังไม่ลบแชทที่หายไป';
      } else if (roster.rounds >= MAX_ROUNDS || roster.map.size >= MAX_CHATS) {
        roster.done = true;
        roster.note = 'รายการแชทยาวเกินกำหนด อ่านได้เท่าที่เลื่อนถึง';
      }
    }
    return {
      ok: true,
      done: roster.done,
      count: roster.map.size,
      rounds: roster.rounds,
      complete: roster.done && roster.complete,
      note: roster.note,
      chats: roster.done ? [...roster.map.values()] : undefined,
    };
  }

  // ---------- open a chat ----------

  async function clickRow(m) {
    const userId = String(m.userId || '');
    if (!USER_ID_RE.test(userId)) return { ok: false, error: 'รหัสลูกค้าของแชทไม่ถูกต้อง' };
    const open = currentChat();
    if (open && sameId(open.userId, userId)) return { ok: true, already: true, found: true, unread: false };
    let a = rowFor(userId);
    if (!a) {
      // Not in view (or not rendered yet): walk the list down in small steps, the way a person looks for a name.
      const sc = listScroller();
      if (sc) {
        sc.scrollTop = 0;
        await sleep(rnd(300, 700));
        let idle = 0;
        for (let i = 0; i < 120 && !(a = rowFor(userId)) && idle < 3; i++) {
          const top = sc.scrollTop;
          scrollListBy(sc, Math.max(60, Math.round(sc.clientHeight * rnd(0.5, 0.8))));
          await sleep(rnd(250, 600));
          if (!(a = rowFor(userId)) && (sc.scrollTop === top || atBottom(sc))) {
            await sleep(rnd(800, 1500)); // the list may still be loading its next page
            idle++;
          } else idle = 0;
        }
      }
    }
    if (!a) return { ok: true, notFound: true, found: false };
    const unread = rowUnread(a);
    if (m.click === false) return { ok: true, found: true, unread };
    const chatBtn = (a.querySelector && a.querySelector('a[href*="/chat/"]')) || buttonLike(/^(?:แชท|chat)$/i, a) || a;
    await humanClick(chatBtn);
    const opened = await waitFor(() => {
      const c = currentChat();
      return (c && sameId(c.userId, userId)) || (chatState().open && (!chatState().userId || sameId(chatState().userId, userId)));
    }, 8000, 250);
    return opened ? { ok: true, found: true, unread } : { ok: false, error: 'กดเลือกแชทแล้วแต่หน้าไม่เปลี่ยนไปที่แชทของลูกค้านั้น' };
  }

  async function focusSearch() {
    const input = findSearch();
    if (!input) return { ok: false, error: 'ไม่พบช่องค้นหาของรายชื่อแชท LINE' };
    await humanClick(input);
    clearInput(input);
    typingTarget = 'search';
    return { ok: true };
  }

  async function focusComposer() {
    const c = findComposer();
    if (!c) return { ok: false, error: 'ไม่พบช่องพิมพ์ข้อความของแชท LINE' };
    typingTarget = 'composer';
    await humanClick(c);
    focusEditor(c);
    return { ok: true };
  }

  // ---------- the state of the open chat (flags, counts and one time of day: NEVER a message text) ----------

  const rgbaAlpha = (c) => {
    const m = /^rgba?\(([^)]+)\)$/.exec(String(c || '').trim());
    if (!m) return c === 'transparent' ? 0 : 1;
    const p = m[1].split(/[ ,/]+/).filter(Boolean);
    return p.length >= 4 ? Number(p[3]) : 1;
  };

  // Bubbles of the open chat as { el, side: 'in'|'out' } sorted top to bottom. Selector candidates first; else the page's
  // geometry: a rounded, filled box inside the message pane, right of the centre = outgoing (assumption A6).
  function findBubbles(pane) {
    if (!pane) return [];
    const outermost = (list) => list.filter((b) => !list.some((o) => o.el !== b.el && o.el.contains(b.el)));
    const picked = [];
    for (const el of every(SELECTORS.bubbleOut, pane)) picked.push({ el, side: 'out' });
    for (const el of every(SELECTORS.bubbleIn, pane)) picked.push({ el, side: 'in' });
    let list = outermost(picked);
    // The selectors are only trusted when they found both sides (one side alone could hide the customer's messages).
    if (!(list.some((b) => b.side === 'out') && list.some((b) => b.side === 'in'))) {
      const pr = pane.getBoundingClientRect();
      const mid = pr.left + pr.width / 2;
      const found = [];
      const all = [...pane.querySelectorAll('div, p, span, li, pre, img')].slice(-400); // the newest messages are last in the DOM
      for (const el of all) {
        if (!visible(el) || el.closest('button, a, input, textarea, [role="button"]')) continue;
        const r = el.getBoundingClientRect();
        if (r.width < 24 || r.height < 18 || r.width > pr.width * 0.92) continue;
        const cs = getComputedStyle(el);
        const radius = parseFloat(cs.borderTopLeftRadius) || 0;
        const filled = rgbaAlpha(cs.backgroundColor) > 0.1;
        const picture = el.tagName === 'IMG' && r.width >= 60 && radius >= 4;
        if (!((radius >= 6 && filled) || picture)) continue;
        // A centred pill with a date or a notice in it is not a message.
        const t = el.tagName === 'IMG' ? '' : (el.textContent || '').replace(/\s+/g, ' ').trim();
        if (t && t.length <= 60 && (isDividerText(t) || LABELS.autoReplyBar.test(t) || LABELS.overlayText.test(t))) continue;
        found.push({ el, side: r.left + r.width / 2 > mid ? 'out' : 'in' });
      }
      const geo = outermost(found);
      if (geo.length > list.length) list = geo;
    }
    return list.sort((a, b) => a.el.getBoundingClientRect().top - b.el.getBoundingClientRect().top);
  }

  // No known class for the chat header: the customer's name is the biggest text in the strip above the message pane, in the
  // same column (buttons, links and inputs there are controls, not the name).
  function guessHeaderName(pane) {
    const pr = pane.getBoundingClientRect();
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let best = '';
    let bestSize = 0;
    let bestLeft = Infinity;
    let n;
    let seen = 0;
    while ((n = walker.nextNode()) && seen++ < 5000) {
      const t = (n.nodeValue || '').replace(/\s+/g, ' ').trim();
      const p = n.parentElement;
      if (!t || t.length > 120 || !p || p.closest('button, a, input, textarea, select, [role="button"], [contenteditable="true"]') || pane.contains(p)) continue;
      const r = p.getBoundingClientRect();
      if (r.width < 2 || r.height < 2 || r.bottom > pr.top + 8 || r.top < pr.top - 200 || r.left < pr.left - 40 || r.right > pr.right + 40) continue;
      if (LABELS.autoReplyBar.test(t) || LABELS.overlayText.test(t)) continue;
      const size = parseFloat(getComputedStyle(p).fontSize) || 0;
      if (size > bestSize || (size === bestSize && r.left < bestLeft)) {
        best = t;
        bestSize = size;
        bestLeft = r.left;
      }
    }
    return best;
  }

  // A date divider of the conversation ("วันนี้", "เมื่อวาน", a weekday or a date).
  const isDividerText = (t) => LABELS.today.test(t) || LABELS.yesterday.test(t) || (timeLabelToHHmm(t) === null && timeLabelToIso(t) !== null);

  // The page's own words outside the chat list and the message pane (bars, overlays, notices, buttons), as short text blocks.
  // Used to recognise LABELS; the customers' messages are never walked.
  function pageTexts(skip) {
    const out = [];
    const seen = new Set();
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
      acceptNode: (n) => {
        const p = n.parentElement;
        if (!p || /^(SCRIPT|STYLE|NOSCRIPT|TEXTAREA)$/.test(p.tagName) || !(n.nodeValue || '').trim()) return NodeFilter.FILTER_REJECT;
        if (p.closest('[contenteditable="true"]') || p.closest('a[href*="/chat/"]') || skip.some((s) => s && s.contains(p))) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      },
    });
    let n;
    let count = 0;
    while ((n = walker.nextNode()) && count++ < 3000) {
      // The text node's own element, then up to two levels above it while the text stays short (a phrase split by <b>).
      // innerText, not textContent: a hidden overlay inside a wider element must not count as text on the page.
      let el = n.parentElement;
      for (let i = 0; i < 3 && el; i++, el = el.parentElement) {
        if (seen.has(el)) continue;
        if (!visible(el)) break;
        const text = (el.innerText || '').replace(/\s+/g, ' ').trim();
        if (text.length > 200) break;
        seen.add(el);
        if (text) out.push({ el, text });
      }
    }
    return out;
  }

  // Time of day printed next to a bubble ("11.34 น." -> "11:34"): short text nodes of the bubble's row only.
  function bubbleTime(bubble, pane) {
    let row = bubble.el;
    for (let i = 0; i < 3 && row.parentElement && row.parentElement !== pane; i++) {
      if (row.parentElement.getBoundingClientRect().height > pane.getBoundingClientRect().height * 0.6) break;
      row = row.parentElement;
    }
    const w = document.createTreeWalker(row, NodeFilter.SHOW_TEXT);
    let n;
    let seen = 0;
    while ((n = w.nextNode()) && seen++ < 60) {
      const t = (n.nodeValue || '').replace(/\s+/g, ' ').trim();
      if (t && t.length <= 14) {
        const hhmm = timeLabelToHHmm(t);
        if (hhmm) return hhmm;
      }
    }
    return null;
  }

  // Is the newest date divider above the last bubble "today"? true / false / null (no divider found).
  function lastBubbleIsToday(bubble, pane) {
    const top = bubble.el.getBoundingClientRect().top + 4;
    let best = null;
    let bestBottom = -Infinity;
    const consider = (el, text) => {
      const r = el.getBoundingClientRect();
      if (r.bottom <= top && r.bottom > bestBottom) {
        best = text;
        bestBottom = r.bottom;
      }
    };
    for (const el of every(SELECTORS.dateDivider, pane)) {
      const t = textOf(el);
      if (t && t.length <= 40) consider(el, t);
    }
    if (best === null) {
      const w = document.createTreeWalker(pane, NodeFilter.SHOW_TEXT);
      let n;
      let seen = 0;
      while ((n = w.nextNode()) && seen++ < 400) {
        const t = (n.nodeValue || '').replace(/\s+/g, ' ').trim();
        if (!t || t.length > 40 || !n.parentElement) continue;
        if (LABELS.today.test(t) || LABELS.yesterday.test(t) || (timeLabelToHHmm(t) === null && timeLabelToIso(t) !== null)) consider(n.parentElement, t);
      }
    }
    if (best === null) return null;
    return LABELS.today.test(best);
  }

  function hasUploads(area) {
    return !!area && every(SELECTORS.uploading, area).length > 0;
  }

  function findSendButton(area) {
    const scopes = [area, document].filter(Boolean);
    for (const root of scopes) {
      const b = first(SELECTORS.sendButton, root, (el) => !el.closest('a[href*="/chat/"]'));
      if (b) return b;
    }
    if (area) {
      const allBtns = [...area.querySelectorAll('button, [role="button"]')].filter(visible);
      const candidates = allBtns.filter((b) => {
        const l = labelOf(b);
        return !/แนบ|attach|file|ไฟล์|รูป|image|photo|emoji|สติกเกอร์|sticker/i.test(l);
      });
      if (candidates.length) return candidates[candidates.length - 1];
    }
    return null;
  }

  const looksDisabled = (el) => isDisabled(el) || /(^|[\s_-])disabled([\s_-]|$)/i.test(el.className || '');

  // After a file is chosen LINE asks "ต้องการส่งไฟล์ในห้องแชทนี้หรือไม่": a modal OUTSIDE the composer area with a canvas
  // preview and ส่ง / ยกเลิก (checked on the real page 2026-10-27). The picture goes out only when that ส่ง is pressed.
  function uploadDialog() {
    return [...document.querySelectorAll('.modal-dialog')].find((d) => visible(d) && d.querySelector('.modal-footer .btn-primary')) || null;
  }

  const uploadPreviewReady = (d) => !!d && [...d.querySelectorAll('canvas, img')].some((e) => visible(e));

  function chatState() {
    const chat = currentChat();
    const composer = findComposer();
    const area = composerArea(composer);
    const pane = messagePane(composer);
    const bubbles = findBubbles(pane);
    const last = bubbles.length ? bubbles[bubbles.length - 1] : null;

    // Header: the customer's name as LINE shows it (the one personal datum returned), selector first, else the first
    // heading-like node in the top strip of the conversation column.
    let headerName = '';
    // Not the top bar (the signed-in staff member's name sits there as span.user-name: the first real run read it as the
    // customer and the identity check refused the send), and, once the message pane is known, only inside its column.
    const paneRect = pane ? pane.getBoundingClientRect() : null;
    const h = first(SELECTORS.headerName, document, (e) => {
      if (e.closest('a[href*="/chat/"], nav, [class*="user-name" i], [class*="navbar" i], [class*="gnb" i]')) return false;
      if (!paneRect) return true;
      const r = e.getBoundingClientRect();
      return r.left >= paneRect.left - 40 && r.right <= paneRect.right + 40;
    });
    if (h) headerName = firstTextNode(h);
    if (!headerName && pane) headerName = guessHeaderName(pane);

    // The page's own words (never the chat list or the messages): the auto-reply bar, the overlay, the manual button, notices.
    const texts = pageTexts([pane]).filter((b) => visible(b.el));
    const manual = findManualButton(texts);
    const overlayText = texts.some((b) => LABELS.overlayText.test(b.text));
    const barText = texts.some((b) => LABELS.autoReplyBar.test(b.text));

    let covered = false;
    if (composer) {
      const r = composer.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      const shell = shellOf(composer);
      covered = !!hit && !shell.contains(hit) && !hit.contains(shell);
    }
    const overlayEl = area ? first(SELECTORS.composerOverlay, area, (e) => e !== composer && !shellOf(composer).contains(e)) : null;
    const overlay = overlayText || covered || (!!overlayEl && /overlay|cover|mask/i.test(overlayEl.className || ''));

    // A notice where the composer should be ("this customer cannot be written to"): in the composer's area, else in the
    // lower part of the screen when the composer is missing altogether.
    const cannotSend = texts.some((b) => {
      if (!LABELS.cannotSend.test(b.text)) return false;
      if (composer && shellOf(composer).contains(b.el)) return false;
      return area ? area.contains(b.el) : b.el.getBoundingClientRect().top > window.innerHeight * 0.55;
    });

    const disabled = !!composer && (composer.disabled === true || composer.readOnly === true || composer.getAttribute('aria-disabled') === 'true');
    const send = findSendButton(area);
    const rowForOpen = chat ? rowFor(chat.userId) : null;
    const text = composer ? valueOf(composer) : '';
    const failedCount = pane ? every(SELECTORS.sendFailed, pane).length : 0; // old failed messages count too: compare before/after

    return {
      ok: true,
      open: !!chat && (!!composer || !!pane || !!headerName),
      userId: chat ? chat.userId : '',
      headerName: headerName.slice(0, 120),
      composerFound: !!composer,
      composerReady: !!composer && !disabled && !overlay,
      composerEmpty: !composer || text.trim().length === 0,
      composerDisabled: disabled,
      overlay,
      manualButton: !!manual,
      autoReplyBar: barText,
      cannotSend,
      bubbleCount: bubbles.length,
      lastBubbleSide: last ? last.side : null,
      lastBubbleTime: last && pane ? bubbleTime(last, pane) : null,
      lastBubbleToday: last && pane ? lastBubbleIsToday(last, pane) : null,
      unreadInList: rowForOpen ? rowUnread(rowForOpen) : false,
      outgoingCount: bubbles.filter((b) => b.side === 'out').length,
      incomingCount: bubbles.filter((b) => b.side === 'in').length,
      sendEnabled: !!send && !looksDisabled(send),
      uploading: hasUploads(area) || (!!uploadDialog() && !uploadPreviewReady(uploadDialog())),
      uploadDialog: !!uploadDialog(),
      sendFailed: failedCount > 0,
      failedCount,
      blocked: blockCheck({ composer, pane }).blocked,
    };
  }

  // ---------- manual chat, send ----------

  // The "แชทแบบแมนนวล" button: a text block that says exactly that, preferring a clickable element around it.
  function findManualButton(texts) {
    const hits = texts.filter((b) => LABELS.manualButton.test(b.text));
    for (const b of hits) {
      const clickable = b.el.closest('button, [role="button"], a, [tabindex]');
      if (clickable && visible(clickable)) return clickable;
    }
    if (hits.length) return hits[0].el;
    return buttonLike(LABELS.manualButton);
  }

  async function manualSwitch() {
    const btn = findManualButton(pageTexts([messagePane(findComposer())]).filter((b) => visible(b.el)));
    if (!btn) return { ok: true, clicked: false };
    await humanClick(btn);
    // Assumption A4: a confirm dialog may follow. Only an exact confirm label is ever clicked.
    await sleep(rnd(600, 1200));
    const confirm = await waitFor(() => {
      for (const d of every(SELECTORS.dialog)) {
        const b = buttonLike(LABELS.confirm, d);
        if (b) return b;
      }
      return null;
    }, 2500, 250);
    if (confirm) await humanClick(confirm);
    const ready = await waitFor(() => {
      const s = chatState();
      return s.composerFound && s.composerReady && !s.overlay;
    }, 8000, 300);
    return ready ? { ok: true, clicked: true } : { ok: false, error: 'สลับเป็นแชทแบบแมนนวลไม่สำเร็จ (ช่องพิมพ์ยังถูกปิดอยู่)' };
  }

  async function send(m) {
    const composer = findComposer();
    if (!composer) return { ok: false, error: 'ไม่พบช่องพิมพ์ข้อความของแชท LINE' };
    // Defence in depth (D11a): the chat that is open right now must still be the one the job is for.
    const open = currentChat();
    if (m.expectUserId && !(open && sameId(open.userId, m.expectUserId))) {
      return { ok: false, error: 'แชทที่เปิดไม่ตรงกับลูกค้าในงาน ยกเลิกการส่ง' };
    }
    // A picture waits in LINE's confirmation modal: its ส่ง sends it (the composer's own ส่ง would send nothing).
    const dlg = uploadDialog();
    if (dlg) {
      if (!(await waitFor(() => uploadPreviewReady(uploadDialog()), 15000, 250))) return { ok: false, error: 'หน้าต่างยืนยันส่งรูปยังไม่แสดงรูปตัวอย่าง' };
      const confirmBtn = (uploadDialog() || dlg).querySelector('.modal-footer .btn-primary');
      await humanClick(confirmBtn);
      return (await waitFor(() => !uploadDialog(), 30000, 300)) ? { ok: true, dialog: true } : { ok: false, error: 'กดส่งรูปแล้วแต่หน้าต่างยืนยันยังไม่ปิด' };
    }
    const area = composerArea(composer);
    const btn = findSendButton(area);
    if (!btn) return { ok: false, error: 'ไม่พบปุ่ม "ส่ง" ของแชท LINE' };
    // The button enables a moment after the text (or the picture) is in.
    const enabled = await waitFor(() => !looksDisabled(btn), 6000, 250);
    if (!enabled) return { ok: false, error: 'ปุ่ม "ส่ง" ยังกดไม่ได้ (ไม่มีข้อความหรือรูปให้ส่ง)' };
    await humanClick(btn); // never the Enter key
    return { ok: true };
  }

  // Clears what this script typed: a failed job must not leave a half-written message for the shop's staff to send, nor the
  // customer's name in the list's search box (which would filter the next job's list). `composer` / `search` say which.
  async function discard(m) {
    const o = m || {};
    typingTarget = 'composer';
    if (o.dialog || o.composer) {
      // An unsent picture waits in LINE's confirmation modal: cancel it (never send it from a cleanup).
      const dlg = uploadDialog();
      const cancel = dlg && dlg.querySelector('.modal-footer .btn-secondary');
      if (cancel) await humanClick(cancel);
    }
    if (o.search) {
      const s = findSearch();
      if (s && s.value) clearInput(s);
    }
    if (o.composer) {
      const c = findComposer();
      if (c && valueOf(c).trim()) await setText('');
    }
    return { ok: true };
  }

  // ---------- editing commands ----------

  function insertText(text) {
    const ed = requireEditor();
    focusEditor(ed);
    if (isTextField(ed)) {
      if (!document.execCommand('insertText', false, text)) {
        const s = ed.selectionStart ?? ed.value.length;
        const e = ed.selectionEnd ?? s;
        setNativeValue(ed, ed.value.slice(0, s) + text + ed.value.slice(e));
        ed.setSelectionRange(s + text.length, s + text.length);
      }
    } else if (!document.execCommand('insertText', false, text)) pasteText(ed, text);
    return { ok: true };
  }

  // A new line inside the message (assumption A3: Shift+Enter; Enter would send). Never presses plain Enter.
  function newline() {
    const ed = requireEditor();
    focusEditor(ed);
    if (isTextField(ed)) {
      if (!document.execCommand('insertText', false, '\n')) {
        const s = ed.selectionStart ?? ed.value.length;
        setNativeValue(ed, ed.value.slice(0, s) + '\n' + ed.value.slice(ed.selectionEnd ?? s));
        ed.setSelectionRange(s + 1, s + 1);
      }
      return { ok: true };
    }
    const handled = pressKey(ed, 'Enter', 'Enter', 13, { shiftKey: true });
    if (!handled && !document.execCommand('insertLineBreak')) document.execCommand('insertParagraph');
    return { ok: true };
  }

  function backspace() {
    const ed = requireEditor();
    focusEditor(ed);
    const handled = pressKey(ed, 'Backspace', 'Backspace', 8);
    if (!handled) document.execCommand('delete');
    return { ok: true };
  }

  function pasteAtCaret(text) {
    const ed = requireEditor();
    focusEditor(ed);
    // A synthetic paste event does nothing in a textarea; insertText is what lands the text there.
    if (isTextField(ed)) return insertText(text);
    if (!pasteText(ed, text)) document.execCommand('insertText', false, text);
    return { ok: true };
  }

  function getText() {
    const ed = editor();
    if (!ed) return { ok: false, open: false, text: '' };
    return { ok: true, open: true, text: valueOf(ed) }; // what was typed into the box: the job's own text
  }

  async function setText(text) {
    const ed = requireEditor();
    focusEditor(ed);
    if (isTextField(ed)) {
      ed.select();
      const done = text === '' ? document.execCommand('delete') : document.execCommand('insertText', false, text);
      if (!done || ed.value !== text) setNativeValue(ed, text);
    } else {
      if (!pressKey(ed, 'a', 'KeyA', 65, { ctrlKey: true })) document.execCommand('selectAll');
      await sleep(150);
      if (text === '') document.execCommand('delete');
      else if (!pasteText(ed, text)) document.execCommand('insertText', false, text);
    }
    return { ok: true };
  }

  // ---------- images ----------

  function dataUrlToFile(rec, i) {
    const [head, b64] = rec.data.split(',');
    const mime = rec.type || (head.match(/data:([^;]+)/) || [])[1] || 'image/jpeg';
    const bin = atob(b64);
    const arr = new Uint8Array(bin.length);
    for (let k = 0; k < bin.length; k++) arr[k] = bin.charCodeAt(k);
    const ext = (mime.split('/')[1] || 'jpg').replace('jpeg', 'jpg').replace(/[^a-z0-9]/gi, '');
    // LINE tells a picture from a file by the extension too: a library file called "test" (no extension) showed up as a
    // FILE in its confirmation modal and was refused (seen on the real page 2026-10-27), so the name always carries one.
    const base = String(rec.name || `image_${i + 1}`).replace(/[\\/]/g, '_');
    const name = /\.(jpe?g|png|gif|webp|bmp)$/i.test(base) ? base : `${base}.${ext}`;
    return new File([arr], name, {
      type: mime,
      lastModified: Date.now() - Math.floor(Math.random() * 86400000),
    });
  }

  // Posts from the web app pass full keys (cloudimg:<id>).
  async function loadFiles(ids) {
    const keys = ids.map((id) => (String(id).includes(':') ? String(id) : 'img:' + id));
    const data = await chrome.storage.local.get(keys);
    return keys.map((k) => data[k]).filter(Boolean).map(dataUrlToFile);
  }

  function mediaCount(root) {
    let n = 0;
    for (const img of root.querySelectorAll('img')) {
      const r = img.getBoundingClientRect();
      if ((img.src || '').startsWith('blob:') || (r.width >= 70 && r.height >= 70)) n++;
    }
    return n + root.querySelectorAll('video').length;
  }

  function snapshot(root) {
    return { media: mediaCount(root), nodes: root.querySelectorAll('*').length };
  }

  function grew(root, before) {
    const now = snapshot(root);
    return (
      now.media > before.media ||
      now.nodes - before.nodes > 15 ||
      !!root.querySelector('[role="progressbar"]')
    );
  }

  function findFileInput(root) {
    const inputs = [...root.querySelectorAll('input[type="file"]')];
    return (
      inputs.find((i) => /image/.test(i.accept || '')) ||
      inputs.find((i) => !i.accept) ||
      inputs[0] ||
      null
    );
  }

  async function attachImages(ids) {
    const c = findComposer();
    if (!c) throw new Error('ไม่พบช่องพิมพ์ข้อความของแชท LINE');
    const area = composerArea(c) || document.body;
    const files = await loadFiles(ids || []);
    if (!files.length) return { ok: false, error: 'ไม่พบไฟล์รูปในที่เก็บข้อมูล' };
    const makeDT = () => {
      const dt = new DataTransfer();
      files.forEach((f) => dt.items.add(f));
      return dt;
    };
    const before = snapshot(area);
    const attached = () => waitFor(() => grew(area, before), 20000, 500);

    // 1) a hidden file input (near the box, else the only one on the page); click the attach icon first if there is none.
    // The icon is looked for next to the box only: a wide "photo"-like label elsewhere (a menu item) would leave the chat.
    const findInput = () => findFileInput(area) || (document.querySelectorAll('input[type="file"]').length === 1 ? findFileInput(document) : null);
    let input = findInput();
    if (!input) {
      const attachBtn = first(SELECTORS.attachButton, area) || findButtonByPattern(area, PHOTO_PATTERNS);
      if (attachBtn) {
        await humanClick(attachBtn);
        input = await waitFor(findInput, 6000, 300);
      }
    }
    if (input) {
      input.files = makeDT().files;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
      // The confirmation modal (see uploadDialog) appears outside `area`: its ส่ง is pressed by `send`.
      const dlg = await waitFor(uploadDialog, 8000, 250);
      if (dlg) {
        // A picture shows a canvas preview; a FILE shows an icon and cannot be sent as a picture: cancel it, never send it.
        if (!(await waitFor(() => uploadPreviewReady(uploadDialog()), 8000, 250))) {
          const cancel = (uploadDialog() || dlg).querySelector('.modal-footer .btn-secondary');
          if (cancel) await humanClick(cancel);
          return { ok: false, error: 'LINE ไม่ถือว่าไฟล์นี้เป็นรูปภาพ (ขึ้นเป็นไฟล์) จึงไม่ส่ง' };
        }
        return { ok: true, method: 'input', dialog: true };
      }
      if (await attached()) return { ok: true, method: 'input' };
    }

    // 2) paste the images into the box
    focusEditor(c);
    c.dispatchEvent(new ClipboardEvent('paste', { clipboardData: makeDT(), bubbles: true, cancelable: true }));
    if (await attached()) return { ok: true, method: 'paste' };

    // 3) drag & drop onto the conversation
    const dt = makeDT();
    const target = messagePane(c) || c;
    const r = target.getBoundingClientRect();
    const at = { bubbles: true, cancelable: true, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 };
    for (const type of ['dragenter', 'dragover', 'drop']) {
      target.dispatchEvent(new DragEvent(type, { ...at, dataTransfer: dt }));
      await sleep(rnd(80, 200));
    }
    if (await attached()) return { ok: true, method: 'drop' };

    return { ok: false, error: 'แนบรูปไม่สำเร็จ (ไม่พบช่องอัปโหลดรูปของแชท LINE)' };
  }

  // ---------- scrolling (the message pane or the chat list, never the window while those exist) ----------

  async function scrollEl(el, dy) {
    const steps = document.hidden ? 1 : 6 + Math.floor(Math.random() * 8);
    for (let i = 0; i < steps; i++) {
      if (el === document.scrollingElement || el === document.documentElement) window.scrollBy(0, dy / steps);
      else el.scrollTop += dy / steps;
      if (steps > 1) await sleep(rnd(18, 55));
    }
    return { ok: true, y: el.scrollTop };
  }

  function scrollTarget(where) {
    if (where === 'list') return listScroller() || document.scrollingElement;
    return messagePane(findComposer()) || document.scrollingElement || document.documentElement;
  }

  async function scrollBy(m) {
    return scrollEl(scrollTarget(m.where), Number(m.dy) || 0);
  }

  async function scrollToTop(m) {
    const el = scrollTarget(m && m.where);
    if (el.scrollTop > 0) await scrollEl(el, -el.scrollTop);
    el.scrollTop = 0;
    return { ok: true };
  }

  async function scrollToBottom(m) {
    const el = scrollTarget(m && m.where);
    const gap = el.scrollHeight - el.clientHeight - el.scrollTop;
    if (gap > 0) await scrollEl(el, gap);
    el.scrollTop = el.scrollHeight;
    return { ok: true };
  }

  // ---------- page checks ----------

  function loginForm() {
    return !!document.querySelector('input[type="password"], form[action*="login" i] input[type="text"], form[action*="login" i] input[type="email"]');
  }

  function pageCheck() {
    const host = location.hostname;
    const login = LABELS.loginHosts.some((h) => host === h || host.endsWith('.' + h)) || LABELS.loginPath.test(location.pathname) || loginForm();
    const chat = currentChat();
    let oaName = '';
    const el = first(SELECTORS.oaName, document, (e) => !e.closest('a[href*="/chat/"]'));
    if (el) oaName = firstTextNode(el).slice(0, 120);
    return {
      ok: true,
      title: String(document.title || '').slice(0, 80),
      login,
      checkpoint: false,
      canSwitch: false,
      botId: currentBotId(),
      oaName,
      chatOpen: !!chat,
      // The chat list or the message box is on the page: the app has finished loading.
      ready: chatRows().length > 0 || !!findComposer(),
      // OAs offered on the chooser (chat.line.biz/ with no OA picked): the page is loaded then, though it has no chat list.
      accounts: currentBotId() ? 0 : listAccounts().length,
    };
  }

  // The OAs offered on chat.line.biz/ when the browser has not picked one: { botId, name }.
  function listAccounts() {
    const out = [];
    const seen = new Set();
    for (const a of document.querySelectorAll('a[href]')) {
      if (!visible(a)) continue;
      let u;
      try {
        u = new URL(a.getAttribute('href'), location.href);
      } catch {
        continue;
      }
      if (u.hostname !== 'chat.line.biz') continue;
      const seg = u.pathname.split('/')[1] || '';
      if (!BOT_ID_RE.test(seg) || RESERVED_SEGMENTS.has(seg.toLowerCase()) || seen.has(seg.toLowerCase())) continue;
      seen.add(seg.toLowerCase());
      out.push({ botId: seg, name: firstTextNode(a).slice(0, 120), strong: OA_HEX_RE.test(seg) });
    }
    const strong = out.filter((o) => o.strong);
    return (strong.length ? strong : out).slice(0, 20).map(({ botId, name }) => ({ botId, name }));
  }

  // ---------- LINE warnings ----------

  // Text of dialogs/alerts only (never the chat list, never what we typed).
  function noticeText(root) {
    const parts = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let n;
    let size = 0;
    while ((n = walker.nextNode()) && size < 2000) {
      if (n.parentElement && n.parentElement.closest('[contenteditable="true"], textarea')) continue;
      const t = n.nodeValue.trim();
      if (t) {
        parts.push(t);
        size += t.length;
      }
    }
    return parts.join(' ');
  }

  function blockCheck(ctx = null) {
    const composer = ctx ? ctx.composer : findComposer();
    const pane = ctx ? ctx.pane : messagePane(composer);
    const inChat = (el) => (pane && pane.contains(el)) || (composer && shellOf(composer).contains(el)) || !!el.querySelector('a[href*="/chat/"]') || !!el.closest('a[href*="/chat/"]');
    const roots = every(SELECTORS.noticeRoots).filter((r) => !inChat(r));
    for (const r of roots) {
      const text = noticeText(r);
      const hit = BLOCK_PATTERNS.map((re) => re.exec(text)).find(Boolean);
      if (hit) {
        const at = Math.max(0, hit.index - 40);
        return { ok: true, blocked: true, text: text.slice(at, at + 160) };
      }
    }
    if (first(['iframe[src*="captcha" i]', 'iframe[src*="recaptcha" i]'])) return { ok: true, blocked: true, text: 'captcha' };
    // A bare error page instead of the app (nothing of the chat UI on it).
    if (!composer && !chatRows().length && document.body) {
      const body = noticeText(document.body).slice(0, 600);
      const hit = body.length < 500 && BLOCK_PATTERNS.map((re) => re.exec(body)).find(Boolean);
      if (hit) return { ok: true, blocked: true, text: body.slice(0, 160) };
    }
    return { ok: true, blocked: false };
  }

  // ---------- commands ----------

  const handlers = {
    ping: async () => ({ ok: true }),
    check: async () => pageCheck(),
    lineListAccounts: async () => ({ ok: true, accounts: listAccounts() }),
    lineListChats: async (m) => listChats(m || {}),
    lineClickRow: async (m) => clickRow(m || {}),
    lineFocusSearch: async () => focusSearch(),
    lineChatState: async () => chatState(),
    lineManualSwitch: async () => manualSwitch(),
    lineFocusComposer: async () => focusComposer(),
    lineSend: async (m) => send(m || {}),
    lineParseTime: async (m) => ({ ok: true, iso: timeLabelToIso(m.label, m.now ? new Date(m.now) : new Date()), hhmm: timeLabelToHHmm(m.label) }),
    insert: async (m) => insertText(String(m.text || '')),
    newline: async () => newline(),
    backspace: async () => backspace(),
    getText: async () => getText(),
    setText: async (m) => setText(String(m.text || '')),
    paste: async (m) => pasteAtCaret(String(m.text || '')),
    attachImages: async (m) => attachImages(m.imageIds),
    scroll: async (m) => scrollBy(m),
    scrollTop: async (m) => scrollToTop(m),
    scrollBottom: async (m) => scrollToBottom(m),
    discard: async (m) => discard(m),
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
