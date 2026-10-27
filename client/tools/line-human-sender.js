// =====================================================================================================
// LINE OA Human Auto-Sender (สคริปต์ส่งข้อความและรูปหาลูกค้า LINE OA ทีละแชทแบบมนุษย์)
//
// คุณสมบัติเด่น:
// - ส่งทีละแชท เลียนแบบพฤติกรรมมนุษย์ (สุ่มจังหวะพิมพ์, พิมพ์ทีละตัว, มีหยุดคิด, แก้คำผิด)
// - ไม่ส่งซ้ำลูกค้ารายเดิมสำหรับโพสต์เดิม (เว้นแต่จะเป็นโพสต์ใหม่ หรือข้อความ/รูปเปลี่ยนไป)
// - รองรับ Spintax สุ่มคำ เช่น {สวัสดีครับ|สวัสดีค่ะ|ยินดีต้อนรับ}
// - รองรับแท็ก {{name}} หรือ {{ชื่อ}} ดึงชื่อลูกค้ามาใส่ในข้อความอัตโนมัติ
// - แนบรูปภาพได้หลายรูป พร้อมสลับส่งข้อความก่อน/รูปก่อนได้
// - สลับ "แชทแบบแมนนวล" ให้อัตโนมัติเมื่อพบว่าเปิดบอทตอบกลับอัตโนมัติอยู่
// - ข้ามลูกค้าที่กำลังคุยอยู่ล่าสุด (ภายใน 30 นาที) เพื่อไม่ไปขัดจังหวะการสนทนา
// - หน้าต่างลอย (Floating UI) บนหน้า LINE OA Manager ใช้งานง่าย กด ย่อ/ขยาย หรือปิดได้
// =====================================================================================================

