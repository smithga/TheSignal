import { createRenderer, DevelopmentRenderer, PerformanceQueue } from './actor-performance-renderers.js?v=localvoice-1';

const byId = (id) => document.getElementById(id);
const elements = {
  startButton: byId('startButton'), stopButton: byId('stopButton'), status: byId('status'),
  videoPlaceholder: byId('videoPlaceholder'), directorToggle: byId('directorToggle'),
  directorPanel: byId('directorPanel'), sceneList: byId('sceneList'),
  signalLostOverlay: byId('signalLostOverlay'), clockDisplay: byId('clockDisplay'),
  clockStartButton: byId('clockStartButton'), clockPauseButton: byId('clockPauseButton'),
  clockResetButton: byId('clockResetButton'), assignRolesButton: byId('assignRolesButton'),
  rolesSummary: byId('rolesSummary'), rendererMode: byId('rendererMode'),
  paidServicesWarning: byId('paidServicesWarning'), developmentStage: byId('developmentStage'),
  playerInputForm: byId('playerInputForm'), playerInputText: byId('playerInputText'),
  sendPlayerInputButton: byId('sendPlayerInputButton'),
  performanceActor: byId('performanceActor'), performanceText: byId('performanceText'),
  performanceDirection: byId('performanceDirection'), performanceSource: byId('performanceSource'),
  performanceHistory: byId('performanceHistory'), completionMode: byId('completionMode'),
  completePerformanceButton: byId('completePerformanceButton'),
  voiceInputPanel: byId('voiceInputPanel'), voiceInputButton: byId('voiceInputButton'),
  voiceInputStatus: byId('voiceInputStatus')
};

let scenes = [];
let actors = {};
let session = null;
let renderer = null;
let performanceQueue = null;
let clockIntervalId = null;
let clockBusy = false;
let playerInputSubmitting = false;
let localBridgeEndpoint = null;
let mediaRecorder = null;
let voiceChunks = [];
let voiceBusy = false;
const sceneElements = new Map();

function setStatus(text) { elements.status.textContent = text; }

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) throw new Error(`Request to ${url} failed (${response.status}): ${await response.text()}`);
  if (response.status === 204) return null;
  return response.json();
}

function formatClock(totalSeconds) {
  const minutes = Math.floor(totalSeconds / 60).toString().padStart(2, '0');
  const seconds = Math.floor(totalSeconds % 60).toString().padStart(2, '0');
  return `${minutes}:${seconds}`;
}

function elapsedSeconds(value) {
  if (typeof value === 'number') return value;
  const parts = String(value ?? '0').split(':').map(Number);
  return parts.length === 3 ? (parts[0] * 3600) + (parts[1] * 60) + parts[2] : 0;
}

function describeTrigger(scene) {
  const trigger = scene.trigger;
  if (trigger.type === 'timestamp') return formatClock(trigger.at ?? 0);
  if (trigger.type === 'after') return `after "${trigger.after}" +${trigger.wait ?? 0}s`;
  return 'manual only';
}

function setDirectorControlsEnabled(enabled) {
  elements.sceneList.querySelectorAll('button').forEach((button) => { button.disabled = !enabled; });
  elements.clockStartButton.disabled = !enabled || Boolean(session?.isClockRunning);
  elements.clockPauseButton.disabled = !enabled || !session?.isClockRunning;
  elements.clockResetButton.disabled = !enabled;
  elements.assignRolesButton.disabled = !enabled;
  elements.playerInputForm.hidden = !['development', 'local'].includes(elements.rendererMode.value);
  updatePlayerInputControls();
}

function updatePlayerInputControls() {
  const mode = elements.rendererMode.value;
  const isPerforming = Boolean(session?.currentSceneId) ||
    (session?.pendingUtterances?.length ?? 0) > 0;
  const enabled = ['development', 'local'].includes(mode) && Boolean(renderer) &&
    !isPerforming && !playerInputSubmitting && !voiceBusy;
  elements.playerInputText.disabled = !enabled;
  elements.sendPlayerInputButton.disabled = !enabled;
  elements.voiceInputPanel.hidden = mode !== 'local' || !renderer;
  elements.voiceInputButton.disabled = !enabled || !localBridgeEndpoint;
}

