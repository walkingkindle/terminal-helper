import { Component, computed, ElementRef, inject, signal, viewChild } from '@angular/core';
import { Board } from './board/board';
import { NoteDialog } from './board/note-dialog';
import { AskStore } from './core/ask-store';
import { DOCK_DEFAULT, DOCK_MAX, DOCK_MIN, Layout } from './core/layout';
import { isTyping } from './core/util';
import { Dock } from './dock/dock';
import { Topbar } from './topbar/topbar';
import { ToastHost } from './ui/toast-host';

/**
 * One page: the board fills the top, the ask dock sits underneath, and a draggable
 * divider between them remembers where you left it.
 */
@Component({
  selector: 'app-root',
  imports: [Board, Dock, NoteDialog, ToastHost, Topbar],
  templateUrl: './app.html',
  styleUrl: './app.css',
  host: {
    '[style.--dock]': 'layout.dock()',
    '[class.resizing]': 'resizing()',
    '(document:keydown)': 'onKeydown($event)',
  },
})
export class App {
  protected readonly layout = inject(Layout);
  private readonly ask = inject(AskStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly topbar = viewChild.required(Topbar);
  private readonly dock = viewChild.required(Dock);

  protected readonly resizing = signal(false);
  protected readonly dockPercent = computed(() => Math.round(this.layout.dock() * 100));
  protected readonly limits = { min: DOCK_MIN * 100, max: DOCK_MAX * 100 };

  protected onKeydown(event: KeyboardEvent) {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.topbar().focusFilter();
    } else if (event.key === 'Escape' && this.ask.busy() && !document.querySelector('dialog[open], .menu')) {
      this.ask.stop();
    } else if (event.key === '/' && !isTyping(event) && !event.ctrlKey && !event.metaKey && !event.altKey) {
      event.preventDefault();
      this.dock().focusInput();
    }
  }

  protected startResize(event: PointerEvent) {
    if (event.button !== 0) return;
    event.preventDefault();
    const handle = event.currentTarget as HTMLElement;
    handle.setPointerCapture(event.pointerId);
    this.resizing.set(true);

    const bottom = this.host.getBoundingClientRect().bottom;
    const top = (handle.previousElementSibling as HTMLElement).getBoundingClientRect().top;
    const available = bottom - top - handle.offsetHeight;
    const move = (e: PointerEvent) =>
      this.layout.setDock((bottom - e.clientY - handle.offsetHeight / 2) / available);
    const end = () => {
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', end);
      handle.removeEventListener('pointercancel', end);
      this.resizing.set(false);
      this.layout.save();
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', end);
    handle.addEventListener('pointercancel', end);
  }

  protected onResizeKey(event: KeyboardEvent) {
    const step = event.shiftKey ? 0.1 : 0.04;
    const dock = this.layout.dock();
    const next: Record<string, number> = {
      ArrowUp: dock + step,
      ArrowDown: dock - step,
      Home: DOCK_MAX,
      End: DOCK_MIN,
      Enter: DOCK_DEFAULT,
    };
    if (!(event.key in next)) return;
    event.preventDefault();
    this.layout.setDock(next[event.key]);
    this.layout.save();
  }

  protected resetDock() {
    this.layout.setDock(DOCK_DEFAULT);
    this.layout.save();
  }
}
