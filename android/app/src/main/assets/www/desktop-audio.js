import { $, api, bridge, PREVIEW } from './main.js';

const open = $('desktopAudioOpen');
const dialog = $('desktopAudio');
const status = $('desktopAudioStatus');
const connect = $('desktopAudioConnect');
const close = $('desktopAudioClose');

async function refreshAvailability() {
  try {
    open.hidden = !(await api('/desktop-audio')).enabled;
  } catch (_) { /* Keep the button available during brief network interruptions. */ }
}

function render(state) {
  const connected = state === 'waiting' || state === 'active';
  if (state === 'waiting') status.textContent = '已连接音频，等待 Mac 上的 Codex 语音。';
  else if (state === 'active') status.textContent = '通话中，正在使用手机麦克风和扬声器。';
  else if (state === 'off') status.textContent = '保持 PhoneBox 在前台，然后在 Mac 上开始语音对话。';
  else status.textContent = state;
  connect.hidden = connected;
  connect.disabled = false;
  close.textContent = connected ? '断开' : '关闭';
}

function start() {
  status.textContent = '正在连接，请允许麦克风访问。';
  connect.disabled = true;
  bridge.desktopAudioStart();
}

if (!PREVIEW && bridge?.desktopAudioStart) {
  window.onDesktopAudioStatus = render;
  open.addEventListener('click', () => {
    render('off');
    dialog.showModal();
    start();
  });
  connect.addEventListener('click', () => {
    bridge.desktopAudioStop();
    start();
  });
  close.addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => bridge.desktopAudioStop());
  window.addEventListener('pagehide', () => bridge.desktopAudioStop());
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) refreshAvailability();
  });
  setInterval(() => { if (!document.hidden && open.hidden) refreshAvailability(); }, 8000);
  refreshAvailability();
}