function updateSession(nextSession) {
  if (!nextSession) return;
  session = nextSession;
  elements.clockDisplay.textContent = formatClock(elapsedSeconds(session.elapsedTime));
  for (const [sceneId, state] of Object.entries(session.scenes ?? {})) {
    const sceneEl = sceneElements.get(sceneId);
    if (sceneEl) sceneEl.dataset.state = state.status;
  }
  setDirectorControlsEnabled(Boolean(renderer));
}

function rendererElements() {
  return {
    stage: elements.developmentStage, actorName: elements.performanceActor,
    text: elements.performanceText, direction: elements.performanceDirection,
    source: elements.performanceSource, history: elements.performanceHistory,
    completionMode: elements.completionMode
  };
}

async function acknowledgeUtterance(utterance) {
  const result = await fetchJson(`/api/game/sessions/${session.id}/utterances/${utterance.id}/complete`, { method: 'POST' });
  updateSession(result.session);
  await handleEvents(result.events);
}

async function failPerformance(utterance, error) {
  console.error(`Performance ${utterance.id} failed`, error);
  setStatus(`Performance failed: ${error.message}`);
  const result = await fetchJson(`/api/game/sessions/${session.id}/utterances/${utterance.id}/fail`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ error: error.message })
  });
  updateSession(result.session);
}

async function handleEvents(events = []) {
  for (const event of events) {
    if (event.type === 'visualEffectRequested' && event.effect === 'signal-lost') {
      elements.signalLostOverlay.hidden = false;
      const result = await fetchJson(`/api/game/sessions/${session.id}/scenes/${event.sceneId}/complete`, { method: 'POST' });
      updateSession(result.session);
      await handleEvents(result.events);
    }
    if (event.type === 'sceneStarted') {
      const result = await fetchJson(`/api/game/sessions/${session.id}/scenes/active/prepare`, { method: 'POST' });
      updateSession(result.session);
      await handleEvents(result.events);
    }
    if ((event.type === 'utteranceReady' || event.type === 'nonSpokenDeliveryReady') && event.utterance) {
      performanceQueue.enqueue(event.utterance);
    }
  }
}

async function runCommand(url, options = { method: 'POST' }) {
  const result = await fetchJson(url, options);
  updateSession(result.session);
  await handleEvents(result.events);
  return result;
}

async function assignRoles() {
  const assignments = await fetchJson(`/api/game/sessions/${session.id}/roles/assign`, { method: 'POST' });
  elements.rolesSummary.textContent = assignments.map((assignment) =>
    `${assignment.roleId}=${assignment.player.firstName}`).join(', ');
}

async function triggerScene(scene) {
  elements.signalLostOverlay.hidden = true;
  await runCommand(`/api/game/sessions/${session.id}/scenes/${scene.id}/trigger`);
}

async function triggerLine(scene, lineIndex) {
  await runCommand(`/api/game/sessions/${session.id}/scenes/${scene.id}/lines/${lineIndex}/trigger`);
}

function renderScenes() {
  elements.sceneList.innerHTML = '';
  sceneElements.clear();
  for (const scene of scenes) {
    const sceneEl = document.createElement('div');
    sceneEl.className = 'scene';
    sceneElements.set(scene.id, sceneEl);
    const meta = document.createElement('div');
    meta.className = 'scene-meta';
    const title = document.createElement('h3');
    title.textContent = scene.title;
    const time = document.createElement('span');
    time.className = 'scene-time';
    time.textContent = describeTrigger(scene);
    meta.append(title, time);
    sceneEl.appendChild(meta);

    const playButton = document.createElement('button');
    playButton.className = 'scene-play-button';
    playButton.textContent = scene.kind === 'signal-lost' ? 'Cut Signal Now' : 'Play Scene Now';
    playButton.disabled = !renderer;
    playButton.addEventListener('click', () => triggerScene(scene));
    sceneEl.appendChild(playButton);

    if (scene.kind !== 'signal-lost') {
      const contextButton = document.createElement('button');
      contextButton.className = 'scene-context-button';
      contextButton.textContent = 'Set Context (silent)';
      contextButton.disabled = !renderer;
      contextButton.addEventListener('click', () => runCommand(`/api/game/sessions/${session.id}/scenes/${scene.id}/context`));
      sceneEl.appendChild(contextButton);
      scene.dialog.forEach((line, lineIndex) => {
        const button = document.createElement('button');
        const isText = actors[line.actor]?.channel === 'textMessage';
        button.className = `scene-line-button${isText ? ' scene-line-textmessage' : ''}${line.mode === 'improvise' ? ' scene-line-improvise' : ''}`;
        button.textContent = `${isText ? '📱 ' : ''}${line.actor}: ${line.mode === 'improvise' ? '💭 ' : ''}${line.text}`;
        button.title = 'Resolve through the game engine, then perform the finalized utterance.';
        button.disabled = !renderer;
        button.addEventListener('click', () => triggerLine(scene, lineIndex));
        sceneEl.appendChild(button);
      });
    }
    elements.sceneList.appendChild(sceneEl);
  }
}