(() => {
  if (window.__lineHumanSenderActive) {
    const existing = document.getElementById('line-human-sender-panel');
    if (existing) {
      existing.style.display = 'block';
      alert('เครื่องมือ LINE Human Sender เปิดใช้งานอยู่แล้ว');
      return;
    }
  }
  window.__lineHumanSenderActive = true;

  // ---------- Utilities & Human Simulation ----------

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const rand = (min, max) => Math.floor(Math.random() * (max - min + 1)) + min;
  const randFloat = (min, max) => min + Math.random() * (max - min);

  // Spintax parser: {สวัสดี|สวัสดีครับ|ยินดีต้อนรับ} -> สุ่มเลือก 1 คำ
  function parseSpintax(text) {
    if (!text) return '';
    let result = text;
    const regex = /\{([^{}]+)\}/g;
    while (regex.test(result)) {
      result = result.replace(regex, (_, choices) => {
        const arr = choices.split('|');
        return arr[Math.floor(Math.random() * arr.length)];
      });
    }
    return result;
  }

  // แทนที่ {{name}} หรือ {{ชื่อ}} ด้วยชื่อลูกค้า
  function formatMessage(template, customerName) {
    const spin = parseSpintax(template);
    const cleanName = (customerName || '').trim() || 'คุณลูกค้า';
    return spin
      .replace(/\{\{\s*(?:name|ชื่อ)\s*\}\}/gi, cleanName)
      .replace(/\[\[\s*(?:name|ชื่อ)\s*\]\]/gi, cleanName);
  }

  function visible(el) {
    if (!el || !el.isConnected) return false;
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none' && cs.opacity !== '0';
  }

  async function humanClick(el) {
    if (!el) return;
    el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
    await sleep(rand(200, 450));
    const r = el.getBoundingClientRect();
    const x = r.left + r.width * randFloat(0.3, 0.7);
    const y = r.top + r.height * randFloat(0.3, 0.7);
    const hit = document.elementFromPoint(x, y);
    const target = hit && el.contains(hit) ? hit : el;

    const base = { bubbles: true, cancelable: true, composed: true, clientX: x, clientY: y, button: 0 };
    target.dispatchEvent(new MouseEvent('mouseover', base));
    target.dispatchEvent(new MouseEvent('mousemove', base));
    await sleep(rand(80, 200));
    target.dispatchEvent(new MouseEvent('mousedown', { ...base, buttons: 1 }));
    await sleep(rand(60, 160));
    target.dispatchEvent(new MouseEvent('mouseup', base));
    target.dispatchEvent(new MouseEvent('click', base));
  }

  // ---------- Sent History & Duplicate Prevention (ระบบป้องกันการส่งซ้ำลูกค้ารายเดิม) ----------

  const HISTORY_STORAGE_KEY = 'LINE_HUMAN_SENDER_SENT_HISTORY';

  function getHistory() {
    try {
      const raw = localStorage.getItem(HISTORY_STORAGE_KEY);
      return raw ? JSON.parse(raw) : {};
    } catch {
      return {};
    }
  }

  function saveHistory(data) {
    try {
      localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(data));
    } catch (e) {
      console.warn('[LINE Sender] Failed to save history to localStorage', e);
    }
  }

  function hashString(str) {
    let hash = 0;
    for (let i = 0; i < str.length; i++) {
      hash = ((hash << 5) - hash) + str.charCodeAt(i);
      hash |= 0;
    }
    return 'post_' + Math.abs(hash).toString(36);
  }

  // Fingerprint ของโพสต์: ถ้าข้อความหรือรูปเปลี่ยนไป จะถือว่าเป็น "โพสต์ใหม่" ทันที
  function getPostFingerprint(text, files) {
    const cleanText = (text || '').trim();
    const fileSigs = (files || []).map((f) => `${f.name}:${f.size}`).sort().join('|');
    return hashString(cleanText + '###' + fileSigs);
  }

  // ตรวจสอบว่าเคยส่งโพสต์นี้ให้ลูกค้ารายนี้แล้วหรือยัง
  function hasAlreadyReceivedPost(postHash, customerId, customerName) {
    const history = getHistory();
    const postRecord = history[postHash];
    if (!postRecord || !postRecord.recipients) return false;

    // เช็คจาก User ID (ถ้ามี)
    if (customerId && postRecord.recipients[customerId.toLowerCase()]) return true;

    // เช็คจากชื่อลูกค้า
    if (customerName) {
      const cleanName = customerName.trim().toLowerCase();
      for (const [key, rec] of Object.entries(postRecord.recipients)) {
        if (key === cleanName || (rec && rec.name && rec.name.trim().toLowerCase() === cleanName)) {
          return true;
        }
      }
    }
    return false;
  }

  // บันทึกว่าลูกค้ารายนี้ได้รับโพสต์นี้แล้ว
  function markPostSent(postHash, customerId, customerName, textPreview, filesCount) {
    const history = getHistory();
    if (!history[postHash]) {
      history[postHash] = {
        preview: (textPreview || '').trim().slice(0, 80),
        filesCount: filesCount || 0,
        createdAt: new Date().toISOString(),
        recipients: {}
      };
    }
    const key = (customerId || customerName || 'unknown').toLowerCase();
    history[postHash].recipients[key] = {
      name: customerName,
      sentAt: new Date().toISOString()
    };
    saveHistory(history);
  }

  function getPostRecipientsCount(postHash) {
    const history = getHistory();
    const record = history[postHash];
    return record && record.recipients ? Object.keys(record.recipients).length : 0;
  }

  function clearHistoryForPost(postHash) {
    const history = getHistory();
    if (history[postHash]) {
      delete history[postHash];
      saveHistory(history);
    }
  }

  // ---------- DOM Selectors for LINE OA ----------

  const SELECTORS = {
    chatRows: [
      'table tbody tr',
      '[role="row"]',
      'a[href*="/chat/"]',
      '[class*="chat-item" i]',
      '[class*="contact-item" i]',
      '[class*="chatlist" i] a'
    ],
    search: ['input[type="search"]', 'input[placeholder*="ค้นหา"]', 'input[placeholder*="search" i]', 'input[type="text"]'],
    composer: [
      'textarea',
      '[contenteditable="true"][role="textbox"]',
      '[contenteditable="true"]',
      'div[role="textbox"]',
      '[class*="composer" i] [contenteditable]',
      '[class*="editor" i]'
    ],
    sendButton: [
      'button[class*="send" i]',
      '[class*="send" i] button',
      'button[type="submit"]',
      'button[aria-label*="ส่ง" i]',
      'button[title*="ส่ง" i]',
      'button[aria-label*="send" i]',
      'button[title*="send" i]',
      'button.btn-primary',
      'button[data-testid*="send" i]'
    ],
    attachButton: [
      'button[class*="attach" i]',
      'button[class*="file" i]',
      'button[class*="clip" i]',
      'button[class*="image" i]',
      '[class*="attach" i]',
      '[class*="clip" i]',
      '[aria-label*="แนบ" i]',
      '[title*="แนบ" i]',
      '[aria-label*="รูป" i]',
      '[title*="รูป" i]'
    ],
    headerName: [
      '[class*="chat-header" i] [class*="name" i]',
      '[class*="chatheader" i] [class*="name" i]',
      'header h1', 'header h2', 'header h3',
      '[class*="header" i] [class*="name" i]',
      '[class*="header" i] [class*="title" i]',
      '[class*="chat-title" i]',
      '[class*="room-name" i]'
    ],
    messagePane: ['[class*="messages" i]', '[class*="message-list" i]', '[class*="chat-body" i]', '[class*="chatroom" i]'],
    manualButton: ['button', '[role="button"]', 'a']
  };

  function findElement(selectorList, root = document) {
    for (const sel of selectorList) {
      try {
        const found = root.querySelector(sel);
        if (found && visible(found)) return found;
      } catch {
        /* ignore invalid selector */
      }
    }
    return null;
  }

  function findComposer() {
    return findElement(SELECTORS.composer);
  }

  function findSendButton() {
    const c = findComposer();
    const area = c ? (c.closest('div[class*="footer" i], [class*="composer" i], form, div') || document) : document;
    const btn = findElement(SELECTORS.sendButton, area) || findElement(SELECTORS.sendButton, document);
    if (btn) return btn;
    for (const b of area.querySelectorAll('button, [role="button"]')) {
      const txt = (b.textContent || b.getAttribute('aria-label') || b.title || '').trim();
      if (/^\s*(?:ส่ง|send)\s*$/i.test(txt) && visible(b)) return b;
    }
    if (c) {
      const p = c.parentElement?.parentElement;
      if (p) {
        const btns = [...p.querySelectorAll('button')].filter(visible);
        if (btns.length) return btns[btns.length - 1];
      }
    }
    return null;
  }

  function findManualChatButton() {
    for (const el of document.querySelectorAll('button, [role="button"], a, span')) {
      const t = (el.textContent || el.getAttribute('aria-label') || '').trim();
      if (/แชทแบบแมนนวล|แชทแมนนวล|manual chat/i.test(t) && visible(el)) {
        return el.closest('button, [role="button"], a') || el;
      }
    }
    return null;
  }

  function getHeaderCustomerName() {
    const el = findElement(SELECTORS.headerName);
    if (el) {
      const t = (el.textContent || '').trim();
      if (t && t.length < 80) return t;
    }
    return '';
  }

  function hasAutoReplyOverlay() {
    for (const el of document.querySelectorAll('div, p, span, section')) {
      const t = (el.textContent || '').trim();
      if (/อยู่ในเวลาตอบข้อความ|โปรดคลิก\s*[""“]?แชทแบบแมนนวล|กำลังใช้ข้อความตอบกลับอัตโนมัติ/i.test(t) && visible(el)) {
        return true;
      }
    }
    return false;
  }

  // ---------- Human Typing Simulation (พิมพ์แบบมนุษย์) ----------

  async function typeHumanLike(editor, text, paceMs = 60, shouldAbort = () => false) {
    if (!editor) return false;
    editor.focus();

    const isInput = editor.tagName === 'TEXTAREA' || editor.tagName === 'INPUT';
    const chars = Array.from(text);

    for (let i = 0; i < chars.length; i++) {
      if (shouldAbort()) return false;
      const ch = chars[i];

      // จำลองพิมพ์ผิดแล้วลบแก้ไข (Typo Simulation 3% chance)
      if (Math.random() < 0.03 && i > 0 && i < chars.length - 1) {
        const typoChar = String.fromCharCode(ch.charCodeAt(0) + (Math.random() < 0.5 ? 1 : -1));
        if (isInput) {
          editor.value += typoChar;
          editor.dispatchEvent(new Event('input', { bubbles: true }));
        } else {
          document.execCommand('insertText', false, typoChar);
        }
        await sleep(rand(120, 300));
        if (isInput) {
          editor.value = editor.value.slice(0, -1);
          editor.dispatchEvent(new Event('input', { bubbles: true }));
        } else {
          document.execCommand('delete', false);
        }
        await sleep(rand(100, 250));
      }

      if (ch === '\n') {
        if (isInput) {
          editor.value += '\n';
          editor.dispatchEvent(new Event('input', { bubbles: true }));
        } else {
          document.execCommand('insertLineBreak');
        }
        await sleep(rand(200, 500));
      } else {
        if (isInput) {
          editor.value += ch;
          editor.dispatchEvent(new Event('input', { bubbles: true }));
        } else {
          document.execCommand('insertText', false, ch);
        }
      }

      let delay = rand(paceMs * 0.7, paceMs * 1.4);
      if (ch === ' ' || ch === '!' || ch === '.') delay += rand(100, 300);
      await sleep(delay);
    }

    return true;
  }

  async function attachImagesToChat(files) {
    if (!files || !files.length) return true;
    const composer = findComposer();
    if (!composer) return false;

    const dt = new DataTransfer();
    for (const f of files) dt.items.add(f);

    let fileInput = document.querySelector('input[type="file"]');
    if (!fileInput) {
      const attachBtn = findElement(SELECTORS.attachButton);
      if (attachBtn) {
        await humanClick(attachBtn);
        await sleep(rand(500, 1000));
        fileInput = document.querySelector('input[type="file"]');
      }
    }

    if (fileInput) {
      fileInput.files = dt.files;
      fileInput.dispatchEvent(new Event('input', { bubbles: true }));
      fileInput.dispatchEvent(new Event('change', { bubbles: true }));
      await sleep(rand(1500, 3000));
      return true;
    }

    composer.focus();
    composer.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
    await sleep(rand(1500, 3000));
    return true;
  }

  // ---------- Floating UI Panel ----------

  let isRunning = false;
  let isPaused = false;
  let selectedFiles = [];
  let abortController = false;

  const panel = document.createElement('div');
  panel.id = 'line-human-sender-panel';
  panel.innerHTML = `
    <style>
      #line-human-sender-panel {
        position: fixed;
        bottom: 24px;
        right: 24px;
        width: 390px;
        max-height: 90vh;
        background: #ffffff;
        border-radius: 14px;
        box-shadow: 0 12px 36px rgba(0,0,0,0.22), 0 2px 8px rgba(0,0,0,0.12);
        z-index: 999999;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", sans-serif;
        font-size: 13px;
        color: #1e293b;
        display: flex;
        flex-direction: column;
        overflow: hidden;
        border: 1px solid #e2e8f0;
        transition: all 0.2s ease;
      }
      #line-human-sender-panel.minimized {
        max-height: 48px;
        width: 280px;
      }
      .lhs-head {
        background: linear-gradient(135deg, #06c755, #00b900);
        color: #fff;
        padding: 12px 16px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        font-weight: 600;
        cursor: move;
        user-select: none;
      }
      .lhs-head-title {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 14px;
      }
      .lhs-head-btns {
        display: flex;
        gap: 6px;
      }
      .lhs-icon-btn {
        background: rgba(255,255,255,0.2);
        border: none;
        color: white;
        width: 24px;
        height: 24px;
        border-radius: 6px;
        cursor: pointer;
        font-size: 12px;
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .lhs-icon-btn:hover { background: rgba(255,255,255,0.35); }
      .lhs-body {
        padding: 14px 16px;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 12px;
        max-height: calc(90vh - 50px);
      }
      .lhs-label {
        font-weight: 600;
        margin-bottom: 4px;
        display: flex;
        justify-content: space-between;
        align-items: center;
        color: #334155;
      }
      .lhs-hint {
        font-weight: normal;
        font-size: 11px;
        color: #64748b;
      }
      .lhs-textarea {
        width: 100%;
        min-height: 80px;
        padding: 8px 10px;
        border: 1px solid #cbd5e1;
        border-radius: 8px;
        font-size: 13px;
        box-sizing: border-box;
        resize: vertical;
        font-family: inherit;
      }
      .lhs-textarea:focus {
        outline: none;
        border-color: #06c755;
        box-shadow: 0 0 0 2px rgba(6,199,85,0.2);
      }
      .lhs-post-badge-row {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-top: 4px;
        font-size: 11px;
      }
      .lhs-post-badge {
        color: #059669;
        font-weight: 600;
        background: #ecfdf5;
        padding: 3px 8px;
        border-radius: 4px;
        border: 1px solid #a7f3d0;
      }
      .lhs-text-btn {
        background: none;
        border: none;
        color: #64748b;
        text-decoration: underline;
        cursor: pointer;
        padding: 0;
        font-size: 11px;
      }
      .lhs-text-btn:hover { color: #dc2626; }
      .lhs-file-area {
        border: 2px dashed #cbd5e1;
        border-radius: 8px;
        padding: 10px;
        text-align: center;
        cursor: pointer;
        background: #f8fafc;
        transition: border-color 0.2s;
      }
      .lhs-file-area:hover { border-color: #06c755; background: #f0fdf4; }
      .lhs-file-preview {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-top: 8px;
      }
      .lhs-thumb {
        width: 48px;
        height: 48px;
        border-radius: 6px;
        object-fit: cover;
        border: 1px solid #cbd5e1;
      }
      .lhs-row {
        display: flex;
        gap: 10px;
        align-items: center;
      }
      .lhs-input {
        width: 70px;
        padding: 6px 8px;
        border: 1px solid #cbd5e1;
        border-radius: 6px;
        font-size: 13px;
        box-sizing: border-box;
      }
      .lhs-select {
        flex: 1;
        padding: 6px 8px;
        border: 1px solid #cbd5e1;
        border-radius: 6px;
        font-size: 13px;
        background: #fff;
      }
      .lhs-checkbox-card {
        background: #f8fafc;
        border: 1px solid #e2e8f0;
        border-radius: 8px;
        padding: 8px 10px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .lhs-checkbox-row {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 12px;
        color: #334155;
      }
      .lhs-checkbox-row input { margin: 0; cursor: pointer; }
      .lhs-btns {
        display: flex;
        gap: 8px;
        margin-top: 4px;
      }
      .lhs-btn {
        flex: 1;
        padding: 9px 12px;
        border-radius: 8px;
        font-weight: 600;
        cursor: pointer;
        border: none;
        font-size: 13px;
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 6px;
        transition: all 0.15s ease;
      }
      .lhs-btn-primary { background: #06c755; color: white; }
      .lhs-btn-primary:hover { background: #05ad4a; }
      .lhs-btn-pause { background: #f59e0b; color: white; display: none; }
      .lhs-btn-pause:hover { background: #d97706; }
      .lhs-btn-stop { background: #ef4444; color: white; display: none; }
      .lhs-btn-stop:hover { background: #dc2626; }
      .lhs-status-box {
        background: #f1f5f9;
        border-radius: 8px;
        padding: 10px;
        font-size: 12px;
        display: flex;
        flex-direction: column;
        gap: 4px;
        border-left: 3px solid #06c755;
      }
      .lhs-progress-bar-container {
        height: 6px;
        background: #e2e8f0;
        border-radius: 3px;
        overflow: hidden;
        margin-top: 4px;
      }
      .lhs-progress-bar {
        height: 100%;
        background: #06c755;
        width: 0%;
        transition: width 0.3s;
      }
      .lhs-log {
        max-height: 95px;
        overflow-y: auto;
        font-size: 11px;
        color: #64748b;
        background: #f8fafc;
        border-radius: 6px;
        padding: 6px 8px;
        border: 1px solid #e2e8f0;
        list-style: none;
        margin: 0;
      }
      .lhs-log li { margin-bottom: 3px; }
      .lhs-log li.success { color: #16a34a; }
      .lhs-log li.warn { color: #d97706; }
      .lhs-log li.error { color: #dc2626; }
    </style>

    <div class="lhs-head" id="lhs-drag-handle">
      <div class="lhs-head-title">
        <span>🤖</span>
        <span>LINE OA Human Sender</span>
      </div>
      <div class="lhs-head-btns">
        <button class="lhs-icon-btn" id="lhs-btn-min" title="ย่อ/ขยาย">_</button>
        <button class="lhs-icon-btn" id="lhs-btn-close" title="ปิด">✕</button>
      </div>
    </div>

    <div class="lhs-body" id="lhs-body">
      <div>
        <div class="lhs-label">
          <span>ข้อความที่ต้องการส่ง</span>
          <span class="lhs-hint">ใช้ {{name}} และ {ข้อความสุ่ม|สวัสดี} ได้</span>
        </div>
        <textarea class="lhs-textarea" id="lhs-message" placeholder="สวัสดีครับ คุณ {{name}} วันนี้ทางร้านมีโปรโมชั่นพิเศษสำหรับคุณ..."></textarea>
        <div class="lhs-post-badge-row">
          <span class="lhs-post-badge" id="lhs-post-badge">📌 โพสต์ใหม่ (ยังไม่เคยส่งใคร)</span>
          <button type="button" class="lhs-text-btn" id="lhs-btn-clear-history" title="ล้างประวัติการส่งของโพสต์นี้เพื่อเริ่มส่งใหม่">ล้างประวัติโพสต์นี้</button>
        </div>
      </div>

      <div>
        <div class="lhs-label">
          <span>แนบรูปภาพ (ส่งเหมือนคน)</span>
          <span class="lhs-hint" id="lhs-file-count">0 รูป</span>
        </div>
        <div class="lhs-file-area" id="lhs-drop-zone">
          <span id="lhs-drop-text">คลิกเพื่อเลือกรูปภาพ หรือลากรูปมาวางที่นี่</span>
          <input type="file" id="lhs-file-input" multiple accept="image/*" style="display:none;">
          <div class="lhs-file-preview" id="lhs-file-preview"></div>
        </div>
      </div>

      <div class="lhs-checkbox-card">
        <div class="lhs-checkbox-row">
          <input type="checkbox" id="lhs-no-repeat" checked>
          <label for="lhs-no-repeat" style="font-weight: 600; color: #0f172a;">ไม่ส่งซ้ำลูกค้ารายเดิม (เว้นแต่เป็นโพสต์ใหม่)</label>
        </div>
        <div class="lhs-checkbox-row">
          <input type="checkbox" id="lhs-auto-manual" checked>
          <label for="lhs-auto-manual">คลิก "แชทแบบแมนนวล" อัตโนมัติเมื่อพบบอท</label>
        </div>
        <div class="lhs-checkbox-row">
          <input type="checkbox" id="lhs-skip-active" checked>
          <label for="lhs-skip-active">ข้ามถ้าลูกค้าคุยอยู่ล่าสุด (ภายใน 30 นาที)</label>
        </div>
      </div>

      <div class="lhs-row">
        <div style="flex: 1;">
          <div class="lhs-label"><span>เว้นห่างระหว่างแชท</span></div>
          <div class="lhs-row">
            <input type="number" class="lhs-input" id="lhs-delay-min" value="45" min="10" max="600">
            <span>-</span>
            <input type="number" class="lhs-input" id="lhs-delay-max" value="90" min="10" max="600">
            <span class="lhs-hint">วินาที</span>
          </div>
        </div>
        <div style="flex: 1;">
          <div class="lhs-label"><span>จำนวนแชทสูงสุด</span></div>
          <input type="number" class="lhs-input" id="lhs-max-chats" value="20" min="1" max="500" style="width: 100%;">
        </div>
      </div>

      <div class="lhs-row">
        <div style="flex: 1;">
          <div class="lhs-label"><span>ความเร็วในการพิมพ์</span></div>
          <select class="lhs-select" id="lhs-speed">
            <option value="80">พิมพ์ช้าแบบธรรมชาติ (ปลอดภัยสูงสุด)</option>
            <option value="50" selected>พิมพ์ปกติ (แนะนำ)</option>
            <option value="25">พิมพ์เร็ว</option>
            <option value="0">วางข้อความทีเดียว (ไม่พิมพ์ทีละตัว)</option>
          </select>
        </div>
      </div>

      <div class="lhs-status-box" id="lhs-status-box">
        <div id="lhs-status-text" style="font-weight: 600;">พร้อมทำงาน: เลื่อนส่งทีละแชทแบบคน</div>
        <div id="lhs-status-sub" style="color: #64748b; font-size: 11px;">กด "เริ่มส่งแบบมนุษย์" เพื่อเริ่มทำงาน</div>
        <div class="lhs-progress-bar-container">
          <div class="lhs-progress-bar" id="lhs-progress-bar"></div>
        </div>
      </div>

      <div class="lhs-btns">
        <button class="lhs-btn lhs-btn-primary" id="lhs-btn-start">▶ เริ่มส่งแบบมนุษย์</button>
        <button class="lhs-btn lhs-btn-pause" id="lhs-btn-pause">⏸ พัก</button>
        <button class="lhs-btn lhs-btn-stop" id="lhs-btn-stop">⏹ ยกเลิก</button>
      </div>

      <ul class="lhs-log" id="lhs-log">
        <li>พร้อมทำงาน: ระบบป้องกันการส่งซ้ำเปิดใช้งานแล้ว</li>
      </ul>
    </div>
  `;

  document.body.appendChild(panel);

  // ---------- Panel UI Logic & Events ----------

  const msgInput = document.getElementById('lhs-message');
  const fileInput = document.getElementById('lhs-file-input');
  const dropZone = document.getElementById('lhs-drop-zone');
  const filePreview = document.getElementById('lhs-file-preview');
  const fileCount = document.getElementById('lhs-file-count');
  const delayMinInput = document.getElementById('lhs-delay-min');
  const delayMaxInput = document.getElementById('lhs-delay-max');
  const maxChatsInput = document.getElementById('lhs-max-chats');
  const speedSelect = document.getElementById('lhs-speed');
  const noRepeatCheck = document.getElementById('lhs-no-repeat');
  const autoManualCheck = document.getElementById('lhs-auto-manual');
  const skipActiveCheck = document.getElementById('lhs-skip-active');
  const statusText = document.getElementById('lhs-status-text');
  const statusSub = document.getElementById('lhs-status-sub');
  const progressBar = document.getElementById('lhs-progress-bar');
  const startBtn = document.getElementById('lhs-btn-start');
  const pauseBtn = document.getElementById('lhs-btn-pause');
  const stopBtn = document.getElementById('lhs-btn-stop');
  const logEl = document.getElementById('lhs-log');
  const postBadge = document.getElementById('lhs-post-badge');
  const clearHistoryBtn = document.getElementById('lhs-btn-clear-history');

  function addLog(msg, type = 'info') {
    const li = document.createElement('li');
    li.className = type;
    const time = new Date().toLocaleTimeString('th-TH', { hour12: false });
    li.textContent = `[${time}] ${msg}`;
    logEl.prepend(li);
    while (logEl.children.length > 40) logEl.removeChild(logEl.lastChild);
  }

  // อัปเดตสถานะของโพสต์ปัจจุบัน (เคยส่งไปกี่คนแล้ว)
  function updatePostBadge() {
    const postHash = getPostFingerprint(msgInput.value, selectedFiles);
    const count = getPostRecipientsCount(postHash);
    if (!postBadge) return;
    if (count > 0) {
      postBadge.textContent = `📌 โพสต์นี้: เคยส่งแล้ว ${count} คน (จะไม่ส่งซ้ำ)`;
      postBadge.style.color = '#2563eb';
      postBadge.style.background = '#eff6ff';
      postBadge.style.borderColor = '#bfdbfe';
    } else {
      postBadge.textContent = '📌 โพสต์ใหม่ (ยังไม่เคยส่งใคร)';
      postBadge.style.color = '#059669';
      postBadge.style.background = '#ecfdf5';
      postBadge.style.borderColor = '#a7f3d0';
    }
  }

  msgInput.addEventListener('input', updatePostBadge);

  clearHistoryBtn.addEventListener('click', () => {
    const postHash = getPostFingerprint(msgInput.value, selectedFiles);
    const count = getPostRecipientsCount(postHash);
    if (count === 0) {
      alert('โพสต์นี้ยังไม่เคยมีประวัติการส่ง');
      return;
    }
    if (confirm(`ต้องการล้างประวัติการส่งของโพสต์นี้ (${count} คน) เพื่อให้สามารถส่งซ้ำหาทุกคนได้หรือไม่?`)) {
      clearHistoryForPost(postHash);
      updatePostBadge();
      addLog('ล้างประวัติการส่งของโพสต์นี้เรียบร้อยแล้ว', 'warn');
    }
  });

  // Minimize / Close
  document.getElementById('lhs-btn-min').addEventListener('click', () => {
    panel.classList.toggle('minimized');
  });
  document.getElementById('lhs-btn-close').addEventListener('click', () => {
    if (isRunning && !confirm('กำลังทำงานอยู่ ต้องการปิดเครื่องมือหรือไม่?')) return;
    abortController = true;
    panel.remove();
    window.__lineHumanSenderActive = false;
  });

  // File Handling
  dropZone.addEventListener('click', () => fileInput.click());
  fileInput.addEventListener('change', (e) => {
    handleFiles(e.target.files);
    updatePostBadge();
  });
  dropZone.addEventListener('dragover', (e) => { e.preventDefault(); dropZone.style.borderColor = '#06c755'; });
  dropZone.addEventListener('dragleave', () => { dropZone.style.borderColor = '#cbd5e1'; });
  dropZone.addEventListener('drop', (e) => {
    e.preventDefault();
    dropZone.style.borderColor = '#cbd5e1';
    if (e.dataTransfer.files) {
      handleFiles(e.dataTransfer.files);
      updatePostBadge();
    }
  });

  function handleFiles(files) {
    selectedFiles = Array.from(files).filter((f) => f.type.startsWith('image/'));
    fileCount.textContent = `${selectedFiles.length} รูป`;
    filePreview.innerHTML = '';
    selectedFiles.forEach((file) => {
      const img = document.createElement('img');
      img.className = 'lhs-thumb';
      img.src = URL.createObjectURL(file);
      filePreview.appendChild(img);
    });
    addLog(`เลือกรูปภาพ ${selectedFiles.length} รูป`, 'info');
  }

  // Draggable Header
  const handle = document.getElementById('lhs-drag-handle');
  let isDragging = false, startX, startY, origX, origY;
  handle.addEventListener('mousedown', (e) => {
    if (e.target.tagName === 'BUTTON') return;
    isDragging = true;
    startX = e.clientX;
    startY = e.clientY;
    const r = panel.getBoundingClientRect();
    origX = r.left;
    origY = r.top;
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  });
  function onMouseMove(e) {
    if (!isDragging) return;
    panel.style.left = `${origX + (e.clientX - startX)}px`;
    panel.style.top = `${origY + (e.clientY - startY)}px`;
    panel.style.right = 'auto';
    panel.style.bottom = 'auto';
  }
  function onMouseUp() {
    isDragging = false;
    document.removeEventListener('mousemove', onMouseMove);
    document.removeEventListener('mouseup', onMouseUp);
  }

  // Button Controls
  startBtn.addEventListener('click', () => {
    const text = msgInput.value.trim();
    if (!text && !selectedFiles.length) {
      alert('โปรดใส่ข้อความหรือเลือกรูปภาพที่ต้องการส่ง');
      return;
    }
    startSending();
  });

  pauseBtn.addEventListener('click', () => {
    isPaused = !isPaused;
    pauseBtn.textContent = isPaused ? '▶ ต่อ' : '⏸ พัก';
    statusText.textContent = isPaused ? '⏸ หยุดชั่วคราว' : 'กำลังส่งแบบมนุษย์...';
    addLog(isPaused ? 'หยุดการทำงานชั่วคราว' : 'กลับมาทำงานต่อ', 'warn');
  });

  stopBtn.addEventListener('click', () => {
    if (confirm('ต้องการยกเลิกการส่งหรือไม่?')) {
      abortController = true;
      resetUi();
      addLog('ยกเลิกการส่งแล้ว', 'error');
    }
  });

  function resetUi() {
    isRunning = false;
    isPaused = false;
    startBtn.style.display = 'flex';
    pauseBtn.style.display = 'none';
    stopBtn.style.display = 'none';
    statusText.textContent = 'การส่งสิ้นสุดลงแล้ว';
    statusSub.textContent = 'สามารถเริ่มส่งรอบใหม่ได้ตลอดเวลา';
    progressBar.style.width = '0%';
  }

  // ---------- Execution Engine: Human-like Sending Flow ----------

  async function startSending() {
    isRunning = true;
    isPaused = false;
    abortController = false;

    startBtn.style.display = 'none';
    pauseBtn.style.display = 'flex';
    stopBtn.style.display = 'flex';

    const messageTemplate = msgInput.value;
    const minDelay = parseInt(delayMinInput.value, 10) || 45;
    const maxDelay = parseInt(delayMaxInput.value, 10) || 90;
    const maxChats = parseInt(maxChatsInput.value, 10) || 20;
    const typingPace = parseInt(speedSelect.value, 10);
    const noRepeat = noRepeatCheck.checked;
    const autoManual = autoManualCheck.checked;
    const skipActive = skipActiveCheck.checked;

    // คำนวณ Fingerprint ของโพสต์นี้
    const currentPostHash = getPostFingerprint(messageTemplate, selectedFiles);
    const previousSentCount = getPostRecipientsCount(currentPostHash);

    addLog(`เริ่มส่งข้อความหาลูกค้า สูงสุด ${maxChats} แชท...`, 'info');
    if (noRepeat && previousSentCount > 0) {
      addLog(`โพสต์นี้เคยส่งแล้ว ${previousSentCount} คน (ระบบจะข้ามคนเดิมอัตโนมัติ)`, 'info');
    }

    let processedCount = 0;
    let successCount = 0;
    let skippedRepeatCount = 0;
    let skippedOtherCount = 0;

    function getAvailableRows() {
      // 1. ตารางในหน้ารายชื่อติดต่อ (Contacts)
      const tableRows = [...document.querySelectorAll('table tbody tr, [role="row"]')].filter(visible);
      if (tableRows.length) {
        return tableRows.filter((tr) => {
          const btn = tr.querySelector('button, a');
          const txt = (tr.textContent || '').trim();
          return !!btn && txt.length > 2 && !/^(?:ทั้งหมด|ค้นหา|ชื่อ)$/i.test(txt);
        });
      }
      // 2. รายชื่อแชทในหน้า Chat (ห้องแชท)
      const chatItems = [...document.querySelectorAll('a[href*="/chat/"], [class*="chat-item" i], [class*="chatlist" i] a')].filter(visible);
      return chatItems;
    }

    try {
      while (successCount < maxChats && !abortController) {
        while (isPaused && !abortController) await sleep(500);
        if (abortController) break;

        const rows = getAvailableRows();
        if (!rows.length) {
          addLog('ไม่พบรายการแชทหรือรายชื่อติดต่อในหน้านี้', 'error');
          break;
        }

        if (processedCount >= rows.length) {
          addLog('ตรวจสอบครบทุกแชทในรายการปัจจุบันแล้ว', 'info');
          break;
        }

        // เลือกแถวปัจจุบันที่จะส่ง
        const targetRow = rows[processedCount];
        processedCount++;

        if (!targetRow) break;

        // ดึงชื่อลูกค้าและ User ID จากแถว
        let customerName = '';
        const nameNode = targetRow.querySelector('[class*="name" i], [class*="title" i], strong, h3, h4, td:nth-child(1), td:nth-child(2)');
        if (nameNode) customerName = (nameNode.textContent || '').trim();
        if (!customerName || /^(?:แชท|chat|ดูรายละเอียด)$/i.test(customerName)) {
          customerName = (targetRow.textContent || '').split('\n')[0].trim();
        }
        customerName = customerName.replace(/[\r\n\t]+/g, ' ').slice(0, 40);

        const rowUserId = targetRow.getAttribute('data-user-id') || 
                          targetRow.querySelector('[data-user-id]')?.getAttribute('data-user-id') || 
                          targetRow.querySelector('a[href*="/chat/"]')?.getAttribute('href')?.match(/\/chat\/([A-Za-z0-9_-]+)/)?.[1] || '';

        // 0. ตรวจสอบเงื่อนไข "ไม่ส่งซ้ำลูกค้ารายเดิมสำหรับโพสต์นี้"
        if (noRepeat && hasAlreadyReceivedPost(currentPostHash, rowUserId, customerName)) {
          addLog(`ข้าม ${customerName || 'ลูกค้า'}: เคยส่งโพสต์นี้ไปแล้ว (ไม่ส่งซ้ำ)`, 'warn');
          skippedRepeatCount++;
          continue;
        }

        // เลื่อนหน้าจอไปหาแถวนั้นอย่างนุ่มนวล
        targetRow.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
        await sleep(rand(600, 1200));

        statusText.textContent = `กำลังส่งแชทที่ ${successCount + 1}/${maxChats}: ${customerName || 'ลูกค้า'}`;
        statusSub.textContent = 'กำลังเปิดห้องแชท...';
        progressBar.style.width = `${((successCount) / maxChats) * 100}%`;

        // 1. คลิกเปิดแชท (หาปุ่ม "แชท" หรือคลิกที่แถว)
        const chatBtn = [...targetRow.querySelectorAll('button, a')].find((b) => /^(?:แชท|chat)$/i.test((b.textContent || '').trim())) || targetRow;
        await humanClick(chatBtn);
        await sleep(rand(1800, 3200));

        if (abortController) break;

        // ดึงชื่อลูกค้าจากหัวแชทจริง (ถ้ามี)
        const headerName = getHeaderCustomerName();
        if (headerName) customerName = headerName;

        // 2. ตรวจสอบ "แชทแบบแมนนวล"
        if (autoManual && hasAutoReplyOverlay()) {
          const manualBtn = findManualChatButton();
          if (manualBtn) {
            statusSub.textContent = 'สลับเป็น "แชทแบบแมนนวล"...';
            addLog(`สลับเป็นแชทแบบแมนนวล (${customerName})`, 'warn');
            await humanClick(manualBtn);
            await sleep(rand(1200, 2200));
            for (const d of document.querySelectorAll('[role="dialog"], [class*="modal" i]')) {
              const confirmBtn = [...d.querySelectorAll('button')].find((b) => /^(?:ตกลง|ยืนยัน|ใช่|ok|confirm)$/i.test((b.textContent || '').trim()));
              if (confirmBtn) {
                await humanClick(confirmBtn);
                await sleep(rand(800, 1500));
              }
            }
          }
        }

        if (abortController) break;

        // 3. ตรวจสอบกล่องพิมพ์ข้อความ
        const composer = findComposer();
        if (!composer) {
          addLog(`ข้าม ${customerName}: ไม่พบช่องพิมพ์ข้อความ (อาจถูกบล็อกหรือแชทปิดอยู่)`, 'warn');
          skippedOtherCount++;
          continue;
        }

        // 4. พิมพ์ข้อความเสมือนมนุษย์
        const personalizedMsg = formatMessage(messageTemplate, customerName);
        if (personalizedMsg) {
          statusSub.textContent = `กำลังพิมพ์ข้อความ (${personalizedMsg.length} ตัวอักษร)...`;
          if (typingPace > 0) {
            await typeHumanLike(composer, personalizedMsg, typingPace, () => abortController);
          } else {
            composer.focus();
            if (composer.tagName === 'TEXTAREA' || composer.tagName === 'INPUT') {
              composer.value = personalizedMsg;
              composer.dispatchEvent(new Event('input', { bubbles: true }));
            } else {
              document.execCommand('insertText', false, personalizedMsg);
            }
          }
          await sleep(rand(800, 1800));
        }

        if (abortController) break;

        // 5. แนบรูปภาพ (ถ้ามี)
        if (selectedFiles.length) {
          statusSub.textContent = `กำลังแนบรูปภาพ ${selectedFiles.length} รูป...`;
          await attachImagesToChat(selectedFiles);
          await sleep(rand(1200, 2500));
        }

        if (abortController) break;

        // 6. กดส่ง
        const sendBtn = findSendButton();
        if (sendBtn) {
          statusSub.textContent = 'กำลังกดส่งข้อความ...';
          await humanClick(sendBtn);
          await sleep(rand(1500, 2500));
          successCount++;

          // บันทึกลงประวัติเพื่อป้องกันการส่งซ้ำ
          markPostSent(currentPostHash, rowUserId, customerName, personalizedMsg, selectedFiles.length);
          updatePostBadge();

          addLog(`ส่งสำเร็จ: ${customerName}`, 'success');
        } else {
          addLog(`ไม่พบปุ่มส่งสำหรับ ${customerName}`, 'error');
          skippedOtherCount++;
        }

        progressBar.style.width = `${(successCount / maxChats) * 100}%`;

        // 7. พักรอระหว่างแชท (Human Random Gap) เพื่อความปลอดภัย
        if (successCount < maxChats && !abortController) {
          const waitSec = rand(minDelay, maxDelay);
          addLog(`พักรอแบบมนุษย์ ${waitSec} วินาทีก่อนส่งแชทถัดไป...`, 'info');
          for (let sec = waitSec; sec > 0; sec--) {
            if (abortController) break;
            while (isPaused && !abortController) await sleep(500);
            statusText.textContent = `รอแชทถัดไป... (${sec} วินาที)`;
            statusSub.textContent = `ส่งสำเร็จ ${successCount} คน | ข้ามคนเดิม ${skippedRepeatCount} คน`;
            await sleep(1000);
          }
        }
      }
    } catch (err) {
      addLog(`เกิดข้อผิดพลาด: ${err.message}`, 'error');
    } finally {
      resetUi();
      statusText.textContent = `เสร็จสิ้น: ส่งสำเร็จ ${successCount} คน (ข้ามคนเดิม ${skippedRepeatCount} คน)`;
      addLog(`สรุปผล: ส่งสำเร็จ ${successCount} คน, ข้ามคนเดิม ${skippedRepeatCount} คน, ข้ามอื่นๆ ${skippedOtherCount} คน`, 'success');
    }
  }

  // อัปเดตสถานะโพสต์ตอนเปิดเครื่องมือครั้งแรก
  setTimeout(updatePostBadge, 300);

  console.log('%c[LINE OA Human Sender] เปิดใช้งานสำเร็จ!', 'color: #06c755; font-size: 16px; font-weight: bold;');
  console.log('ระบบป้องกันการส่งซ้ำเปิดใช้งานอยู่: จะไม่ส่งซ้ำลูกค้ารายเดิม เว้นแต่คุณจะเปลี่ยนข้อความหรือรูปภาพ (โพสต์ใหม่)');
})();
