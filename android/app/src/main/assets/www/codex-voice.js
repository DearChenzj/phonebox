import { api, bridge, PREVIEW } from './main.js';

const open = document.getElementById('codexVoiceOpen');
const dialog = document.getElementById('codexVoice');
const status = document.getElementById('codexVoiceStatus');
const close = document.getElementById('codexVoiceClose');
const audio = document.getElementById('codexVoiceAudio');
const picker = document.getElementById('codexVoicePicker');
const thread = document.getElementById('codexVoiceThread');
const dial = document.getElementById('codexVoiceDial');
const more = document.getElementById('codexVoiceMore');
const conversations = new Map();
let active;
let refreshing = false;
let cursor = null;
let pickerGeneration = 0;

async function refreshAvailability() {
  if (refreshing) return;
  refreshing = true;
  try { open.hidden = !(await api('/codex-voice')).enabled; }
  catch (_) { /* Keep a previously discovered voice button during a network transition. */ }
  finally { refreshing = false; }
}

function stop() {
  const call = active;
  active = null;
  if (!call) return;
  clearTimeout(call.connectionTimer);
  call.mic?.stop();
  call.peer?.close();
  audio.srcObject = null;
  if (call.offered) bridge.codexVoiceStop(call.id);
}

function fail(call, message) {
  if (active !== call) return;
  stop();
  status.textContent = message;
  close.textContent = '关闭';
}

async function loadConversations() {
  const generation = pickerGeneration;
  more.disabled = true;
  status.textContent = '正在加载已有会话…';
  try {
    const query = cursor ? '?cursor=' + encodeURIComponent(cursor) : '';
    const result = await api('/codex-voice/threads' + query, 'GET', 4500);
    if (generation !== pickerGeneration || active) return;
    for (const conversation of result.threads || []) {
      if (conversations.has(conversation.id)) continue;
      conversations.set(conversation.id, conversation);
      const label = [conversation.title, conversation.project, conversation.busy ? '任务运行中' : '']
        .filter(Boolean).join(' · ');
      const option = new Option(label, conversation.id);
      option.disabled = conversation.busy;
      thread.add(option);
    }
    cursor = result.nextCursor || null;
    more.textContent = '加载更多';
    more.hidden = !cursor;
    status.textContent = conversations.size ? '' : '没有可继续的已保存会话。';
  } catch (_) {
    if (generation !== pickerGeneration || active) return;
    more.textContent = '重试加载';
    more.hidden = false;
    status.textContent = '暂时无法加载会话。';
  } finally {
    if (generation === pickerGeneration) more.disabled = false;
  }
}

function showChooser() {
  if (dialog.open) return;
  pickerGeneration++;
  conversations.clear();
  cursor = null;
  thread.replaceChildren(new Option('新建临时会话', ''));
  more.hidden = true;
  picker.hidden = false;
  close.textContent = '取消';
  dialog.showModal();
  loadConversations();
}

function callId() {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
  return hex.replace(/^(........)(....)(....)(....)(............)$/, '$1-$2-$3-$4-$5');
}

function gatherIce(peer) {
  return new Promise(resolve => {
    if (peer.iceGatheringState === 'complete') { resolve(); return; }
    const timer = setTimeout(done, 1600);
    function done() {
      clearTimeout(timer);
      peer.removeEventListener('icegatheringstatechange', changed);
      resolve();
    }
    function changed() { if (peer.iceGatheringState === 'complete') done(); }
    peer.addEventListener('icegatheringstatechange', changed);
  });
}

async function start() {
  if (active) return;
  const call = { id: callId(), selection: conversations.get(thread.value) };
  active = call;
  picker.hidden = true;
  status.textContent = '请允许麦克风访问。';
  close.textContent = '结束通话';
  try {
    const media = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
    const mic = media.getAudioTracks()[0];
    if (active !== call) { mic?.stop(); return; }
    if (!mic) throw new Error('No microphone was found.');
    call.mic = mic;
    mic.enabled = false;
    status.textContent = '正在连接 Codex…';
    const peer = new RTCPeerConnection();
    call.peer = peer;
    peer.addTrack(mic);
    const events = peer.createDataChannel('oai-events');
    events.onmessage = event => {
      if (active !== call) return;
      try {
        const data = JSON.parse(event.data);
        if (data.type === 'error') fail(call, data.error?.message || 'Codex reported a voice error.');
      } catch (_) {}
    };
    peer.ontrack = event => {
      if (active !== call || event.track.kind !== 'audio') return;
      audio.srcObject = new MediaStream([event.track]);
      audio.play().catch(() => fail(call, 'Could not play Codex audio.'));
    };
    peer.onconnectionstatechange = () => {
      if (active !== call) return;
      if (peer.connectionState === 'connected') {
        clearTimeout(call.connectionTimer);
        mic.enabled = true;
        status.textContent = '已连接，可以说话。';
      } else if (peer.connectionState === 'failed' || peer.connectionState === 'closed') {
        fail(call, 'The Codex voice connection ended.');
      } else if (peer.connectionState === 'disconnected') {
        mic.enabled = false;
        status.textContent = '正在重新连接…';
        clearTimeout(call.connectionTimer);
        call.connectionTimer = setTimeout(() => fail(call, 'The Codex voice connection ended.'), 8000);
      }
    };
    await peer.setLocalDescription(await peer.createOffer());
    await gatherIce(peer);
    if (active !== call) return;
    call.offered = bridge.codexVoiceStart(call.id, peer.localDescription.sdp,
      call.selection?.id || '', call.selection?.ticket || '');
    if (!call.offered) throw new Error('Could not start a Codex voice call.');
    call.connectionTimer = setTimeout(() => fail(call, 'Could not connect to Codex Voice in time.'), 35000);
  } catch (error) {
    fail(call, error.name === 'NotAllowedError' ? 'Microphone permission was declined.' : (error.message || 'Could not start Codex Voice.'));
  }
}

if (!PREVIEW && bridge?.codexVoiceStart && globalThis.RTCPeerConnection && navigator.mediaDevices?.getUserMedia) {
  open.addEventListener('click', showChooser);
  dial.addEventListener('click', start);
  more.addEventListener('click', loadConversations);
  close.addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => { pickerGeneration++; stop(); });
  window.onCodexVoicePause = () => {
    stop();
    if (dialog.open) dialog.close();
  };
  window.onCodexVoiceAnswer = async (id, code, payload) => {
    const call = active;
    if (!call || call.id !== id) return;
    try {
      const result = JSON.parse(payload);
      if (code !== 200 || !result.sdp) throw new Error(result.error || 'Codex did not start the voice call.');
      await call.peer.setRemoteDescription({ type: 'answer', sdp: result.sdp });
    } catch (error) {
      fail(call, error.message || 'Could not connect to Codex Voice.');
    }
  };
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      if (active?.mic) window.onCodexVoicePause();
    } else {
      refreshAvailability();
    }
  });
  window.addEventListener('pagehide', stop);
  setInterval(() => { if (!document.hidden && open.hidden) refreshAvailability(); }, 8000);
  refreshAvailability();
}
