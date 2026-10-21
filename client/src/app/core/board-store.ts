import { computed, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { ApiError } from './api';
import { LocalApi, StoredBoard } from './local-api';
import { Note, NOTE_COLORS, NoteColor, Part } from './models';
import { local } from './storage';
import { Toasts } from './toasts';
import { newId } from './util';

/** Where the first version kept the board, in the browser. Moved to the data folder on first load. */
const BROWSER_KEY = 'th.board';
const VERSION = 1;

export interface NewNote {
  label: string;
  command: string;
  env: string;
  where?: string;
  explanation?: string;
  parts?: Part[];
  question?: string;
  origin?: string;
}

export interface ImportResult {
  added: number;
  updated: number;
  skipped: number;
}

export type BoardStatus = 'loading' | 'ready' | 'error';

/**
 * The sticky notes. They live in notes.json in the data folder on this computer, so they outlast the
 * browser and every way of opening the app sees the same board. Changes save as they happen.
 */
@Injectable({ providedIn: 'root' })
export class BoardStore {
  private readonly api = inject(LocalApi);
  private readonly toasts = inject(Toasts);
  private readonly _notes = signal<Note[]>([]);

  /** The server's revision of the board this copy is based on. */
  private revision = 0;
  /** The notes as last saved (or loaded), to skip saves that would change nothing. */
  private savedJson = '[]';
  private inflight: Promise<void> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;

  readonly status = signal<BoardStatus>('loading');
  readonly loadError = signal('');

  /** Newest first. */
  readonly notes = this._notes.asReadonly();
  readonly query = signal('');
  readonly filtered = computed(() => {
    const query = this.query();
    return query.trim() ? this._notes().filter((n) => matches(n, query)) : this._notes();
  });
  /** The note that was just pinned: the board scrolls to it and plays the drop-in animation. */
  readonly landed = signal<string | null>(null);
  /** Answer positions (`turnId:index`) that are currently pinned. */
  readonly origins = computed(() => new Set(this._notes().map((n) => n.origin).filter(Boolean)));

  constructor() {
    effect(() => {
      this._notes();
      if (this.status() === 'ready') untracked(() => void this.save());
    });
    // Another window may have changed the board while this one was in the background.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') void this.refresh();
    });
    void this.load();
  }

  async load() {
    this.status.set('loading');
    try {
      const board = await this.api.board();
      const early = this._notes(); // pinned while the board was still loading
      this.adopt(board);
      if (early.length) this._notes.update((notes) => [...early.filter((e) => !notes.some((n) => n.id === e.id)), ...notes]);
      this.status.set('ready');
      await this.moveBrowserBoard();
    } catch (error) {
      this.loadError.set(messageOf(error));
      this.status.set('error');
    }
  }

  get(id: string): Note | undefined {
    return this._notes().find((n) => n.id === id);
  }

  add(input: NewNote): Note {
    const now = Date.now();
    const note: Note = {
      id: newId(),
      label: input.label.trim(),
      command: input.command,
      where: input.where ?? '',
      explanation: input.explanation ?? '',
      parts: input.parts ?? [],
      memo: '',
      question: input.question ?? '',
      env: input.env,
      color: 'yellow',
      origin: input.origin ?? '',
      createdAt: now,
      updatedAt: now,
    };
    this._notes.update((notes) => [note, ...notes]);
    if (!matches(note, this.query())) this.query.set('');
    this.landed.set(note.id);
    setTimeout(() => this.landed.update((id) => (id === note.id ? null : id)), 1200);
    return note;
  }

  update(id: string, patch: Partial<Omit<Note, 'id' | 'createdAt' | 'updatedAt'>>) {
    this._notes.update((notes) =>
      notes.map((n) => (n.id === id ? { ...n, ...patch, updatedAt: Date.now() } : n)),
    );
  }

  /** Deletes a note; the toast offers Undo. `quiet` skips the toast (e.g. an abandoned empty note). */
  remove(id: string, quiet = false) {
    const notes = this._notes();
    const index = notes.findIndex((n) => n.id === id);
    if (index === -1) return;
    const note = notes[index];
    this._notes.set(notes.filter((n) => n.id !== id));
    if (!quiet) {
      this.toasts.show('Note deleted', { action: { label: 'Undo', run: () => this.restore(note, index) } });
    }
  }

  removeByOrigin(origin: string) {
    const note = this._notes().find((n) => n.origin === origin);
    if (note) this.remove(note.id);
  }

  findByOrigin(origin: string): Note | undefined {
    return this._notes().find((n) => n.origin === origin);
  }

  exportFile(): { name: string; json: string } {
    const file = {
      app: 'terminal-helper',
      version: VERSION,
      exportedAt: new Date().toISOString(),
      notes: this._notes(),
    };
    const date = new Date().toISOString().slice(0, 10);
    return { name: `terminal-helper-board-${date}.json`, json: JSON.stringify(file, null, 2) };
  }

  /** Merges an exported board: new notes are added, and a newer copy of an existing note wins. */
  import(json: string): ImportResult {
    let parsed: unknown;
    try {
      parsed = JSON.parse(json);
    } catch {
      throw new Error("That file isn't valid JSON.");
    }
    const list = Array.isArray(parsed) ? parsed : (parsed as { notes?: unknown } | null)?.notes;
    if (!Array.isArray(list)) throw new Error('No notes found in that file.');

    const result: ImportResult = { added: 0, updated: 0, skipped: 0 };
    const byId = new Map(this._notes().map((n) => [n.id, n]));
    for (const item of list) {
      const note = toNote(item);
      const existing = note && byId.get(note.id);
      if (!note) result.skipped++;
      else if (!existing) {
        byId.set(note.id, note);
        result.added++;
      } else if (note.updatedAt > existing.updatedAt) {
        byId.set(note.id, note);
        result.updated++;
      } else result.skipped++;
    }
    this._notes.set([...byId.values()].sort((a, b) => b.createdAt - a.createdAt));
    return result;
  }

  private restore(note: Note, index: number) {
    this._notes.update((notes) =>
      notes.some((n) => n.id === note.id) ? notes : [...notes.slice(0, index), note, ...notes.slice(index)],
    );
  }

  /** Resolves once everything changed so far is saved, or the save failed and a retry is scheduled. */
  private save(): Promise<void> {
    this.inflight ??= this.saveChanges().finally(() => (this.inflight = null));
    return this.inflight;
  }

  /**
   * One save at a time; edits made while it's in flight go out right after. If another window saved
   * first, its board wins and this one shows it (rare: it's one person's board).
   */
  private async saveChanges() {
    clearTimeout(this.retryTimer);
    try {
      for (let json = JSON.stringify(this._notes()); json !== this.savedJson; json = JSON.stringify(this._notes())) {
        const outcome = await this.api.saveBoard(this.revision, json);
        if (!outcome.saved) {
          this.adopt(outcome.board);
          this.toasts.show('The board was changed in another window, so this one now shows that version.');
          return;
        }
        this.revision = outcome.revision;
        this.savedJson = json;
      }
    } catch (error) {
      this.toasts.show(`Couldn't save the board: ${messageOf(error)} Retrying…`, { kind: 'error', duration: 5000 });
      this.retryTimer = setTimeout(() => void this.save(), 5000);
    }
  }

  /** Picks up changes made in another window, unless this one has unsaved changes of its own. */
  private async refresh() {
    if (this.status() !== 'ready' || this.inflight || JSON.stringify(this._notes()) !== this.savedJson) return;
    try {
      const board = await this.api.board();
      if (board.revision !== this.revision) this.adopt(board);
    } catch {
      // Nothing to do: the next save reports the problem.
    }
  }

  private adopt(board: StoredBoard) {
    const notes = board.notes.map(toNote).filter((n) => n !== null);
    this.revision = board.revision;
    this.savedJson = JSON.stringify(notes);
    this._notes.set(notes);
  }

  /**
   * The first version kept the board in browser storage. If the data folder has no board yet, move
   * that one in, and only forget the browser copy once the server has saved it.
   */
  private async moveBrowserBoard() {
    const raw = local()?.getItem(BROWSER_KEY);
    if (!raw || this.revision !== 0 || this._notes().length) return;
    const notes = parseBoard(raw);
    if (!notes.length) return;
    this._notes.set(notes);
    await this.save();
    if (this.savedJson !== JSON.stringify(notes)) return;
    local()?.setItem(`${BROWSER_KEY}.moved`, raw);
    local()?.removeItem(BROWSER_KEY);
    this.toasts.show(`Moved ${notes.length} ${notes.length === 1 ? 'note' : 'notes'} from this browser into your data folder.`);
  }
}