async function loadContent() {
  const [loadedScenes, loadedActors] = await Promise.all([fetchJson('/api/game/scenes'), fetchJson('/api/game/actors')]);
  scenes = loadedScenes;
  actors = Object.fromEntries(loadedActors.map((actor) => [actor.id, actor]));
  renderScenes();
}

async function startPerformance() {
  elements.startButton.disabled = true;
  elements.rendererMode.disabled = true;
  setStatus('Starting...');
  try {
    if (!session) {
      session = await fetchJson('/api/game/sessions', { method: 'POST' });
      await runCommand(`/api/game/sessions/${session.id}/initialize`);
      await assignRoles();
    }
    localBridgeEndpoint = null;
    let localDialogModel = null;
    if (elements.rendererMode.value === 'local') {
      const localConfig = await fetchJson('/api/local/configuration');
      if (!localConfig.configured) {
        throw new Error('Local voice mode needs LOCAL__BridgeEndpoint in .env, and the bridge running: tools/voice-bridge/service.py');
      }
      localBridgeEndpoint = localConfig.bridgeEndpoint;
      localDialogModel = localConfig.dialogModel;
    }
    renderer = createRenderer(elements.rendererMode.value, rendererElements(), { localEndpoint: localBridgeEndpoint });
    performanceQueue = new PerformanceQueue(renderer, { onComplete: acknowledgeUtterance, onError: failPerformance });
    await renderer.start();
    elements.videoPlaceholder.hidden = ['anam', 'local'].includes(elements.rendererMode.value);
    elements.developmentStage.hidden = !['development', 'local'].includes(elements.rendererMode.value);
    elements.stopButton.disabled = false;
    setDirectorControlsEnabled(true);
    if (elements.rendererMode.value === 'local') {
      const model = localDialogModel ? `local dialog model: ${localDialogModel}` : 'improvise stub (no local LLM configured)';
      setStatus(`Local voice ready (free — ${model})`);
    } else {
      setStatus(elements.rendererMode.value === 'anam' ? 'Anam ready (paid service active)' : 'Development renderer ready (no Anam)');
    }
  } catch (error) {
    console.error(error);
    renderer = null;
    performanceQueue = null;
    elements.startButton.disabled = false;
    elements.rendererMode.disabled = false;
    setStatus(`Failed to start: ${error.message}`);
  }
}

async function stopPerformance() {
  elements.stopButton.disabled = true;
  clearInterval(clockIntervalId);
  clockIntervalId = null;
  performanceQueue?.cancel();
  try { await renderer?.stop(); } catch (error) { console.error(error); }
  renderer = null;
  performanceQueue = null;
  localBridgeEndpoint = null;
  mediaRecorder = null;
  voiceBusy = false;
  elements.voiceInputStatus.textContent = '';
  delete elements.voiceInputButton.dataset.recording;
  elements.videoPlaceholder.hidden = false;
  elements.developmentStage.hidden = true;
  elements.signalLostOverlay.hidden = true;
  elements.startButton.disabled = false;
  elements.rendererMode.disabled = false;
  setDirectorControlsEnabled(false);
  setStatus('Idle');
}

async function tickClock() {
  if (clockBusy || !session?.isClockRunning) return;
  clockBusy = true;
  try {
    await runCommand(`/api/game/sessions/${session.id}/advance`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ seconds: 1 })
    });
  } finally { clockBusy = false; }
}

async function startClock() {
  await runCommand(`/api/game/sessions/${session.id}/start`);
  clearInterval(clockIntervalId);
  clockIntervalId = setInterval(tickClock, 1000);
}

async function pauseClock() {
  clearInterval(clockIntervalId);
  clockIntervalId = null;
  await runCommand(`/api/game/sessions/${session.id}/pause`);
}

async function resetGame() {
  clearInterval(clockIntervalId);
  clockIntervalId = null;
  performanceQueue?.cancel();
  session = await fetchJson(`/api/game/sessions/${session.id}/reset`, { method: 'POST' });
  await runCommand(`/api/game/sessions/${session.id}/initialize`);
  await assignRoles();
  elements.signalLostOverlay.hidden = true;
  elements.performanceHistory.innerHTML = '';
  updateSession(session);
}

