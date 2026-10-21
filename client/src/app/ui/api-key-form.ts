import { Component, inject, output, signal } from '@angular/core';
import { ApiError } from '../core/api';
import { KeyState, LocalApi } from '../core/local-api';
import { ServerStatus } from '../core/server-status';
import { Toasts } from '../core/toasts';

/** Paste a key, the server checks it with Anthropic and saves it in the data folder. */
@Component({
  selector: 'app-api-key-form',
  template: `
    <form (submit)="$event.preventDefault(); save()">
      <input
        class="input"
        type="password"
        placeholder="sk-ant-…"
        aria-label="Anthropic API key"
        autocomplete="off"
        spellcheck="false"
        [value]="value()"
        (input)="value.set($any($event.target).value)"
      />
      <button class="btn primary" type="submit" [disabled]="busy() || !value().trim()">
        {{ busy() ? 'Checking…' : 'Save' }}
      </button>
    </form>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
  `,
  styles: `
    form {
      display: flex;
      gap: 8px;
    }
    input {
      flex: 1;
      min-width: 0;
      font-family: var(--mono);
      font-size: 13px;
    }
    .btn {
      flex: none;
      height: 30px;
    }
    .error {
      margin: 6px 0 0;
      color: var(--danger);
      font-size: 13px;
    }
  `,
})
export class ApiKeyForm {
  readonly saved = output<KeyState>();

  private readonly api = inject(LocalApi);
  private readonly status = inject(ServerStatus);
  private readonly toasts = inject(Toasts);

  protected readonly value = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected async save() {
    const key = this.value().trim();
    if (!key || this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const state = await this.api.saveApiKey(key);
      this.value.set('');
      this.toasts.show(
        state.verified === false ? "API key saved. Anthropic couldn't be reached to check it, though." : 'API key saved',
      );
      await this.status.refresh();
      this.saved.emit(state);
    } catch (error) {
      this.error.set(error instanceof ApiError ? error.message : "Couldn't save the key.");
    } finally {
      this.busy.set(false);
    }
  }
}
