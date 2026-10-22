import { Component, computed, effect, inject, input, linkedSignal, output, signal } from '@angular/core';
import { ApiError } from '../core/api';
import { KeyState, LocalApi, ProviderId, ProviderSaved, ProviderState, ProviderUpdate } from '../core/local-api';
import { ServerStatus } from '../core/server-status';
import { Toasts } from '../core/toasts';

/** Shown in the model picker before the provider has been asked what it has. */
const SUGGESTED: Record<ProviderId, string[]> = {
  anthropic: ['claude-haiku-5-5', 'claude-sonnet-5-5', 'claude-opus-5-5'],
  openai: ['gpt-5.4-mini', 'gpt-5.4-nano'],
  local: [],
};

const KEY_PLACEHOLDER: Record<ProviderId, string> = {
  anthropic: 'sk-ant-…',
  openai: 'sk-…',
  local: 'Only if your server asks for one',
};

const KEY_SOURCES: Record<NonNullable<KeyState['source']>, string> = {
  saved: 'saved on this computer',
  config: "from the app's configuration (appsettings or user-secrets)",
  environment: 'from an environment variable',
};

/** OpenAI lists every model it has (speech, images…); only chat models can answer here. */
const isChatModel = (id: string) =>
  /^(gpt-|o\d|chatgpt)/.test(id) && !/(audio|realtime|transcribe|tts|image|search|embedding|instruct)/.test(id);

/** One provider's key, model and server, checked with the provider before it's saved. */
@Component({
  selector: 'app-provider-form',
  templateUrl: './provider-form.html',
  styleUrl: './provider-form.css',
})
export class ProviderForm {
  readonly provider = input.required<ProviderState>();
  readonly saved = output<ProviderSaved>();

  private readonly api = inject(LocalApi);
  private readonly status = inject(ServerStatus);
  private readonly toasts = inject(Toasts);

  protected readonly keySources = KEY_SOURCES;
  protected readonly apiKey = linkedSignal({ source: () => this.provider().id, computation: () => '' });
  protected readonly model = linkedSignal(() => this.provider().model);
  protected readonly baseUrl = linkedSignal(() => this.provider().baseUrl ?? '');
  /** What the provider said it has, once asked. */
  protected readonly offered = linkedSignal<ProviderId, string[]>({ source: () => this.provider().id, computation: () => [] });
  protected readonly suggestions = computed(() => {
    const id = this.provider().id;
    const offered = id === 'openai' ? this.offered().filter(isChatModel) : this.offered();
    return [...new Set([...offered, ...SUGGESTED[id]])];
  });
  protected readonly keyPlaceholder = computed(() =>
    this.provider().apiKey.set ? 'Paste a new key to replace it' : KEY_PLACEHOLDER[this.provider().id],
  );
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  /** For a local server: whether it answers at the address typed so far. */
  protected readonly server = signal<{ ok: boolean; text: string } | null>(null);

  constructor() {
    // A local server is checked as soon as its tab opens and again whenever the address changes.
    effect((onCleanup) => {
      const url = this.baseUrl().trim();
      if (!this.provider().editableBaseUrl || !url) return;
      const timer = setTimeout(() => void this.lookAtServer(url), 400);
      onCleanup(() => clearTimeout(timer));
    });
  }

  protected async save() {
    const provider = this.provider();
    const update: ProviderUpdate = {};
    if (this.apiKey().trim()) update.apiKey = this.apiKey().trim();
    if (this.model().trim() !== provider.model) update.model = this.model().trim();
    if (provider.editableBaseUrl && this.baseUrl().trim() !== (provider.baseUrl ?? '')) update.baseUrl = this.baseUrl().trim();

    this.busy.set(true);
    this.error.set('');
    try {
      const result = await this.api.saveProvider(provider.id, update);
      this.apiKey.set('');
      if (result.models.length) this.offered.set(result.models);
      this.toasts.show(
        result.verified || !provider.requiresKey
          ? `Answers now come from ${result.provider.model}`
          : `Saved. ${provider.name} couldn't be reached to check the key, though.`,
      );
      await this.status.refresh();
      this.saved.emit(result);
    } catch (error) {
      this.error.set(error instanceof ApiError ? error.message : "Couldn't save that.");
    } finally {
      this.busy.set(false);
    }
  }

  protected async removeKey() {
    try {
      await this.api.removeKey(this.provider().id);
      this.toasts.show('Saved key removed');
      await this.status.refresh();
      this.saved.emit({ provider: this.provider(), verified: false, models: [] });
    } catch (error) {
      this.error.set(error instanceof ApiError ? error.message : "Couldn't remove the key.");
    }
  }

  private async lookAtServer(baseUrl: string) {
    this.server.set({ ok: true, text: 'Looking for the server…' });
    try {
      const models = await this.api.models(this.provider().id, { baseUrl, apiKey: this.apiKey().trim() || undefined });
      if (this.baseUrl().trim() !== baseUrl) return; // the address changed meanwhile
      this.offered.set(models);
      this.server.set({
        ok: true,
        text: models.length
          ? `Connected · ${models.length} ${models.length === 1 ? 'model' : 'models'}`
          : 'Connected, but it has no models yet (for Ollama: ollama pull qwen2.5-coder)',
      });
      if (!this.model() && models.length) this.model.set(models[0]);
    } catch (error) {
      if (this.baseUrl().trim() !== baseUrl) return;
      this.offered.set([]);
      this.server.set({ ok: false, text: error instanceof ApiError ? error.message : "Can't reach it." });
    }
  }
}
