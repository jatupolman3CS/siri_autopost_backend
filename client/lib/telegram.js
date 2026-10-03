// Minimal Telegram Bot API client (sendMessage / sendPhoto / getUpdates).

export const escHtml = (s) =>
  String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

async function call(token, method, body, timeoutMs = 20000) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await fetch(`https://api.telegram.org/bot${token}/${method}`, {
      method: 'POST',
      body,
      headers: body instanceof FormData ? undefined : { 'content-type': 'application/json' },
      signal: ctrl.signal,
    });
    let data = null;
    try {
      data = await res.json();
    } catch {
      data = null;
    }
    if (!data || !data.ok) throw new Error((data && data.description) || `HTTP ${res.status}`);
    return data.result;
  } catch (e) {
    if (e.name === 'AbortError') throw new Error('Telegram ไม่ตอบสนอง (timeout)');
    throw e;
  } finally {
    clearTimeout(timer);
  }
}

// Cut long messages at a line break so no HTML tag/entity is split.
function fit(html, max) {
  if (html.length <= max) return html;
  const cut = html.lastIndexOf('\n', max - 10);
  return html.slice(0, cut > 0 ? cut : max - 10) + '\n…';
}

export function sendText(token, chatId, html) {
  return call(
    token,
    'sendMessage',
    JSON.stringify({ chat_id: chatId, text: fit(html, 4000), parse_mode: 'HTML', disable_web_page_preview: true })
  );
}

// Captions are limited to 1024 chars: longer text goes in a separate message.
export async function sendPhoto(token, chatId, blob, html) {
  const fd = new FormData();
  fd.append('chat_id', chatId);
  fd.append('photo', blob, 'screenshot.jpg');
  if (html && html.length <= 1000) {
    fd.append('caption', html);
    fd.append('parse_mode', 'HTML');
    return call(token, 'sendPhoto', fd, 60000);
  }
  await call(token, 'sendPhoto', fd, 60000);
  if (html) await sendText(token, chatId, html);
}

// Chats that recently messaged the bot (to help find the Chat ID).
export async function findChats(token) {
  const updates = await call(token, 'getUpdates', JSON.stringify({ limit: 100 }));
  const chats = new Map();
  for (const u of updates || []) {
    const chat =
      (u.message && u.message.chat) ||
      (u.channel_post && u.channel_post.chat) ||
      (u.my_chat_member && u.my_chat_member.chat) ||
      (u.edited_message && u.edited_message.chat);
    if (!chat) continue;
    const name = chat.title || [chat.first_name, chat.last_name].filter(Boolean).join(' ') || chat.username || '';
    chats.set(String(chat.id), { id: String(chat.id), name, type: chat.type });
  }
  return [...chats.values()];
}

export async function dataUrlToBlob(dataUrl) {
  const res = await fetch(dataUrl);
  return res.blob();
}
