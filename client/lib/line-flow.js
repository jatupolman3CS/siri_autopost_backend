// The LINE Official Account side of the worker: what the service worker does with the chat.line.biz tab (line.js is the
// content script that touches the page). Like postToGroup for Facebook, but every step is built around the customer on the
// other end: it opens one chat, makes sure it is the right customer, leaves a customer who is in the middle of a
// conversation alone, and types and sends like a person.
//
// background.js owns the helpers (tabCmd, typeHuman, the abort flag, the error classes...) and hands them over in
// createLineFlow(deps), because they are module-private there. Nothing here takes a screenshot (captureWorker is
// deliberately not passed): a LINE window shows customers' messages, and no picture of it may leave the browser.
//
// Safety invariants (spec D11), none of which may be weakened:
//  (a) the open chat is verified to be the intended customer BEFORE anything is typed (the userId in the address, and the
//      header name when it is readable), and line.js checks it again at the moment of the click on "ส่ง";
//  (b) the server decides the allowed hours (it hands out no LINE job outside them); this flow never starts a send by itself;
//  (c) a customer who wrote last and is still active (or has an unread message) is not interrupted: the job is deferred;
//  (d) no message text is ever read from the page or logged: only counts, flags and the time of the newest bubble.
import { lineChatTarget } from './shared.js';

const OA_ORIGIN = 'https://chat.line.biz/';
const LOAD_FAIL = 'โหลดหน้า LINE OA Manager ไม่สำเร็จ';

// Every pause is sleep(rand(a, b)); the numbers are starting values (milliseconds), all in one place.
export const TIMING = {
  look: [2500, 6000], // after opening the OA page
  pageReadyMs: 25000, // how long the app may take to draw its chat list
  browsePauses: [1200, 4500],
  beforeOpenChat: [800, 2200],
  afterOpenChat: [1500, 4000],
  read: [2000, 7000], // reading the conversation before deciding
  beforeManual: [800, 2500],
  beforeFocus: [600, 1800],
  afterTyping: [600, 1500],
  reread: [800, 2500],
  betweenBubbles: [1200, 4000],
  beforeImages: [800, 2000],
  afterImages: [800, 2000],
  settle: [2000, 6000],
  sendConfirmMs: 25000,
  imageReadyMs: 300000,
  imagesPerSend: 10, // pictures per click on "ส่ง" (assumption A8)
  syncBurstRounds: 25,
  syncBurstPause: [3000, 8000],
  syncEveryMs: 24 * 3600000, // the customer list is read once a day (the contacts page; the web button reads it on demand)
  syncJitterMs: 60 * 60000,
  contactPages: 400, // pages of the contacts table read at most in one go
  contactPagePause: [1500, 3500],
};

// Contains-match on names with everything but letters and digits stripped: the header may decorate a name that the chat
// list showed plainly (emoji, punctuation, spacing). Not comparable (one side is empty after stripping) = no objection.
export function looseSameName(header, expected, textClose) {
  const norm = (s) => String(s || '').normalize('NFC').toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');
  const a = norm(header);
  const e = norm(expected);
  if (!a || !e) return true;
  return a === e || a.includes(e) || e.includes(a) || (textClose ? textClose(a, e) : false);
}

// Minutes since a time of day ("11:34" = today at that time); a time later than now was before midnight.
export function minutesSinceHHmm(hhmm, now = new Date()) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(String(hhmm || ''));
  if (!m) return null;
  let diff = now.getHours() * 60 + now.getMinutes() - (Number(m[1]) * 60 + Number(m[2]));
  if (diff < 0) diff += 1440;
  return diff;
}

