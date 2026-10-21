import { TestBed } from '@angular/core/testing';
import { ApiError } from './api';
import { BoardStore, toNote } from './board-store';
import { LocalApi, SaveOutcome, StoredBoard } from './local-api';
import { Toasts } from './toasts';

/** The server's notes.json, in memory. */
class FakeLocalApi {
  revision = 0;
  notes: unknown[] = [];
  saves: unknown[][] = [];
  failLoad = false;
  /** Holds saves until released, to test edits made while one is in flight. */
  gate: Promise<void> = Promise.resolve();

  async board(): Promise<StoredBoard> {
    if (this.failLoad) throw new ApiError('notes.json is broken', '500');
    return { revision: this.revision, notes: structuredClone(this.notes) };
  }

  async saveBoard(revision: number, notesJson: string): Promise<SaveOutcome> {
    await this.gate;
    if (revision !== this.revision) return { saved: false, board: await this.board() };
    this.notes = JSON.parse(notesJson);
    this.saves.push(this.notes);
    return { saved: true, revision: ++this.revision };
  }
}

const stored = (id: string, label: string) => ({ id, label, command: label.toLowerCase(), env: '', createdAt: 1, updatedAt: 1 });

describe('BoardStore', () => {
  let api: FakeLocalApi;
  let toasts: Toasts;

  /** Lets effects run and pending saves finish. */
  const settle = async () => {
    for (let i = 0; i < 5; i++) {
      TestBed.tick();
      await new Promise((resolve) => setTimeout(resolve));
    }
  };

  async function start(): Promise<BoardStore> {
    const board = TestBed.inject(BoardStore);
    await settle();
    return board;
  }

  beforeEach(() => {
    localStorage.clear();
    api = new FakeLocalApi();
    TestBed.configureTestingModule({ providers: [{ provide: LocalApi, useValue: api }] });
    toasts = TestBed.inject(Toasts);
  });

  it('loads the board from the data folder', async () => {
    api.revision = 3;
    api.notes = [stored('a', 'First')];

    const board = await start();

    expect(board.status()).toBe('ready');
    expect(board.notes().map((n) => n.label)).toEqual(['First']);
    expect(api.saves).toEqual([]);
  });

  it('saves every change, newest first', async () => {
    const board = await start();

    board.add({ label: 'First', command: 'pwd', env: 'Arch Linux · bash' });
    board.add({ label: 'Second', command: 'ls -la', env: 'Arch Linux · bash' });
    await settle();

    expect(api.revision).toBeGreaterThan(0);
    expect((api.notes as { label: string }[]).map((n) => n.label)).toEqual(['Second', 'First']);
  });

  it('sends edits made during a save right after it', async () => {
    const board = await start();
    let release!: () => void;
    api.gate = new Promise((resolve) => (release = resolve));

    board.add({ label: 'One', command: 'a', env: '' });
    await settle();
    board.add({ label: 'Two', command: 'b', env: '' });
    await settle();
    release();
    await settle();

    expect((api.notes as { label: string }[]).map((n) => n.label)).toEqual(['Two', 'One']);
  });

  it("shows another window's newer board instead of overwriting it", async () => {
    const board = await start();
    api.revision = 7; // someone else saved meanwhile
    api.notes = [stored('x', 'From the other window')];

    board.add({ label: 'Mine', command: 'pwd', env: '' });
    await settle();

    expect(board.notes().map((n) => n.label)).toEqual(['From the other window']);
    expect(toasts.items().at(-1)?.message).toContain('another window');
  });

  it('moves a board kept in browser storage into the data folder once', async () => {
    localStorage.setItem('th.board', JSON.stringify({ version: 1, notes: [stored('a', 'Old note')] }));

    const board = await start();

    expect(board.notes().map((n) => n.label)).toEqual(['Old note']);
    expect((api.notes as { label: string }[]).map((n) => n.label)).toEqual(['Old note']);
    expect(localStorage.getItem('th.board')).toBeNull();
    expect(localStorage.getItem('th.board.moved')).toContain('Old note');
    expect(toasts.items().at(-1)?.message).toContain('Moved 1 note');
  });

  it('leaves the browser copy alone when the data folder already has a board', async () => {
    api.revision = 1;
    api.notes = [stored('b', 'Server note')];
    localStorage.setItem('th.board', JSON.stringify({ version: 1, notes: [stored('a', 'Old note')] }));

    const board = await start();

    expect(board.notes().map((n) => n.label)).toEqual(['Server note']);
    expect(localStorage.getItem('th.board')).not.toBeNull();
  });

  it('reports a board that cannot be loaded, and can try again', async () => {
    api.failLoad = true;
    const board = await start();

    expect(board.status()).toBe('error');
    expect(board.loadError()).toContain('notes.json');

    api.failLoad = false;
    await board.load();
    expect(board.status()).toBe('ready');
  });

  it('filters by every word in the query', async () => {
    const board = await start();
    board.add({ label: 'Find big files', command: 'find . -size +100M', env: 'Arch Linux · bash' });
    board.add({ label: 'SSH', command: 'ssh <user>@<host>', env: 'Windows · Git Bash' });

    board.query.set('find 100m');
    expect(board.filtered().map((n) => n.label)).toEqual(['Find big files']);
    board.query.set('git bash');
    expect(board.filtered().map((n) => n.label)).toEqual(['SSH']);
  });

  it('can undo a delete, back in the same place', async () => {
    const board = await start();
    board.add({ label: 'A', command: 'a', env: '' });
    const b = board.add({ label: 'B', command: 'b', env: '' });
    board.add({ label: 'C', command: 'c', env: '' });

    board.remove(b.id);
    expect(board.notes().map((n) => n.label)).toEqual(['C', 'A']);
    toasts.items().at(-1)?.action?.run();
    expect(board.notes().map((n) => n.label)).toEqual(['C', 'B', 'A']);
  });

  it('round-trips through export and import, merging by id', async () => {
    const board = await start();
    const note = board.add({ label: 'Keep', command: 'echo hi >> notes.txt', env: 'Arch Linux · bash' });
    const { json, name } = board.exportFile();
    expect(name).toMatch(/^terminal-helper-board-\d{4}-\d{2}-\d{2}\.json$/);

    board.remove(note.id, true);
    expect(board.import(json)).toEqual({ added: 1, updated: 0, skipped: 0 });
    expect(board.notes()[0]).toEqual(note);
    expect(board.import(json)).toEqual({ added: 0, updated: 0, skipped: 1 });

    const newer = { ...note, label: 'Renamed', updatedAt: note.updatedAt + 1 };
    expect(board.import(JSON.stringify([newer]))).toEqual({ added: 0, updated: 1, skipped: 0 });
    expect(board.notes()[0].label).toBe('Renamed');
  });

  it('rejects files that are not boards', async () => {
    const board = await start();
    expect(() => board.import('not json')).toThrow("isn't valid JSON");
    expect(() => board.import('{"hello":1}')).toThrow('No notes');
  });

  it('cleans up notes from old or hand-edited files', () => {
    expect(toNote({ command: 'pwd', color: 'neon', parts: [{ token: '-v' }, 'junk'] })).toMatchObject({
      command: 'pwd',
      color: 'yellow',
      parts: [{ token: '-v', meaning: '' }],
    });
    expect(toNote({ memo: 'no command or label' })).toBeNull();
  });
});
