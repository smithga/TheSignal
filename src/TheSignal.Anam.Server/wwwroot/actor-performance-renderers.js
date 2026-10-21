const ANAM_SDK_URL = 'https://esm.sh/@anam-ai/js-sdk@4.27.1';
const DEFAULT_TIMEOUT_MS = 20_000;

export class ActorPerformanceRenderer {
  async start() {}

  async perform(_utterance, _options = {}) {
    throw new Error('Renderer must implement perform().');
  }

  cancel() {}

  async stop() {}
}

export class PerformanceQueue {
  constructor(renderer, { onComplete, onError } = {}) {
    this.renderer = renderer;
    this.onComplete = onComplete;
    this.onError = onError;
    this.tail = Promise.resolve();
    this.generation = 0;
  }

  enqueue(utterance) {
    const generation = this.generation;
    const work = this.tail.then(async () => {
      if (generation !== this.generation) return;
      try {
        const result = await this.renderer.perform(utterance);
        if (generation === this.generation) await this.onComplete?.(utterance, result);
      } catch (error) {
        if (generation === this.generation) await this.onError?.(utterance, error);
      }
    });
    this.tail = work.catch(() => {});
    return work;
  }

  cancel() {
    this.generation += 1;
    this.renderer.cancel();
    this.tail = Promise.resolve();
  }
}

export class DevelopmentRenderer extends ActorPerformanceRenderer {
  constructor({ stage, actorName, text, direction, source, history, completionMode }) {
    super();
    this.elements = { stage, actorName, text, direction, source, history, completionMode };
    this.pendingFinish = null;
  }

  async start() {
    this.elements.stage.hidden = false;
  }

  async perform(utterance) {
    const { completionMode } = this.elements;
    renderDevelopmentStage(this.elements, utterance);

    if (completionMode.value === 'manual') {
      return new Promise((resolve) => {
        this.pendingFinish = () => {
          this.pendingFinish = null;
          resolve({ utteranceId: utterance.id, reason: 'director-completed' });
        };
      });
    }

    return { utteranceId: utterance.id, reason: 'immediate' };
  }

  completeCurrent() {
    this.pendingFinish?.();
  }

  cancel() {
    this.pendingFinish?.();
    this.pendingFinish = null;
  }

  async stop() {
    this.cancel();
  }
}

// Free local rehearsal renderer: the engine's finalized utterance text goes to the
// local Piper bridge (tools/voice-bridge), plays through the browser, and the
// utterance is acknowledged when the audio ends — so scene pacing is real speech
// pacing, with no paid provider and no network egress.
export class LocalVoiceRenderer extends ActorPerformanceRenderer {
  constructor({ endpoint, elements, timeoutMs = 60_000 }) {
    super();
    this.endpoint = endpoint.replace(/\/+$/, '');
    this.elements = elements;
    this.timeoutMs = timeoutMs;
    this.audio = null;
    this.pendingFinish = null;
  }

  async start() {
    const response = await fetch(`${this.endpoint}/health`);
    if (!response.ok) {
      throw new Error(`Local voice bridge not reachable at ${this.endpoint}. Run tools/voice-bridge/service.py.`);
    }
    const health = await response.json().catch(() => ({}));
    if (health.piper === false) {
      throw new Error('The voice bridge has no Piper voice configured (set PIPER_MODEL when starting it).');
    }
    this.elements.stage.hidden = false;
  }

  async perform(utterance) {
    renderDevelopmentStage(this.elements, utterance);
    if (utterance.channel !== 'spokenAvatar') {
      return { utteranceId: utterance.id, reason: 'non-spoken' };
    }

    return new Promise(async (resolve, reject) => {
      let settled = false;
      let timer;
      const finish = (reason) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        this.pendingFinish = null;
        resolve({ utteranceId: utterance.id, reason });
      };
      this.pendingFinish = () => {
        this.audio?.pause();
        finish('cancelled');
      };
      timer = setTimeout(() => finish('timeout'), this.timeoutMs);
      try {
        const response = await fetch(`${this.endpoint}/tts`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ text: utterance.text })
        });
        if (!response.ok) throw new Error(await response.text() || `Voice bridge failed (${response.status}).`);
        const audio = new Audio(URL.createObjectURL(await response.blob()));
        this.audio = audio;
        audio.addEventListener('ended', () => finish('audio-ended'));
        audio.addEventListener('error', () => finish('audio-error'));
        await audio.play();
      } catch (error) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        this.pendingFinish = null;
        reject(error);
      }
    });
  }

  cancel() {
    this.pendingFinish?.();
  }

  async stop() {
    this.cancel();
    this.audio?.pause();
    this.audio = null;
  }
}

