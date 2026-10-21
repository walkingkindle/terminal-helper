import { parsePartialJson } from './partial-json';

describe('parsePartialJson', () => {
  const full = '{"answer":"","commands":[{"command":"ssh -v <user>@<host>","label":"SSH in","parts":[{"token":"-v","meaning":"verbose"}]}],"note":"ok"}';

  it('parses complete JSON like JSON.parse', () => {
    expect(parsePartialJson(full)).toEqual(JSON.parse(full));
  });

  it('handles every prefix of a stream without throwing', () => {
    for (let i = 0; i < full.length; i++) {
      expect(() => parsePartialJson(full.slice(0, i))).not.toThrow();
    }
    // Once the command string has begun, it is visible at every later prefix.
    const start = full.indexOf('ssh');
    for (let i = start + 1; i <= full.length; i++) {
      const value = parsePartialJson(full.slice(0, i)) as { commands: { command: string }[] };
      expect(value.commands[0].command.length).toBeGreaterThan(0);
    }
  });

  it('returns an open string as far as it got', () => {
    expect(parsePartialJson('{"commands":[{"command":"ssh -v us')).toEqual({
      commands: [{ command: 'ssh -v us' }],
    });
  });

  it('drops a key whose value has not started', () => {
    expect(parsePartialJson('{"answer":"","comm')).toEqual({ answer: '' });
    expect(parsePartialJson('{"answer":"","commands":')).toEqual({ answer: '' });
  });

  it('drops literals that may still be growing', () => {
    expect(parsePartialJson('[1, 2')).toEqual([1]);
    expect(parsePartialJson('{"a":tr')).toEqual({});
    expect(parsePartialJson('{"a":true,')).toEqual({ a: true });
  });

  it('handles escapes, including one cut in half', () => {
    expect(parsePartialJson('"a\\nb\\"c')).toBe('a\nb"c');
    expect(parsePartialJson('"tab\\')).toBe('tab');
    expect(parsePartialJson('"\\u00e9t\\u00')).toBe('ét');
  });

  it('returns undefined when nothing usable arrived', () => {
    expect(parsePartialJson('')).toBeUndefined();
    expect(parsePartialJson('   ')).toBeUndefined();
  });
});
