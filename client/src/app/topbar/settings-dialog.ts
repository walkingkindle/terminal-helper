import { afterRenderEffect, Component, effect, ElementRef, inject, Injectable, signal, viewChild } from '@angular/core';
import { ApiError } from '../core/api';
import { AppSettings, KeyState, LocalApi } from '../core/local-api';
import { ServerStatus } from '../core/server-status';
import { Toasts } from '../core/toasts';
import { copyText } from '../core/util';
import { ApiKeyForm } from '../ui/api-key-form';
import { Icon } from '../ui/icon';

@Injectable({ providedIn: 'root' })
export class SettingsDialogState {
  readonly isOpen = signal(false);

  open() {
    this.isOpen.set(true);
  }

  close() {
    this.isOpen.set(false);
  }
}

const KEY_SOURCES: Record<NonNullable<KeyState['source']>, string> = {
  saved: 'saved on this computer',
  config: "from the app's configuration (appsettings or user-secrets)",
  environment: 'from the ANTHROPIC_API_KEY environment variable',
};

/** The API key and where the data lives. */
@Component({
  selector: 'app-settings-dialog',
  imports: [ApiKeyForm, Icon],
  templateUrl: './settings-dialog.html',
  styleUrl: './settings-dialog.css',
})
export class SettingsDialog {
  protected readonly state = inject(SettingsDialogState);
  private readonly api = inject(LocalApi);
  private readonly status = inject(ServerStatus);
  private readonly toasts = inject(Toasts);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  protected readonly settings = signal<AppSettings | null>(null);
  protected readonly error = signal('');
  protected readonly sources = KEY_SOURCES;
  private pointerDownOnBackdrop = false;

  constructor() {
    effect(() => {
      if (this.state.isOpen()) void this.reload();
    });
    afterRenderEffect(() => {
      const dialog = this.dialog().nativeElement;
      if (this.state.isOpen() && !dialog.open) dialog.showModal();
      else if (!this.state.isOpen() && dialog.open) dialog.close();
    });
  }

  protected async reload() {
    try {
      this.settings.set(await this.api.settings());
      this.error.set('');
    } catch (error) {
      this.error.set(error instanceof ApiError ? error.message : "Couldn't load the settings.");
    }
  }

  protected async removeKey() {
    try {
      await this.api.removeApiKey();
      this.toasts.show('Saved key removed');
      await Promise.all([this.reload(), this.status.refresh()]);
    } catch (error) {
      this.error.set(error instanceof ApiError ? error.message : "Couldn't remove the key.");
    }
  }

  protected async copyPath(path: string) {
    const ok = await copyText(path);
    this.toasts.show(ok ? 'Path copied' : "Couldn't copy to the clipboard", { kind: ok ? 'info' : 'error' });
  }

  protected onPointerDown(event: PointerEvent) {
    this.pointerDownOnBackdrop = event.target === this.dialog().nativeElement;
  }

  protected onClick(event: MouseEvent) {
    if (this.pointerDownOnBackdrop && event.target === this.dialog().nativeElement) this.state.close();
  }
}