export class AnamRenderer extends ActorPerformanceRenderer {
  constructor({ videoElementId, timeoutMs = DEFAULT_TIMEOUT_MS }) {
    super();
    this.videoElementId = videoElementId;
    this.timeoutMs = timeoutMs;
    this.client = null;
    this.anamEvent = null;
    this.pendingFinish = null;
  }

  async start() {
    const configuration = await fetchJson('/api/anam/configuration');
    if (!configuration.configured) {
      throw new Error('Anam is not configured. Add ANAM__ApiKey and ANAM__PersonaId or ANAM__AvatarId to the repository .env file.');
    }
    const [{ createClient, AnamEvent }, tokenResponse] = await Promise.all([
      import(ANAM_SDK_URL),
      fetchJson('/api/anam/session-token', { method: 'POST' })
    ]);
    this.client = createClient(tokenResponse.sessionToken);
    this.anamEvent = AnamEvent;
    this.client.addListener(AnamEvent.MESSAGE_STREAM_EVENT_RECEIVED, (event) => {
      if (event.role === 'persona' && event.endOfSpeech) this.pendingFinish?.('speech-ended');
    });
    await this.client.streamToVideoElement(this.videoElementId);
  }

  async perform(utterance) {
    if (!this.client) throw new Error('Anam renderer has not been started.');
    if (utterance.channel !== 'spokenAvatar') {
      return { utteranceId: utterance.id, reason: 'non-spoken' };
    }

    this.applySupportedDirection(utterance.direction);
    return new Promise(async (resolve, reject) => {
      let timer;
      const finish = (reason) => {
        clearTimeout(timer);
        this.pendingFinish = null;
        resolve({ utteranceId: utterance.id, reason });
      };
      this.pendingFinish = finish;
      timer = setTimeout(() => finish('timeout'), this.timeoutMs);
      try {
        await this.client.talk(utterance.text);
      } catch (error) {
        clearTimeout(timer);
        this.pendingFinish = null;
        reject(error);
      }
    });
  }

  applySupportedDirection(direction) {
    const cue = direction?.emotion?.trim().toLowerCase();
    const supported = new Set(['warm', 'surprised', 'concerned', 'curious', 'laughter']);
    if (!cue || !supported.has(cue) || typeof this.client?.sendDirectorNoteCue !== 'function') return;
    try {
      this.client.sendDirectorNoteCue(cue, { atSeconds: 0 });
    } catch (error) {
      console.warn(`Anam ignored performance cue "${cue}".`, error);
    }
  }

  cancel() {
    this.pendingFinish?.('cancelled');
    this.pendingFinish = null;
  }

  async stop() {
    this.cancel();
    if (this.client) await this.client.stopStreaming();
    this.client = null;
  }
}

export function createRenderer(mode, elements, { localEndpoint } = {}) {
  if (mode === 'anam') return new AnamRenderer({ videoElementId: 'persona-video' });
  if (mode === 'local') {
    if (!localEndpoint) throw new Error('Local voice renderer needs a configured bridge endpoint (LOCAL__BridgeEndpoint).');
    return new LocalVoiceRenderer({ endpoint: localEndpoint, elements });
  }
  return new DevelopmentRenderer(elements);
}

function renderDevelopmentStage({ stage, actorName, text, direction, source, history }, utterance) {
  stage.dataset.channel = utterance.channel;
  actorName.textContent = utterance.actorId;
  text.textContent = utterance.text;
  direction.textContent = formatDirection(utterance.direction) || 'No performance direction';
  source.textContent = [utterance.sceneId, utterance.dialogLineId].filter(Boolean).join(' · ');

  const entry = document.createElement('li');
  entry.dataset.channel = utterance.channel;
  const who = document.createElement('strong');
  who.textContent = `${utterance.actorId}: `;
  entry.append(who, document.createTextNode(utterance.text));
  history.prepend(entry);
}

function formatDirection(direction) {
  if (!direction) return '';
  return Object.entries(direction)
    .filter(([, value]) => value)
    .map(([key, value]) => `${key}: ${value}`)
    .join(' · ');
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) throw new Error(await response.text() || `${response.status} ${response.statusText}`);
  return response.json();
}
