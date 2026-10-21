import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { NoteDialogState } from '../board/note-dialog';
import { SettingsDialogState } from './settings-dialog';
import { BoardStore } from '../core/board-store';
import { Settings, Theme } from '../core/settings';
import { Toasts } from '../core/toasts';
import { isMac } from '../core/util';
import { Icon, IconName } from '../ui/icon';

@Component({
  selector: 'app-topbar',
  imports: [Icon],
  templateUrl: './topbar.html',
  styleUrl: './topbar.css',
  host: {
    '(document:pointerdown)': 'onDocumentPointer($event)',
    '(keydown.escape)': 'menuOpen.set(false)',
  },
})
export class Topbar {
  protected readonly board = inject(BoardStore);
  protected readonly settings = inject(Settings);
  private readonly toasts = inject(Toasts);
  private readonly dialog = inject(NoteDialogState);
  private readonly settingsDialog = inject(SettingsDialogState);
  private readonly filter = viewChild.required<ElementRef<HTMLInputElement>>('filter');
  private readonly menuAnchor = viewChild.required<ElementRef<HTMLElement>>('menuAnchor');

  protected readonly menuOpen = signal(false);
  protected readonly mod = isMac() ? '⌘' : 'Ctrl';
  protected readonly themes: { id: Theme; label: string; icon: IconName }[] = [
    { id: 'system', label: 'System', icon: 'monitor' },
    { id: 'light', label: 'Light', icon: 'sun' },
    { id: 'dark', label: 'Dark', icon: 'moon' },
  ];

  focusFilter() {
    const input = this.filter().nativeElement;
    input.focus();
    input.select();
  }

  protected clearFilter(input: HTMLInputElement) {
    this.board.query.set('');
    input.blur();
  }

  protected newNote() {
    const note = this.board.add({ label: '', command: '', env: this.settings.envLabel() });
    this.dialog.open(note.id, { editCommand: true });
  }

  protected openSettings() {
    this.menuOpen.set(false);
    this.settingsDialog.open();
  }

  protected exportBoard() {
    this.menuOpen.set(false);
    const { name, json } = this.board.exportFile();
    const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
    Object.assign(document.createElement('a'), { href: url, download: name }).click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    const count = this.board.notes().length;
    this.toasts.show(`Exported ${count} ${count === 1 ? 'note' : 'notes'}`);
  }

  protected importBoard() {
    this.menuOpen.set(false);
    const picker = Object.assign(document.createElement('input'), { type: 'file', accept: 'application/json,.json' });
    picker.addEventListener('change', async () => {
      const file = picker.files?.[0];
      if (!file) return;
      try {
        const { added, updated, skipped } = this.board.import(await file.text());
        this.toasts.show(`Imported ${added} new, ${updated} updated${skipped ? `, ${skipped} unchanged` : ''}`);
      } catch (error) {
        this.toasts.show(error instanceof Error ? error.message : 'Import failed', { kind: 'error' });
      }
    });
    picker.click();
  }

  protected onDocumentPointer(event: PointerEvent) {
    if (this.menuOpen() && !this.menuAnchor().nativeElement.contains(event.target as Node)) {
      this.menuOpen.set(false);
    }
  }
}
