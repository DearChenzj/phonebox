import { bridge, curPage, goPage, h, haptic, isFullTrackpadPage, pages } from '../main.js';
import { W } from '../registry.js';
import { speechSessions } from '../speech-sessions.js';

export const ROBOT_PAGE = {
  id: '__phonebox_robot', title: '机器人', icon: '🤖',
  widgets: [{ id: 'face', type: 'robot-face', x: 0, y: 0, w: 12, h: 6 }],
};

const hint = '点一下眼睛，说「笑一个」「睡觉」或「打开仪表盘」';
const commands = new Map([
  ['笑一个', 'happy'], ['笑一笑', 'happy'], ['开心一点', 'happy'],
  ['睡觉', 'sleep'], ['晚安', 'sleep'],
  ['醒醒', 'idle'], ['起床', 'idle'], ['你好', 'idle'],
  ['打开仪表盘', 'dashboard'], ['打开仪表板', 'dashboard'],
]);
const notifications = {
  ok: ['happy', '收到成功通知'],
  warn: ['warn', '收到警告通知'],
  err: ['error', '收到错误通知'],
  '': ['alert', '收到新通知'],
};

W['robot-face'] = {
  bare: true,
  needs: ['tick'],
  create(box) {
    box.lang = 'zh-CN';
    const eyes = h('span', 'robot-eyes', h('span', 'robot-eye'), h('span', 'robot-eye'));
    eyes.setAttribute('aria-hidden', 'true');
    const face = h('button', 'robot-face', eyes);
    face.type = 'button';
    const status = h('div', 'robot-status', hint);
    status.setAttribute('role', 'status');
    const dashboard = h('button', 'robot-dashboard', '仪表盘');
    dashboard.type = 'button';
    const actions = h('div', 'robot-actions', dashboard);
    const trackpadIndex = pages.findIndex(p => isFullTrackpadPage(p) && p.widgets[0].options?.control === true);
    if (trackpadIndex >= 0) {
      const trackpad = h('button', 'robot-dashboard', '触控板');
      trackpad.type = 'button';
      trackpad.onclick = () => { cancel(); goPage(trackpadIndex, true); };
      actions.append(trackpad);
    }
    box.append(face, status, actions);

    let rest = 'idle';
    let session = 0;
    let reactionTimer = 0;
    let watch = 0;
    function show(expression, message = hint) {
      face.dataset.expression = expression;
      face.setAttribute('aria-label', session ? '取消语音指令' : '点按说出中文指令');
      status.textContent = message;
    }
    function react(expression, message) {
      clearTimeout(reactionTimer);
      show(expression, message);
      reactionTimer = setTimeout(() => show(rest), 4200);
    }
    function finish() {
      speechSessions.delete(session);
      session = 0;
      clearInterval(watch);
    }
    function cancel() {
      if (!session) return;
      bridge.cancelListening(session);
      finish();
      show(rest);
    }
    function guard() {
      if (pages[curPage] !== ROBOT_PAGE || document.body.classList.contains('night')) cancel();
    }
    function openDashboard() {
      cancel();
      goPage(0, true);
    }
    function onSpeech(kind, text) {
      if (kind === 'partial') { show('listening', text || '正在听…'); return; }
      if (kind === 'end') { show('thinking', '正在识别…'); return; }
      if (kind !== 'final' && kind !== 'error') return;
      finish();
      if (kind === 'error') {
        if (text === 'cancelled') show(rest);
        else if (['permission', 'permission-blocked', '9'].includes(text)) react('warn', '请在 Android 设置中允许麦克风权限');
        else react('warn', '没听清，请再试一次');
        return;
      }
      const command = commands.get(text.replace(/[\s。！？，、,.!?]/g, ''));
      if (command === 'dashboard') { show(rest); openDashboard(); }
      else if (command === 'sleep') { rest = 'sleep'; show(rest, '晚安 · 点一下仍可说话'); }
      else if (command === 'idle') { rest = 'idle'; show(rest, '我在'); }
      else if (command === 'happy') react('happy', '😊');
      else react('warn', '可以试试说「笑一个」或「打开仪表盘」');
    }

    face.onclick = () => {
      haptic();
      if (session) { cancel(); return; }
      if (!bridge.canListen?.()) { react('warn', '这台手机没有可用的语音识别服务'); return; }
      session = bridge.listen('zh-CN', false);
      if (!session) { react('warn', '无法开始识别，请再试一次'); return; }
      clearTimeout(reactionTimer);
      speechSessions.set(session, onSpeech);
      show('listening', '正在听…');
      watch = setInterval(guard, 250);
    };
    dashboard.onclick = openDashboard;
    show(rest);

    return {
      notify(level) { if (!session) react(...notifications[level]); },
      update: guard,
      destroy() { cancel(); clearTimeout(reactionTimer); },
    };
  },
};
