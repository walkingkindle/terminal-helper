import { Component, computed, effect, ElementRef, inject, linkedSignal } from '@angular/core';
import { BoardStore } from '../core/board-store';
import { Icon } from '../ui/icon';
import { InView } from '../ui/in-view';
import { NoteDialogState } from './note-dialog';
import { StickyNote } from './sticky-note';

const PAGE = 48;

/** The wall of sticky notes. Renders a page at a time and grows as you scroll. */
@Component({
  selector: 'app-board',
  imports: [Icon, InView, StickyNote],
  templateUrl: './board.html',
  styleUrl: './board.css',
})
export class Board {
  protected readonly store = inject(BoardStore);
  protected readonly dialog = inject(NoteDialogState);
  protected readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  /** Back to the first page whenever the filter changes. */
  private readonly limit = linkedSignal({ source: this.store.query, computation: () => PAGE });
  protected readonly visible = computed(() => this.store.filtered().slice(0, this.limit()));

  constructor() {
    // Freshly pinned notes land at the top-left: bring them into view.
    effect(() => {
      if (this.store.landed()) this.host.scrollTo({ top: 0, behavior: 'smooth' });
    });
  }

  protected more() {
    this.limit.update((n) => n + PAGE);
  }
}
