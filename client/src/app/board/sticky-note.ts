import { Component, computed, inject, input, output, signal } from '@angular/core';
import { Note } from '../core/models';
import { Toasts } from '../core/toasts';
import { copyText, timeAgo } from '../core/util';
import { CommandCode } from '../ui/command-code';
import { Icon } from '../ui/icon';

/** A slight, stable tilt per note so the wall looks hand-pinned. */
const TILTS = [-1.4, -0.8, -0.3, 0.4, 0.9, 1.3];

@Component({
  selector: 'app-sticky-note',
  imports: [CommandCode, Icon],
  template: `
    <div class="label">{{ note().label || 'Untitled' }}</div>
    @if (note().command) {
      <button class="cmd" type="button" title="Copy" aria-label="Copy command" (click)="copy($event)">
        <app-command-code [code]="note().command" />
        <app-icon class="copy-icon" [name]="copied() ? 'check' : 'copy'" [size]="13" />
      </button>
    } @else {
      <div class="cmd empty">No command yet</div>
    }
    @if (note().where) {
      <div class="where"><app-icon name="corner" [size]="12" /> {{ note().where }}</div>
    }
    @if (note().memo) {
      <p class="memo">{{ note().memo }}</p>
    }
    <div class="foot">
      @if (note().env) {
        <span class="env">{{ note().env }}</span>
      }
      @if (note().parts.length) {
        <span class="explained" title="Has a breakdown, open the note to read it">
          <app-icon name="book" [size]="12" /> {{ note().parts.length }}
        </span>
      }
      <span class="age">{{ age() }}</span>
    </div>
  `,
  styleUrl: './sticky-note.css',
  host: {
    role: 'button',
    tabindex: '0',
    '[attr.data-color]': 'note().color',
    '[attr.aria-label]': '"Open note: " + (note().label || note().command)',
    '[style.--tilt]': 'tilt()',
    '[class.landed]': 'landed()',
    '(click)': 'open.emit()',
    '(keydown.enter)': 'onKey($event)',
    '(keydown.space)': 'onKey($event)',
  },
})
export class StickyNote {
  readonly note = input.required<Note>();
  readonly landed = input(false);
  readonly open = output<void>();

  private readonly toasts = inject(Toasts);
  protected readonly copied = signal(false);
  protected readonly age = computed(() => timeAgo(this.note().createdAt));
  protected readonly tilt = computed(() => {
    let hash = 0;
    for (const ch of this.note().id) hash = (hash * 31 + ch.charCodeAt(0)) | 0;
    return `${TILTS[Math.abs(hash) % TILTS.length]}deg`;
  });

  protected async copy(event: Event) {
    event.stopPropagation();
    if (await copyText(this.note().command)) {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1200);
    } else {
      this.toasts.show("Couldn't copy to the clipboard", { kind: 'error' });
    }
  }

  /** Enter/Space on the note itself opens it; on the inner copy button they copy. */
  protected onKey(event: Event) {
    if (event.target !== event.currentTarget) return;
    event.preventDefault();
    this.open.emit();
  }
}
