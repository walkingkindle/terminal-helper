import {
  afterRenderEffect,
  Component,
  computed,
  ElementRef,
  inject,
  Injectable,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { toExplanation } from '../core/answer';
import { ApiError, AssistantApi } from '../core/api';
import { BoardStore } from '../core/board-store';
import { toApiEnv } from '../core/env';
import { Explanation, Note, NOTE_COLORS } from '../core/models';
import { Settings } from '../core/settings';
import { Toasts } from '../core/toasts';
import { copyText } from '../core/util';
import { CommandCode } from '../ui/command-code';
import { Icon } from '../ui/icon';

@Injectable({ providedIn: 'root' })
export class NoteDialogState {
  readonly openId = signal<string | null>(null);
  /** Open straight into editing the command (a brand-new note). */
  readonly editCommand = signal(false);

  open(id: string, options: { editCommand?: boolean } = {}) {
    this.editCommand.set(options.editCommand ?? false);
    this.openId.set(id);
  }

  close() {
    this.openId.set(null);
  }
}

interface ExplainProgress {
  status: 'streaming' | 'error';
  explanation: Explanation;
  error: string;
}

/** Notion-style "center peek" for one note: edit it, explain it, color it, delete it. */
@Component({
  selector: 'app-note-dialog',
  imports: [CommandCode, Icon],
  templateUrl: './note-dialog.html',
  styleUrl: './note-dialog.css',
})
export class NoteDialog {
  protected readonly state = inject(NoteDialogState);
  protected readonly board = inject(BoardStore);
  private readonly api = inject(AssistantApi);
  private readonly settings = inject(Settings);
  private readonly toasts = inject(Toasts);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  protected readonly colors = NOTE_COLORS;
  protected readonly note = computed(() => {
    const id = this.state.openId();
    return id ? this.board.get(id) : undefined;
  });
  protected readonly editing = linkedSignal({ source: this.state.openId, computation: () => this.state.editCommand() });
  protected readonly progress = linkedSignal<string | null, ExplainProgress | null>({
    source: this.state.openId,
    computation: () => null,
  });
  /** A streaming explanation wins over the saved one, so the user watches it arrive. */
  protected readonly breakdown = computed<Explanation>(() => {
    const live = this.progress()?.explanation;
    if (live && (live.explanation || live.parts.length)) return live;
    const note = this.note();
    return { explanation: note?.explanation ?? '', parts: note?.parts ?? [] };
  });
  protected readonly created = computed(() => {
    const note = this.note();
    return note
      ? new Date(note.createdAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
      : '';
  });

  private explainController: AbortController | null = null;
  private memoTimer = 0;
  private pendingMemo: { id: string; memo: string } | null = null;
  private pointerDownOnBackdrop = false;

  constructor() {
    afterRenderEffect(() => {
      const dialog = this.dialog().nativeElement;
      const shouldOpen = !!this.note();
      if (shouldOpen && !dialog.open) dialog.showModal();
      else if (!shouldOpen && dialog.open) dialog.close();
    });
  }

  protected onClosed() {
    this.flushMemo();
    this.explainController?.abort();
    const note = this.note();
    // A "New" note that never got any content isn't worth keeping.
    if (note && !note.label.trim() && !note.command.trim() && !note.memo.trim()) this.board.remove(note.id, true);
    this.state.close();
  }

  /** Clicking the dimmed backdrop closes, but not a text selection that ends outside the sheet. */
  protected onPointerDown(event: PointerEvent) {
    this.pointerDownOnBackdrop = event.target === this.dialog().nativeElement;
  }

  protected onClick(event: MouseEvent) {
    if (this.pointerDownOnBackdrop && event.target === this.dialog().nativeElement) this.state.close();
  }

  protected saveLabel(note: Note, value: string) {
    if (value.trim() !== note.label) this.board.update(note.id, { label: value.trim() });
  }

  protected saveCommand(note: Note, value: string) {
    this.editing.set(false);
    const command = value.trim();
    if (command === note.command) return;
    // A breakdown of the old command would now be wrong.
    this.board.update(note.id, { command, explanation: '', parts: [] });
  }

  protected onCommandKey(event: KeyboardEvent, area: HTMLTextAreaElement) {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      area.blur();
    } else if (event.key === 'Escape') {
      // Esc leaves the editor (keeping the text); a second Esc closes the dialog.
      event.preventDefault();
      area.blur();
    }
  }

  protected onMemo(note: Note, memo: string) {
    this.pendingMemo = { id: note.id, memo };
    clearTimeout(this.memoTimer);
    this.memoTimer = setTimeout(() => this.flushMemo(), 400);
  }

  private flushMemo() {
    clearTimeout(this.memoTimer);
    if (this.pendingMemo) this.board.update(this.pendingMemo.id, { memo: this.pendingMemo.memo });
    this.pendingMemo = null;
  }

  protected async copy(command: string) {
    const ok = await copyText(command);
    this.toasts.show(ok ? 'Copied' : "Couldn't copy to the clipboard", { kind: ok ? 'info' : 'error' });
  }

  protected remove(note: Note) {
    this.state.close();
    this.board.remove(note.id);
  }

  protected async explain(note: Note) {
    this.explainController?.abort();
    const controller = new AbortController();
    this.explainController = controller;
    let latest: Explanation = { explanation: '', parts: [] };
    this.progress.set({ status: 'streaming', explanation: latest, error: '' });
    try {
      const { value } = await this.api.stream(
        '/api/explain',
        { command: note.command, env: toApiEnv(this.settings.env()) },
        controller.signal,
        toExplanation,
        (partial) => this.progress.set({ status: 'streaming', explanation: (latest = partial), error: '' }),
      );
      this.board.update(note.id, value);
      if (this.state.openId() === note.id) this.progress.set(null);
    } catch (error) {
      if (controller.signal.aborted) return;
      this.progress.set({
        status: 'error',
        explanation: latest,
        error: error instanceof ApiError ? error.message : 'Explaining failed. Try again.',
      });
    }
  }
}
