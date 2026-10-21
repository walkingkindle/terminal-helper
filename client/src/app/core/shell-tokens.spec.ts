import { tokenizeShell, TokenKind } from './shell-tokens';

/** Kinds and texts, ignoring the whitespace that plain text tokens absorb. */
const kinds = (source: string) =>
  tokenizeShell(source)
    .filter((t) => t.text.trim())
    .map((t) => [t.kind, t.text.trim()] as [TokenKind, string]);

describe('tokenizeShell', () => {
  it('always reproduces the input exactly', () => {
    const samples = [
      'echo "hello world" >> notes.txt',
      "find . -name '*.log' -size +100M -delete",
      'sudo pacman -Syu && yay -S <package>',
      'nohup node server.js -p 8080 > out.log 2>&1 &',
      'Get-ChildItem -Recurse | Where-Object { $_.Length -gt 100MB }',
      'cat <<EOF > file\nline $HOME\nEOF',
      'ssh <user>@<host> # comment',
      '',
    ];
    for (const sample of samples) {
      expect(tokenizeShell(sample).map((t) => t.text).join('')).toBe(sample);
    }
  });

  it('marks the command, flags, strings and operators', () => {
    expect(kinds('echo "hi" >> notes.txt')).toEqual([
      ['cmd', 'echo'],
      ['str', '"hi"'],
      ['op', '>>'],
      ['text', 'notes.txt'],
    ]);
    expect(kinds('ls -la | grep -v x')).toEqual([
      ['cmd', 'ls'],
      ['flag', '-la'],
      ['op', '|'],
      ['cmd', 'grep'],
      ['flag', '-v'],
      ['text', 'x'],
    ]);
  });

  it('treats the word after sudo as a command too', () => {
    expect(kinds('sudo pacman -S htop').slice(0, 2)).toEqual([
      ['cmd', 'sudo'],
      ['cmd', 'pacman'],
    ]);
  });

  it('highlights placeholders but not redirections', () => {
    expect(kinds('ssh <user>@<host>')).toEqual([
      ['cmd', 'ssh'],
      ['ph', '<user>'],
      ['text', '@'],
      ['ph', '<host>'],
    ]);
    expect(kinds('sort < in.txt > out.txt').map(([k]) => k)).toEqual(['cmd', 'op', 'text', 'op', 'text']);
  });

  it('reads breakdown fragments the way a man page shows them', () => {
    expect(kinds('-p 8080')).toEqual([
      ['flag', '-p'],
      ['text', '8080'],
    ]);
    expect(kinds('> server.log')).toEqual([
      ['op', '>'],
      ['text', 'server.log'],
    ]);
    expect(kinds('cmd > out.txt && next')).toEqual([
      ['cmd', 'cmd'],
      ['op', '>'],
      ['text', 'out.txt'],
      ['op', '&&'],
      ['cmd', 'next'],
    ]);
  });

  it('knows variables, 2>&1 and comments', () => {
    expect(kinds('echo $HOME 2>&1 # done')).toEqual([
      ['cmd', 'echo'],
      ['var', '$HOME'],
      ['op', '2>&1'],
      ['comment', '# done'],
    ]);
  });
});
