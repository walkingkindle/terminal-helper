import { TestBed } from '@angular/core/testing';
import { BoardStore, toNote } from './board-store';
import { Toasts } from './toasts';

describe('BoardStore', () => {
  let board: BoardStore;
  let toasts: Toasts;

  const flush = () => TestBed.tick();

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
    board = TestBed.inject(BoardStore);
    toasts = TestBed.inject(Toasts);
  });

  it('adds newest first and saves to localStorage', () => {
    board.add({ label: 'First', command: 'pwd', env: 'Arch Linux · bash' });
    board.add({ label: 'Second', command: 'ls -la', env: 'Arch Linux · bash' });
    flush();

    expect(board.notes().map((n) => n.label)).toEqual(['Second', 'First']);
    const saved = JSON.parse(localStorage.getItem('th.board')!);
    expect(saved.version).toBe(1);
    expect(saved.notes).toHaveLength(2);
  });

  it('filters by every word in the query', () => {
    board.add({ label: 'Find big files', command: 'find . -size +100M', env: 'Arch Linux · bash' });
    board.add({ label: 'SSH', command: 'ssh <user>@<host>', env: 'Windows · Git Bash' });

    board.query.set('find 100m');
    expect(board.filtered().map((n) => n.label)).toEqual(['Find big files']);
    board.query.set('git bash');
    expect(board.filtered().map((n) => n.label)).toEqual(['SSH']);
  });

  it('can undo a delete, back in the same place', () => {
    board.add({ label: 'A', command: 'a', env: '' });
    const b = board.add({ label: 'B', command: 'b', env: '' });
    board.add({ label: 'C', command: 'c', env: '' });

    board.remove(b.id);
    const undo = toasts.items().at(-1)?.action;
    expect(board.notes().map((n) => n.label)).toEqual(['C', 'A']);
    undo?.run();
    expect(board.notes().map((n) => n.label)).toEqual(['C', 'B', 'A']);
  });

  it('round-trips through export and import, merging by id', () => {
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

  it('rejects files that are not boards', () => {
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
