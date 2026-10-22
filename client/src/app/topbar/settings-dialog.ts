import { afterRenderEffect, Component, computed, effect, ElementRef, inject, Injectable, signal, viewChild } from '@angular/core';
import { ApiError } from '../core/api';
import { AppSettings, LocalApi, ProviderId } from '../core/local-api';
import { Toasts } from '../core/toasts';
import { copyText } from '../core/util';
import { Icon } from '../ui/icon';
import { ProviderForm } from './provider-form';

@Injectable({ providedIn: 'root' })
export class SettingsDialogState {
  readonly isOpen = signal(false);
  /** The provider tab to show first; otherwise the one in use. */
  readonly provider = signal<ProviderId | null>(null);

  open(provider: ProviderId | null = null) {
    this.provider.set(provider);
    this.isOpen.set(true);
  }

  close() {
    this.isOpen.set(false);
  }
}

const TAB_NAMES: Record<ProviderId, string> = { anthropic: 'Claude', openai: 'OpenAI', local: 'Local model' };

/** Which AI answers (and with which key), and where the data lives. */
@Component({
  selector: 'app-settings-dialog',
  imports: [Icon, ProviderForm],
  templateUrl: './settings-dialog.html',
  styleUrl: './settings-dialog.css',
})
export class SettingsDialog {
  protected readonly state = inject(SettingsDialogState);
  private readonly api = inject(LocalApi);
  private readonly toasts = inject(Toasts);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  protected readonly settings = signal<AppSettings | null>(null);
  protected readonly error = signal('');
  protected readonly tabNames = TAB_NAMES;
  protected readonly tab = signal<ProviderId>('anthropic');
  protected readonly selected = computed(() => this.settings()?.providers.find((p) => p.id === this.tab()));
  private pointerDownOnBackdrop = false;

  constructor() {
    effect(() => {
      if (this.state.isOpen()) void this.reload(true);
    });
    afterRenderEffect(() => {
      const dialog = this.dialog().nativeElement;
      if (this.state.isOpen() && !dialog.open) dialog.showModal();
      else if (!this.state.isOpen() && dialog.open) dialog.close();
    });
  }

  protected async reload(opening = false) {
    try {
      const settings = await this.api.settings();
      this.settings.set(settings);
      if (opening) this.tab.set(this.state.provider() ?? settings.active);
      this.error.set('');
    } catch (error) {
      this.error.set(error instanceof ApiError ? error.message : "Couldn't load the settings.");
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
