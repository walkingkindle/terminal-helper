import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { Note, NOTE_COLORS, NoteColor, Part } from './models';
import { local, writeText } from './storage';
import { Toasts } from './toasts';
import { newId } from './util';

const KEY = 'th.board';
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

/**
 * The sticky notes. Phase 1 keeps them in localStorage (versioned, so a later move to a
 * server only needs a migration); export/import moves them between browsers.
 */
@Injectable({ providedIn: 'root' })
export class BoardStore {
  private readonly toasts = inject(Toasts);
  private lastSaved = local()?.getItem(KEY) ?? '';
  private readonly _notes = signal<Note[]>(parseBoard(this.lastSaved));

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
    effect(() => this.persist(this._notes()));
    // Another tab changed the board: take its version instead of overwriting it later.
    window.addEventListener('storage', (event) => {
      if (event.key !== KEY) return;
      this.lastSaved = event.newValue ?? '';
      this._notes.set(parseBoard(this.lastSaved));
    });
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

  private persist(notes: Note[]) {
    const json = JSON.stringify({ version: VERSION, notes });
    if (json === this.lastSaved) return;
    if (writeText(local(), KEY, json)) {
      this.lastSaved = json;
    } else {
      this.toasts.show("Couldn't save the board: browser storage is full or blocked. Export it to keep a copy.", {
        kind: 'error',
        duration: 8000,
      });
    }
  }
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
