import { parseFinal, toAnswer } from './answer';
import { parsePartialJson } from './partial-json';

describe('toAnswer', () => {
  it('fills defaults and drops commands that have not started yet', () => {
    const partial = parsePartialJson('{"answer":"","commands":[{"command":"pwd","label":"Print"},{"comm');
    expect(toAnswer(partial)).toEqual({
      answer: '',
      commands: [{ command: 'pwd', label: 'Print', where: '', explanation: '', parts: [] }],
      note: '',
    });
  });

  it('survives garbage', () => {
    expect(toAnswer(null)).toEqual({ answer: '', commands: [], note: '' });
    expect(toAnswer({ commands: 'nope', note: 5 })).toEqual({ answer: '', commands: [], note: '' });
  });

  it('falls back to a partial parse for a cut-off answer', () => {
    expect(parseFinal('{"answer":"cut')).toEqual({ answer: 'cut' });
  });
});
