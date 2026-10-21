export type TokenKind = 'cmd' | 'flag' | 'str' | 'var' | 'op' | 'ph' | 'comment' | 'text';

export interface Token {
  kind: TokenKind;
  text: string;
}

/** After these, the next word is still a command: `sudo pacman`, `nohup node`, `xargs rm`. */
const PREFIX_COMMANDS = new Set(['sudo', 'doas', 'nohup', 'time', 'exec', 'command', 'xargs', 'watch', 'env', 'nice']);

const RULES: readonly [TokenKind, RegExp][] = [
  ['text', /\s+/y],
  ['str', /'[^']*'?/y],
  ['str', /"(?:[^"\\]|\\.)*"?/y],
  // <placeholder> or <two words>, but not a redirection like `< in.txt >out`.
  ['ph', /<[A-Za-z][\w.:/@-]*(?: [\w.:/@-]+)*>/y],
  ['var', /\$\{[^}\s]*\}?|\$[A-Za-z_]\w*|\$[0-9#?@*$!-]/y],
  ['op', /\$\(|\d?>>|\d?>&\d?|&>|\d>|<<-?|&&|\|\||\|&|[|;&<>()]/y],
  ['text', /[^\s'"<>|;&()$]+/y],
  ['text', /./y],
];

/**
 * Splits a shell command into tokens for syntax highlighting. It is deliberately
 * forgiving — commands may be half-streamed or PowerShell — and the concatenated
 * token text always equals the input.
 */
export function tokenizeShell(source: string): Token[] {
  const tokens: Token[] = [];
  let i = 0;
  let expectCommand = true;
  /** After `>`, `2>>`, `<` … the next word is a file name, not a command. */
  let expectFile = false;

  while (i < source.length) {
    const atWordStart = i === 0 || /\s/.test(source[i - 1]);
    if (source[i] === '#' && atWordStart) {
      const end = source.indexOf('\n', i);
      const text = source.slice(i, end === -1 ? undefined : end);
      tokens.push({ kind: 'comment', text });
      i += text.length;
      continue;
    }

    for (const [kind, rule] of RULES) {
      rule.lastIndex = i;
      const match = rule.exec(source);
      if (!match) continue;
      const text = match[0];
      i += text.length;

      if (kind === 'text' && /^\s+$/.test(text)) {
        if (text.includes('\n')) expectCommand = true;
        push(tokens, 'text', text);
      } else if (kind === 'op') {
        push(tokens, 'op', text);
        if (/^(\$\(|&&|\|\||\|&?|;|&|\()$/.test(text)) expectCommand = true;
        expectFile = /^(\d?>>?|&>|<)$/.test(text);
      } else if (kind === 'text' && expectFile) {
        push(tokens, 'text', text);
        expectFile = false;
      } else if (kind === 'text') {
        if (expectCommand && !text.includes('=') && !text.startsWith('-')) {
          push(tokens, 'cmd', text);
          expectCommand = PREFIX_COMMANDS.has(text);
        } else {
          push(tokens, text.startsWith('-') && text.length > 1 ? 'flag' : 'text', text);
          if (!text.includes('=')) expectCommand = false;
        }
      } else {
        push(tokens, kind, text);
        expectCommand = false;
        expectFile = false;
      }
      break;
    }
  }
  return tokens;
}

/** Merges adjacent plain text so the DOM stays small. */
function push(tokens: Token[], kind: TokenKind, text: string) {
  const last = tokens[tokens.length - 1];
  if (kind === 'text' && last?.kind === 'text') last.text += text;
  else tokens.push({ kind, text });
}
