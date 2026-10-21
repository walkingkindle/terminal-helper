import { NgTemplateOutlet } from '@angular/common';
import { afterNextRender, Component, computed, ElementRef, inject, Injector, signal } from '@angular/core';
import { DISTROS, Os, OS_LIST, SHELLS, usesDistro, withOs } from '../core/env';
import { Settings } from '../core/settings';
import { Icon } from '../ui/icon';

const OTHER = '__other';
const WSL_DISTROS = ['Ubuntu', 'Debian', 'Arch Linux', 'Fedora', 'openSUSE', 'Kali Linux', 'AlmaLinux'];

/** The "your setup" chip in the composer and its popover. */
@Component({
  selector: 'app-env-menu',
  imports: [Icon, NgTemplateOutlet],
  templateUrl: './env-menu.html',
  styleUrl: './env-menu.css',
  host: {
    '(document:pointerdown)': 'onDocumentPointer($event)',
    '(keydown.escape)': 'close()',
  },
})
export class EnvMenu {
  protected readonly settings = inject(Settings);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly injector = inject(Injector);

  protected readonly env = this.settings.env;
  protected readonly isOpen = signal(false);
  protected readonly osList = OS_LIST;
  protected readonly other = OTHER;
  protected readonly shells = computed(() => SHELLS[this.env().os]);
  protected readonly showDistro = computed(() => usesDistro(this.env()));
  protected readonly distros = computed(() => (this.env().os === 'windows' ? WSL_DISTROS : DISTROS));
  /** "Other…" is picked (or the saved distro isn't in the list): show a text field. */
  protected readonly customDistro = signal(false);

  open() {
    this.customDistro.set(this.isCustom());
    this.isOpen.set(true);
    this.settings.envConfirmed.set(true);
  }

  close() {
    this.isOpen.set(false);
  }

  protected toggle() {
    if (this.isOpen()) this.close();
    else this.open();
  }

  protected setOs(os: Os) {
    this.settings.env.update((env) => withOs(env, os));
    this.customDistro.set(this.isCustom());
  }

  protected setShell(shell: string) {
    this.settings.env.update((env) => ({ ...env, shell }));
  }

  protected pickDistro(value: string) {
    const custom = value === OTHER;
    this.customDistro.set(custom);
    this.settings.env.update((env) => ({ ...env, distro: custom ? '' : value }));
    if (custom) {
      afterNextRender(() => this.host.querySelector<HTMLInputElement>('.custom-distro')?.focus(), {
        injector: this.injector,
      });
    }
  }

  protected setDistro(distro: string) {
    this.settings.env.update((env) => ({ ...env, distro: distro.slice(0, 60) }));
  }

  protected setNotes(notes: string) {
    this.settings.env.update((env) => ({ ...env, notes: notes.slice(0, 500) }));
  }

  protected onDocumentPointer(event: PointerEvent) {
    if (this.isOpen() && !this.host.contains(event.target as Node)) this.close();
  }

  private isCustom() {
    const distro = this.env().distro;
    return distro !== '' && !this.distros().includes(distro);
  }
}