function messageOf(error: unknown): string {
  return error instanceof ApiError ? error.message : 'Something went wrong.';
}

function matches(note: Note, query: string): boolean {
  const haystack = [note.label, note.command, note.memo, note.env, note.where, note.question]
    .join('\n')
    .toLowerCase();
  return query
    .toLowerCase()
    .split(/\s+/)
    .every((term) => haystack.includes(term));
}

function parseBoard(raw: string): Note[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as { notes?: unknown };
    return Array.isArray(parsed.notes) ? parsed.notes.map(toNote).filter((n) => n !== null) : [];
  } catch {
    return [];
  }
}

/** Validates one note from storage or an imported file; fills defaults, rejects junk. */
export function toNote(value: unknown): Note | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const text = (x: unknown) => (typeof x === 'string' ? x : '');
  const time = (x: unknown) => (typeof x === 'number' && Number.isFinite(x) ? x : Date.now());
  const command = text(v['command']);
  const label = text(v['label']);
  if (!command && !label) return null;
  const parts = Array.isArray(v['parts'])
    ? (v['parts'] as unknown[])
        .map((p) => p as Record<string, unknown>)
        .filter((p) => p && typeof p['token'] === 'string')
        .map((p) => ({ token: p['token'] as string, meaning: text(p['meaning']) }))
    : [];
  const createdAt = time(v['createdAt']);
  return {
    id: text(v['id']) || newId(),
    label,
    command,
    where: text(v['where']),
    explanation: text(v['explanation']),
    parts,
    memo: text(v['memo']),
    question: text(v['question']),
    env: text(v['env']),
    color: NOTE_COLORS.includes(v['color'] as NoteColor) ? (v['color'] as NoteColor) : 'yellow',
    origin: text(v['origin']),
    createdAt,
    updatedAt: typeof v['updatedAt'] === 'number' ? (v['updatedAt'] as number) : createdAt,
  };
}