export function createLineFlow(deps) {
  const {
    tabCmd, tabCmdOk, openWorkerTab, navigateAndWait, typeHuman, typingPace, typingEstimateMs, pollState, textClose,
    sleep, rand, randInt, log, abortCheck, Aborted, Fatal, Blocked, getState, TYPING_SPEEDS, segmenter,
    cloudApi, getCloud, setCloud, tuning, heartbeat,
  } = deps;

  // ---------- LINE wording (the helpers of background.js say "Facebook") ----------

  const lineBlockNotice = (text) => `LINE แจ้งเตือน: ${text || 'พบหน้าจำกัดการใช้งาน'}`;

  async function blockNotice(tabId) {
    try {
      const r = await tabCmd(tabId, 'blockCheck', {}, 15000);
      return r && r.blocked ? r.text || 'LINE แจ้งเตือน' : null;
    } catch {
      return null;
    }
  }

  async function assertNotBlocked(tabId) {
    const text = await blockNotice(tabId);
    if (text) {
      // The text comes from a dialog or a toast, never from the conversation: it is what the owner needs to see.
      await log('warn', `[LINE] พบข้อความเตือนในหน้า LINE: ${text}`);
      throw new Blocked(lineBlockNotice(text));
    }
  }

  // ---------- the tab ----------

  // Runs in the PAGE's own world (a content script cannot see the page's Vue objects). The rows of chat.line.biz's chat list
  // carry no address or data attribute with the customer's id: it only lives in the row component's props
  // (chatModel.payload.chatId, checked on the real page 2026-10-27). This copies that id into a `data-fbap-uid` attribute,
  // which line.js reads. It only reads the page's own data: nothing is clicked, so no chat is marked as read.
  function stampChatRows() {
    if (window.__fbapStamp) return;
    window.__fbapStamp = true;
    const re = /^U[0-9a-f]{32}$/;
    const stamp = () => {
      for (const row of document.querySelectorAll('.list-group-item-chat')) {
        if (row.hasAttribute('data-fbap-uid')) continue;
        const id = row.__vue__?.$props?.chatModel?.payload?.chatId;
        if (typeof id === 'string' && re.test(id)) row.setAttribute('data-fbap-uid', id);
      }
    };
    stamp();
    new MutationObserver(stamp).observe(document.body, { childList: true, subtree: true });
  }

  // The contacts table (/<bot>/contact): the id of each customer is in the props of the avatar component of its row.
  function stampContactRows() {
    if (window.__fbapStampContacts) return;
    window.__fbapStampContacts = true;
    const re = /^U[0-9a-f]{32}$/;
    const stamp = () => {
      for (const tr of document.querySelectorAll('table tbody tr')) {
        if (tr.hasAttribute('data-fbap-uid')) continue;
        const av = tr.querySelector('.avatars');
        const id = av && av.__vue__ && av.__vue__.$props && av.__vue__.$props.profiles && av.__vue__.$props.profiles.userId;
        if (typeof id === 'string' && re.test(id)) tr.setAttribute('data-fbap-uid', id);
      }
    };
    stamp();
    new MutationObserver(stamp).observe(document.body, { childList: true, subtree: true });
  }

  async function injectLine(tabId) {
    try {
      await chrome.scripting.executeScript({ target: { tabId }, files: ['line.js'] });
      await chrome.scripting.executeScript({ target: { tabId }, world: 'MAIN', func: stampChatRows }).catch(() => {});
      await chrome.scripting.executeScript({ target: { tabId }, world: 'MAIN', func: stampContactRows }).catch(() => {});
    } catch (e) {
      throw new Error(`ส่วนขยายยังไม่ได้รับอนุญาตให้ใช้ chat.line.biz: กด "ใช้ LINE OA ในเครื่องนี้" ที่หน้าสถานะของส่วนขยาย (${e?.message || e})`);
    }
  }

  const onBotPage = (url, botId) => {
    try {
      const u = new URL(url);
      if (u.hostname !== 'chat.line.biz') return false;
      // Only the OA's chat view is reusable: the contacts page (/<bot>/contact, left behind by a roster read) has no composer.
      const bot = '/' + String(botId).toLowerCase();
      const path = u.pathname.toLowerCase().replace(/\/+$/, '');
      return path === bot || path.startsWith(bot + '/chat');
    } catch {
      return false;
    }
  };

  // The worker tab, left on the OA's page when it is already there (a person does not reload the page between two chats).
  async function ensureLineTab(botId, focus) {
    const st = await getState();
    if (botId && st.tabId != null) {
      try {
        const tab = await chrome.tabs.get(st.tabId);
        if (tab.windowId === st.windowId && onBotPage(tab.url, botId)) {
          if (focus) {
            const win = await chrome.windows.get(tab.windowId);
            await chrome.windows.update(tab.windowId, win.state === 'minimized' ? { state: 'normal', focused: true } : { focused: true });
            await chrome.tabs.update(tab.id, { active: true });
          }
          return tab.id;
        }
      } catch {
        /* the tab is gone: open the page */
      }
    }
    return openWorkerTab(botId ? `${OA_ORIGIN}${botId}/` : OA_ORIGIN, focus, LOAD_FAIL);
  }

  // Waits until the app has drawn its chat list (or shows a login page).
  async function waitLinePage(tabId) {
    const end = Date.now() + TIMING.pageReadyMs;
    for (;;) {
      abortCheck();
      const page = await tabCmd(tabId, 'check');
      if (page.login || page.ready || page.accounts > 0 || Date.now() >= end) return page;
      await sleep(rand(1000, 2000));
    }
  }

  // ---------- sending to one chat ----------

  // job: { groupUrl, groupName, bubbles: string[], imageIds: storage keys, line: { skipIfActiveMinutes, imagesFirst } | null }
  // Returns { ok: true, groupName, shot: null, blockedAfter } when sent, { ok: false, defer: true, deferMinutes, reason }
  // when the customer is busy (nothing was sent), { ok: false, skip: true, reason } when the chat cannot be written to, and
  // throws for everything else (Fatal = login lost, Blocked = LINE shows a warning, Aborted, Error).
  async function sendToLineChat(job, cfg, global) {
    const check = () => abortCheck();
    const speed = TYPING_SPEEDS[cfg.typingSpeed] || TYPING_SPEEDS.normal;
    const target = lineChatTarget(job.groupUrl);
    if (!target) throw new Error('ลิงก์แชท LINE ของงานนี้ไม่ถูกต้อง');
    const { botId, userId } = target;
    const opts = job.line || {};
    const skipMinutes = Math.max(1, Number(opts.skipIfActiveMinutes) || 30);
    const bubbles = (job.bubbles || []).map((b) => String(b || '')).filter((b) => b.trim());
    const imageIds = job.imageIds || [];
    if (!bubbles.length && !imageIds.length) throw new Error('งานนี้ไม่มีข้อความและไม่มีรูปให้ส่ง');

    let prevWindowId = null;
    let tabId = null;
    let groupName = job.groupName || '';
    let sent = false; // something already reached the customer: a later problem never turns it into a failure
    let typed = false; // this flow put text in the box (and so may clear it again)
    let searched = false; // this flow typed in the list's search box
    const state = () => tabCmdOk(tabId, 'lineChatState', {}, 20000);
    const busy = (reason, minutes = skipMinutes) => ({ ok: false, defer: true, deferMinutes: randInt(minutes, minutes * 2), reason });

    try {
      // 1. window focus, as postToGroup does
      if (global.focusWindow) {
        try {
          prevWindowId = (await chrome.windows.getLastFocused()).id;
        } catch {
          prevWindowId = null;
        }
      }

      // 2. the OA's page
      const cloud = await getCloud();
      if (cloud.lineBotId && cloud.lineBotId.toLowerCase() !== botId.toLowerCase()) {
        throw new Error('บัญชี LINE OA ในเบราว์เซอร์ไม่ตรงกับ OA ของงานนี้');
      }
      tabId = await ensureLineTab(botId, global.focusWindow);
      check();
      await sleep(rand(...TIMING.look));
      await injectLine(tabId);

      // 3. login and OA
      let page = await waitLinePage(tabId);
      if (page.login) throw new Fatal('ยังไม่ได้ล็อกอิน LINE OA Manager ใน Chrome นี้');
      if (!page.ready) throw new Error('หน้า LINE OA Manager โหลดไม่เสร็จหรือหน้าตาเปลี่ยนไป (ไม่พบรายชื่อแชท)');
      if (page.botId && page.botId.toLowerCase() !== botId.toLowerCase()) {
        throw new Error('บัญชี LINE OA ในเบราว์เซอร์ไม่ตรงกับ OA ของงานนี้');
      }

      // 4. a warning on the page
      await assertNotBlocked(tabId);
      check();

      // 5. look around the way a person does before writing to somebody
      if (cfg.browseBeforePost) {
        const n = randInt(1, 2);
        for (let i = 0; i < n; i++) {
          await tabCmd(tabId, 'scroll', { dy: randInt(150, 500), where: 'list' });
          await sleep(rand(...TIMING.browsePauses));
          check();
        }
        await tabCmd(tabId, 'scrollTop', { where: 'list' });
        await sleep(rand(800, 2000));
        check();
      }

      // 6. open the chat. A customer with an unread message is left alone BEFORE the chat is opened (opening it would mark
      // the message as read for the shop's staff).
      let peek = await tabCmd(tabId, 'lineClickRow', { userId, click: false }, 120000);
      if (!peek.found && !peek.already && job.groupName && job.groupName !== 'ลูกค้า') {
        await sleep(rand(...TIMING.beforeOpenChat));
        await tabCmdOk(tabId, 'lineFocusSearch');
        searched = true;
        await sleep(rand(500, 1200));
        await typeHuman(tabId, job.groupName, speed, false, check);
        await sleep(rand(1500, 3000));
        check();
        peek = await tabCmd(tabId, 'lineClickRow', { userId, click: false }, 120000);
      }
      if (peek.found && peek.unread) {
        await log('info', `[LINE] ${job.groupName}: มีข้อความที่ยังไม่ได้อ่าน เลื่อนการส่งออกไป`);
        await tabCmd(tabId, 'discard', { search: searched }, 15000).catch(() => {});
        return busy('ลูกค้ามีข้อความที่ยังไม่ได้อ่าน');
      }
      if (peek.found && !peek.already) {
        await sleep(rand(...TIMING.beforeOpenChat));
        await tabCmdOk(tabId, 'lineClickRow', { userId }, 120000);
      } else if (!peek.found) {
        // Not in the list (a long roster, a renamed customer): open the address itself.
        await log('info', `[LINE] ไม่พบ ${job.groupName || 'ลูกค้า'} ในรายชื่อแชท เปิดจากลิงก์แชทแทน`);
        if (!(await navigateAndWait(tabId, job.groupUrl, 60000))) throw new Error(LOAD_FAIL);
        await sleep(rand(...TIMING.look));
        await injectLine(tabId);
        page = await waitLinePage(tabId);
        if (page.login) throw new Fatal('ยังไม่ได้ล็อกอิน LINE OA Manager ใน Chrome นี้');
      }
      if (searched) await tabCmd(tabId, 'discard', { search: true }, 15000).catch(() => {});
      await sleep(rand(...TIMING.afterOpenChat));
      check();

      // 7. identity (D11a): the right customer, or nothing is typed
      let st = await state();
      // `why` says which check refused (never a name or an id: those are the customer's).
      const wrongChat = (why) => new Error(`แชทที่เปิดไม่ตรงกับลูกค้าในงาน ยกเลิกการส่ง (${why})`);
      if (!st.userId || st.userId.toLowerCase() !== userId.toLowerCase()) throw wrongChat(st.userId ? 'รหัสลูกค้าไม่ตรง' : 'อ่านรหัสลูกค้าของแชทที่เปิดไม่ได้');
      // "ลูกค้า" is the name a link gets when it has none: nothing to compare the header with then.
      const nameWrong = (x) => !!x.headerName && !!job.groupName && job.groupName !== 'ลูกค้า' && !looseSameName(x.headerName, job.groupName, textClose);
      // The address changes before the page has drawn the customer's name: look again for a few seconds before refusing
      // (the first real run read another text of the page at this moment and refused a chat that was the right one).
      for (let i = 0; i < 6 && nameWrong(st); i++) {
        await sleep(1500);
        st = await state();
        if (!st.userId || st.userId.toLowerCase() !== userId.toLowerCase()) throw wrongChat('รหัสลูกค้าเปลี่ยนไประหว่างตรวจ');
      }
      if (nameWrong(st)) throw wrongChat(`ชื่อหัวแชทไม่ตรง ยาว ${st.headerName.length} / ${job.groupName.length}`);
      if (st.headerName) groupName = st.headerName;
      await assertNotBlocked(tabId);

      // 8. read the conversation for a moment
      await tabCmd(tabId, 'scroll', { dy: -randInt(60, 220) });
      await sleep(rand(...TIMING.read));
      await tabCmd(tabId, 'scrollBottom');
      check();

      // 9. do not interrupt (D4): the customer wrote last and is (or just was) active
      st = await state();
      if (st.userId.toLowerCase() !== userId.toLowerCase()) throw wrongChat();
      // A chat in the roster has messages. Not seeing a single bubble means line.js cannot read this page (it changed), and
      // then "is the customer in the middle of a conversation?" cannot be answered: better no send than a blind one.
      if (st.lastBubbleSide === null) {
        throw new Error('ไม่พบข้อความในแชทนี้ (หน้า LINE อาจเปลี่ยนไป) จึงไม่แน่ใจว่าลูกค้ากำลังคุยอยู่หรือไม่ ยกเลิกการส่ง');
      }
      if (st.lastBubbleSide === 'in') {
        const ago = minutesSinceHHmm(st.lastBubbleTime);
        const active = st.unreadInList || (st.lastBubbleToday === true && (ago === null || ago < skipMinutes));
        if (active) {
          await log('info', `[LINE] ${groupName}: ลูกค้ากำลังคุยอยู่ เลื่อนการส่งออกไป`);
          return busy('ลูกค้ากำลังคุยอยู่');
        }
      }

      // 10. a chat that cannot be written to is skipped, never retried. Only a composer that is plainly dead counts: a page
      // that merely looks different from what line.js expects is an error (and counts as a failure), not a reason to
      // switch a customer off for good.
      if (st.cannotSend && !st.manualButton) {
        return { ok: false, skip: true, reason: 'ส่งข้อความในแชทนี้ไม่ได้ (ลูกค้าอาจบล็อก OA หรือแชทถูกลบ)' };
      }
      const botBar = st.manualButton || st.overlay || st.autoReplyBar;
      if (!st.composerFound && !botBar) throw new Error('ไม่พบช่องพิมพ์ข้อความของแชท LINE');
      if (st.composerFound && st.composerDisabled && !botBar) {
        return { ok: false, skip: true, reason: 'ส่งข้อความในแชทนี้ไม่ได้ (ลูกค้าอาจบล็อก OA หรือแชทถูกลบ)' };
      }

      // 11. the OA's auto-reply covers the box until "แชทแบบแมนนวล" is clicked (may pause the auto-reply for this chat)
      if (st.manualButton || st.overlay) {
        if (!st.manualButton) throw new Error('ช่องพิมพ์ถูกปิดอยู่ แต่ไม่พบปุ่ม "แชทแบบแมนนวล"');
        await sleep(rand(...TIMING.beforeManual));
        check();
        const sw = await tabCmd(tabId, 'lineManualSwitch', {}, 40000);
        if (!sw.ok) throw new Error(sw.error || 'สลับเป็นแชทแบบแมนนวลไม่สำเร็จ');
        await log('info', `[LINE] ${groupName}: สลับเป็นแชทแบบแมนนวล`);
      }

      // 12. into the box; a box that already holds text belongs to somebody typing there: leave it alone
      await tabCmdOk(tabId, 'lineFocusComposer');
      await sleep(rand(...TIMING.beforeFocus));
      st = await state();
      if (!st.composerFound) throw new Error('ไม่พบช่องพิมพ์ข้อความของแชท LINE');
      if (!st.composerReady) throw new Error('ช่องพิมพ์ข้อความของแชท LINE ยังใช้ไม่ได้');
      if (!st.composerEmpty) {
        await log('info', `[LINE] ${groupName}: มีข้อความพิมพ์ค้างอยู่ในช่องแชทนี้ เลื่อนการส่งออกไป`);
        return busy('มีข้อความพิมพ์ค้างอยู่ในช่องแชทนี้');
      }
      check();

      // 13./14. the messages and the pictures
      const sendOneBubble = async (text) => {
        const s0 = await state();
        const before = s0.outgoingCount;
        const failedBefore = s0.failedCount;
        const len = Array.from(segmenter.segment(text)).length;
        const paste = cfg.maxTypeChars > 0 && len > cfg.maxTypeChars;
        const pace = paste ? 1 : typingPace(len, speed, cfg.typingBudgetMs);
        await log(
          'info',
          paste
            ? `[LINE] วางข้อความ ${len} ตัวอักษรทีเดียว (${cfg.maxTypeChars === 1 ? 'ปิด "จำลองการพิมพ์" ในหน้าป้องกันการโดนแบน' : `ยาวเกิน ${cfg.maxTypeChars} ตัวอักษร`})`
            : `[LINE] พิมพ์ข้อความ ${len} ตัวอักษรทีละตัว ใช้เวลาประมาณ ${Math.round((typingEstimateMs(len, speed) * pace) / 1000)} วินาที`,
        );
        typed = true;
        if (paste) {
          await sleep(rand(800, 2000));
          await tabCmdOk(tabId, 'setText', { text });
        } else {
          await typeHuman(tabId, text, speed, cfg.typos, check, pace);
        }
        await sleep(rand(...TIMING.afterTyping));
        let r = await tabCmd(tabId, 'getText');
        if (!textClose(r.text, text)) {
          await log('warn', '[LINE] ข้อความในช่องแชทไม่ตรง แก้โดยวางข้อความใหม่ทั้งหมด');
          await tabCmdOk(tabId, 'setText', { text });
          await sleep(rand(800, 1500));
          r = await tabCmd(tabId, 'getText');
          if (!textClose(r.text, text)) throw new Error('ใส่ข้อความในช่องแชทไม่สำเร็จ');
        }
        check();
        await sleep(rand(...TIMING.reread)); // re-read before sending
        check();
        await tabCmdOk(tabId, 'lineSend', { expectUserId: userId }, 40000);
        await confirmSent(before, failedBefore, (s) => s.composerEmpty);
        typed = false;
        sent = true;
      };

      // The send is confirmed when the box emptied and the conversation grew (or, when the page's bubbles cannot be told
      // apart, when the box emptied alone: a doubt is resolved towards "sent", never towards sending the text again).
      const confirmSent = async (before, failedBefore, emptied) => {
        let s = await pollState(
          tabId,
          (x) => x.failedCount > failedBefore || x.blocked || (emptied(x) && (x.outgoingCount > before || x.bubbleCount === 0)),
          TIMING.sendConfirmMs, check, 'lineChatState',
        );
        if (!s) s = await state();
        if (s.failedCount > failedBefore) throw new Error('LINE แจ้งว่าส่งไม่สำเร็จ');
        if (!emptied(s)) {
          if (s.blocked) await assertNotBlocked(tabId);
          throw new Error('กดส่งแล้วแต่ข้อความยังค้างอยู่ในช่องพิมพ์ (อาจส่งไม่สำเร็จ)');
        }
        if (!(s.outgoingCount > before) && s.bubbleCount > 0) {
          await log('warn', '[LINE] ไม่เห็นข้อความใหม่ในแชท แต่ช่องพิมพ์ว่างแล้ว ถือว่าส่งแล้ว');
        }
      };

      const sendPictures = async () => {
        for (let off = 0; off < imageIds.length; off += TIMING.imagesPerSend) {
          const chunk = imageIds.slice(off, off + TIMING.imagesPerSend);
          // Step lines: a picture that stalls must show where (the first real run of the modal flow hung without a word).
          await log('info', `[LINE] เตรียมแนบรูป ${chunk.length} รูป`);
          const s0 = await state();
          check();
          await sleep(rand(...TIMING.beforeImages));
          const r = await tabCmdOk(tabId, 'attachImages', { imageIds: chunk }, 240000);
          await log('info', `[LINE] แนบรูป ${chunk.length} รูป (${r.method}${r.dialog ? ', มีหน้าต่างยืนยัน' : ''})`);
          const ready = await pollState(tabId, (x) => !x.uploading && x.sendEnabled, TIMING.imageReadyMs, check, 'lineChatState');
          if (!ready) throw new Error('อัปโหลดรูปไม่เสร็จภายในเวลา');
          await log('info', '[LINE] รูปพร้อมส่งแล้ว');
          check();
          await sleep(rand(...TIMING.afterImages));
          await tabCmdOk(tabId, 'lineSend', { expectUserId: userId }, 60000);
          await log('info', '[LINE] กดส่งรูปแล้ว รอยืนยัน');
          // Through LINE's confirmation modal the composer's own ส่ง stays enabled: "done" is the modal being gone.
          await confirmSent(s0.outgoingCount, s0.failedCount, (x) => !x.uploading && !x.uploadDialog && (!!r.dialog || !x.sendEnabled));
          sent = true;
          if (off + TIMING.imagesPerSend < imageIds.length) await sleep(rand(...TIMING.betweenBubbles));
        }
      };

      if (opts.imagesFirst && imageIds.length) {
        await sendPictures();
        if (bubbles.length) await sleep(rand(...TIMING.betweenBubbles));
      }
      for (let i = 0; i < bubbles.length; i++) {
        check();
        await sendOneBubble(bubbles[i]);
        if (i < bubbles.length - 1 || (!opts.imagesFirst && imageIds.length)) await sleep(rand(...TIMING.betweenBubbles));
      }
      if (!opts.imagesFirst && imageIds.length) await sendPictures();

      // 15. after the last send (a failed send was already caught by confirmSent)
      await sleep(rand(...TIMING.settle));
      // A warning that pops up right after the send does not undo it: the caller rests the whole device.
      const warn = await blockNotice(tabId);
      await tabCmd(tabId, 'scrollBottom').catch(() => {});
      return { ok: true, groupName, shot: null, blockedAfter: warn ? lineBlockNotice(warn) : '' };
    } catch (err) {
      let e = err;
      if (sent) {
        // The first bubble is out: never report this as a failure (a retry would send it again).
        await log('warn', `[LINE] ส่งข้อความถึง ${groupName || 'ลูกค้า'} ไปแล้ว แต่เกิดปัญหาภายหลัง: ${e?.message || e}`);
        // A picture's confirmation modal must not stay open when the window is left.
        if (tabId != null) await tabCmd(tabId, 'discard', { dialog: true }, 15000).catch(() => {});
        return { ok: true, groupName, shot: null, blockedAfter: e instanceof Blocked ? e.message : '' };
      }
      if (tabId != null && !(e instanceof Aborted) && !(e instanceof Blocked) && !(e instanceof Fatal)) {
        const text = await blockNotice(tabId);
        if (text) {
          await log('warn', `[LINE] พบข้อความเตือนในหน้า LINE: ${text}`);
          e = new Blocked(lineBlockNotice(text));
        }
      }
      if (e && typeof e === 'object' && !(e instanceof Aborted)) e.groupName = groupName;
      if (tabId != null && (typed || searched)) {
        try {
          await tabCmd(tabId, 'discard', { composer: typed, search: searched }, 15000);
        } catch {
          /* ignore */
        }
      }
      throw e;
    } finally {
      if (prevWindowId != null) {
        const st = await getState();
        if (prevWindowId !== st.windowId) chrome.windows.update(prevWindowId, { focused: true }).catch(() => {});
      }
    }
  }

  // ---------- login probe ----------

  // Opens the OA's page the way a person looks whether they are still logged in. Returns
  //   { login: true }                              the login page (or no app) is showing,
  //   { login: false, botId, oaName, choices }     the app is there; botId '' + several choices = the person must pick an OA.
  // The caller runs it in the post queue under a deadline (probeFacebookLogin is the pattern).
  async function probeLineLogin() {
    const c = await getCloud();
    const tabId = await ensureLineTab(c.lineBotId, false);
    await sleep(rand(2500, 5000));
    await injectLine(tabId);
    const page = await waitLinePage(tabId);
    if (page.login) return { login: true };
    let botId = page.botId || c.lineBotId || '';
    let oaName = page.oaName || (botId && botId === c.lineBotId ? c.lineOaName : '') || '';
    let choices = [];
    if (!page.botId) {
      // On chat.line.biz/ with no OA picked: LINE shows the list of OAs (or redirects to the only one).
      const r = await tabCmd(tabId, 'lineListAccounts');
      choices = r.accounts || [];
      if (choices.length === 1) {
        botId = choices[0].botId;
        oaName = choices[0].name || oaName;
        choices = [];
      } else if (choices.length > 1) {
        botId = '';
      } else if (!page.ready) {
        return { login: true }; // neither an OA's page nor a list of OAs: not a page we can use
      }
    }
    return { login: false, botId, oaName, choices };
  }

  // ---------- roster sync ----------

  // Reads the customers from the contacts page in one go (every page of the table): { chats, complete, note }, or null when
  // that page gave no rows (the caller then reads the chat list instead). Nothing is clicked but the table's own "next page".
  async function readContactsRoster(botId) {
    const tabId = await openWorkerTab(`${OA_ORIGIN}${botId}/contact`, false, LOAD_FAIL);
    await sleep(rand(2500, 4500));
    await injectLine(tabId);
    const end = Date.now() + TIMING.pageReadyMs;
    let r;
    for (;;) {
      abortCheck();
      r = await tabCmdOk(tabId, 'lineListContacts', {}, 20000);
      if (r.rowCount > 0 || Date.now() >= end) break;
      await sleep(rand(1000, 2000));
    }
    if (!r.rowCount) return null;
    const map = new Map();
    let complete = true;
    let pages = 0;
    for (;;) {
      abortCheck();
      // A lazily loaded table grows when scrolled: read until the number of rows stops growing.
      let seen = r.rowCount;
      for (let i = 0; i < 4; i++) {
        await sleep(rand(800, 1600));
        const more = await tabCmdOk(tabId, 'lineListContacts', { scroll: true }, 20000);
        if (more.rowCount <= seen) break;
        seen = more.rowCount;
        r = more;
      }
      for (const c of r.contacts) if (!c.blocked && !map.has(c.userId)) map.set(c.userId, c);
      if (r.unstamped > 0) complete = false;
      pages++;
      if (!r.hasNext) break;
      if (pages >= TIMING.contactPages) {
        complete = false;
        break;
      }
      await sleep(rand(...TIMING.contactPagePause));
      const n = await tabCmdOk(tabId, 'lineNextContactsPage', {}, 20000);
      if (!n.clicked) break;
      await sleep(rand(...TIMING.contactPagePause));
      r = await tabCmdOk(tabId, 'lineListContacts', {}, 20000);
    }
    const chats = [...map.values()].slice(0, 5000).map((x) => ({
      userId: x.userId,
      name: x.name || '',
      labels: x.labels || [],
      lastMessageAt: x.lastMessageAt || undefined,
      unread: false,
    }));
    return { chats, complete: complete && map.size <= 5000, note: `อ่านรายชื่อลูกค้าจากหน้ารายชื่อติดต่อ ${pages} หน้า` };
  }

  // Reads the customers' chat list (names, labels, time of the last message, unread flag; never a message) and sends it to
  // the web app. Returns { ok, count, complete } or { ok: false, error, needsChoice?, login? }. The caller queues it in the
  // post queue under a deadline, so it never overlaps a post.
  async function syncLineChats({ trigger = 'manual', botId: wantedBotId = '' } = {}) {
    const c = await getCloud();
    if (!c.enabled || !c.apiUrl || !c.deviceKey) return { ok: false, error: 'ยังไม่ได้จับคู่กับเว็บ AutoPost' };
    if (!c.lineEnabled) return { ok: false, error: 'ยังไม่ได้เปิดใช้ LINE OA ในเครื่องนี้ (กด "ใช้ LINE OA ในเครื่องนี้" ที่หน้าสถานะของส่วนขยาย)' };
    // A read the web asked for names its OA (the web's LINE OA master); another OA than the last one is never throttled by the last read.
    const otherOa = !!wantedBotId && wantedBotId.toLowerCase() !== String(c.lineBotId || '').toLowerCase();
    const wait = otherOa ? 0 : (c.lineSyncAt || 0) + tuning.lineSyncMinGapMs - Date.now();
    if (wait > 0) {
      return { ok: false, throttled: true, error: `ซิงก์รายชื่อแชท LINE ไปเมื่อสักครู่ ซิงก์ได้ทุก ${Math.round(tuning.lineSyncMinGapMs / 60000)} นาที (รออีกประมาณ ${Math.ceil(wait / 60000)} นาที)` };
    }
    const fail = async (error, extra = {}) => {
      await setCloud({ lineError: error, ...extra });
      await log('warn', `[LINE] ซิงก์รายชื่อแชทไม่สำเร็จ: ${error}`);
      return { ok: false, error, ...extra };
    };

    const probe = await probeLineLogin();
    if (probe.login) {
      await setCloud({ lineLoginLostAt: Date.now(), lineLoginProbeAt: Date.now() + tuning.loginProbeMs });
      return fail('ยังไม่ได้ล็อกอิน LINE OA Manager ใน Chrome นี้', { login: true });
    }
    if (wantedBotId) {
      probe.botId = wantedBotId;
      probe.oaName = otherOa ? '' : probe.oaName;
    }
    if (!probe.botId) {
      if (probe.choices.length > 1) {
        await setCloud({ lineChoices: probe.choices, lineError: '' });
        return { ok: false, needsChoice: true, error: 'พบ LINE OA หลายบัญชี: เลือกบัญชีที่หน้าสถานะของส่วนขยาย' };
      }
      return fail('ไม่พบ LINE OA ในบัญชีที่ล็อกอินอยู่');
    }
    // The 10-minute floor counts real reads of the list (a login that was missing does not use it up).
    await setCloud({ lineBotId: probe.botId, lineOaName: probe.oaName || (otherOa ? '' : c.lineOaName) || '', lineLoginLostAt: 0, lineChoices: [], lineError: '', lineSyncAt: Date.now() });

    // The web must know the OA before it takes the list: the heartbeat creates the account (PUT line/chats refuses without it).
    let hb;
    try {
      hb = await heartbeat();
    } catch (e) {
      return fail(e?.message || String(e));
    }
    if (hb.lineNote) {
      await setCloud({ lineNote: hb.lineNote });
      return fail(hb.lineNote);
    }
    if (!hb.lineAccountId) return fail('เว็บยังไม่ได้ผูกบัญชี LINE OA กับเครื่องนี้ (แพ็กเกจของเจ้าของเวิร์กสเปซอาจยังไม่รวมแชท LINE)');
    await setCloud({ lineNote: '' });

    // The contacts page lists every customer once (name, tag, last chat date) and is read in one go; when it gives nothing,
    // the chat list of the OA is scrolled instead (the way it was read before).
    let r = null;
    try {
      r = await readContactsRoster(probe.botId);
    } catch (e) {
      if (e instanceof Aborted || e instanceof Fatal || e instanceof Blocked) throw e;
      await log('warn', `[LINE] อ่านหน้ารายชื่อติดต่อไม่สำเร็จ จะอ่านจากรายชื่อแชทแทน: ${e?.message || e}`);
    }
    if (!r) {
      const tabId = await ensureLineTab(probe.botId, false);
      await injectLine(tabId);
      await waitLinePage(tabId);
      let c = await tabCmdOk(tabId, 'lineListChats', { reset: true, rounds: TIMING.syncBurstRounds }, 180000);
      while (!c.done) {
        abortCheck();
        await sleep(rand(...TIMING.syncBurstPause));
        c = await tabCmdOk(tabId, 'lineListChats', { rounds: TIMING.syncBurstRounds }, 180000);
      }
      r = {
        chats: (c.chats || []).slice(0, 5000).map((x) => ({
          userId: x.userId,
          name: x.name || '',
          labels: x.labels || [],
          lastMessageAt: x.lastMessageAt || undefined,
          unread: !!x.unread,
        })),
        complete: !!c.complete && (c.chats || []).length === c.count,
        note: c.note || '',
      };
    }
    const chats = r.chats;
    if (r.note) await log('info', `[LINE] ${r.note}`);
    let serverTotal;
    try {
      serverTotal = await cloudApi(await getCloud(), 'PUT', '/api/device/line/chats', {
        botId: probe.botId,
        oaName: probe.oaName || (otherOa ? '' : c.lineOaName) || probe.botId,
        complete: !!r.complete,
        chats,
      });
    } catch (e) {
      return fail(e?.message || String(e));
    }
    const next = Date.now() + TIMING.syncEveryMs + rand(-TIMING.syncJitterMs, TIMING.syncJitterMs);
    // The count shown is what was sent: the web's own answer (the total it holds) is kept for the caller, but it is not what the
    // person asked about and has been seen to lag one sync behind.
    await setCloud({ lineChats: chats.length, lineSyncedAt: Date.now(), lineSyncNextAt: next, lineError: '' });
    await log('success', `[LINE] ซิงก์รายชื่อแชท LINE ขึ้นเว็บแล้ว ${chats.length} แชท${r.complete ? '' : ' (อ่านได้ไม่ครบทั้งรายการ จึงยังไม่ลบแชทที่หายไป)'}`);
    return { ok: true, count: chats.length, serverTotal, complete: !!r.complete, trigger };
  }

  return { sendToLineChat, syncLineChats, probeLineLogin, TIMING };
}