async function submitPlayerInput(text) {
  if (!text || !session || playerInputSubmitting) return;

  playerInputSubmitting = true;
  updatePlayerInputControls();
  try {
    await runCommand(`/api/game/sessions/${session.id}/player-input`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text, playerId: null })
    });
    elements.playerInputText.value = '';
  } catch (error) {
    setStatus(`Player input failed: ${error.message}`);
  } finally {
    playerInputSubmitting = false;
    updatePlayerInputControls();
  }
}

function sendPlayerInput(event) {
  event.preventDefault();
  submitPlayerInput(elements.playerInputText.value.trim());
}

// Push-to-talk room input: record while held, whisper.cpp transcribes locally,
// the transcript goes straight into the existing typed player-input path.
// The engine's 409 gating (no input during a performance) applies unchanged.
function pickRecordingMime() {
  for (const mime of ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4']) {
    if (typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported(mime)) return { mimeType: mime };
  }
  return {};
}

async function voiceStart() {
  if (voiceBusy || !localBridgeEndpoint) return;
  voiceBusy = true;
  updatePlayerInputControls();
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    voiceChunks = [];
    mediaRecorder = new MediaRecorder(stream, pickRecordingMime());
    mediaRecorder.onstop = voiceTranscribe;
    mediaRecorder.start();
    elements.voiceInputButton.dataset.recording = 'true';
    elements.voiceInputStatus.textContent = 'Listening… release to send.';
  } catch (error) {
    voiceBusy = false;
    elements.voiceInputStatus.textContent = `Mic unavailable: ${error.message}`;
    updatePlayerInputControls();
  }
}

function voiceStop() {
  if (mediaRecorder?.state === 'recording') mediaRecorder.stop();
}

async function voiceTranscribe() {
  mediaRecorder?.stream.getTracks().forEach((track) => track.stop());
  delete elements.voiceInputButton.dataset.recording;
  const blob = new Blob(voiceChunks, { type: mediaRecorder?.mimeType || 'audio/webm' });
  voiceChunks = [];
  elements.voiceInputStatus.textContent = 'Transcribing…';
  try {
    const form = new FormData();
    form.append('file', blob, 'recording.webm');
    const response = await fetch(`${localBridgeEndpoint}/stt`, { method: 'POST', body: form });
    if (!response.ok) throw new Error(await response.text() || `Voice bridge failed (${response.status}).`);
    const { text } = await response.json();
    if (text?.trim()) {
      elements.voiceInputStatus.textContent = `Heard: "${text.trim().slice(0, 80)}"`;
      await submitPlayerInput(text.trim());
    } else {
      elements.voiceInputStatus.textContent = 'Heard nothing — try again.';
    }
  } catch (error) {
    elements.voiceInputStatus.textContent = `Voice input failed: ${error.message}`;
  } finally {
    mediaRecorder = null;
    voiceBusy = false;
    updatePlayerInputControls();
  }
}

elements.startButton.addEventListener('click', startPerformance);
elements.stopButton.addEventListener('click', stopPerformance);
elements.playerInputForm.addEventListener('submit', sendPlayerInput);
elements.assignRolesButton.addEventListener('click', assignRoles);
elements.clockStartButton.addEventListener('click', startClock);
elements.clockPauseButton.addEventListener('click', pauseClock);
elements.clockResetButton.addEventListener('click', resetGame);
elements.completePerformanceButton.addEventListener('click', () => {
  if (renderer instanceof DevelopmentRenderer) renderer.completeCurrent();
});
elements.rendererMode.addEventListener('change', () => {
  elements.paidServicesWarning.hidden = elements.rendererMode.value !== 'anam';
});
elements.voiceInputButton.addEventListener('pointerdown', voiceStart);
elements.voiceInputButton.addEventListener('pointerup', voiceStop);
elements.voiceInputButton.addEventListener('pointercancel', voiceStop);
elements.voiceInputButton.addEventListener('pointerleave', voiceStop);
elements.directorToggle.addEventListener('click', () => { elements.directorPanel.hidden = !elements.directorPanel.hidden; });

setDirectorControlsEnabled(false);
loadContent().catch((error) => {
  console.error(error);
  setStatus(`Content failed to load: ${error.message}`);
});
