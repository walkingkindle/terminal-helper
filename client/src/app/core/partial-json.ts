interface Parsed {
  value: unknown;
  /** False when the input ended inside this value. */
  done: boolean;
}

const ESCAPES: Record<string, string> = {
  '"': '"',
  '\\': '\\',
  '/': '/',
  b: '\b',
  f: '\f',
  n: '\n',
  r: '\r',
  t: '\t',
};

const LITERAL = /-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null/y;

/**
 * Parses JSON that may be cut off mid-way, as it is while an answer streams in.
 * Returns what is complete so far: an open string is returned as far as it got, open
 * arrays and objects keep their members, and a dangling key or half-written literal is
 * dropped. Returns undefined when nothing usable has arrived yet.
 */
export function parsePartialJson(text: string): unknown {
  let i = 0;

  const skipWhitespace = () => {
    while (i < text.length && (text[i] === ' ' || text[i] === '\n' || text[i] === '\r' || text[i] === '\t')) i++;
  };

  const parseString = (): { value: string; done: boolean } => {
    i++; // opening quote
    let out = '';
    while (i < text.length) {
      const ch = text[i++];
      if (ch === '"') return { value: out, done: true };
      if (ch !== '\\') {
        out += ch;
        continue;
      }
      if (i >= text.length) break; // the stream stopped right after a backslash
      const escape = text[i++];
      if (escape === 'u') {
        const hex = text.slice(i, i + 4);
        if (hex.length < 4) {
          i = text.length;
          break;
        }
        out += String.fromCharCode(parseInt(hex, 16));
        i += 4;
      } else {
        out += ESCAPES[escape] ?? escape;
      }
    }
    return { value: out, done: false };
  };

  const parseArray = (): Parsed => {
    i++;
    const out: unknown[] = [];
    for (;;) {
      skipWhitespace();
      if (i >= text.length) return { value: out, done: false };
      if (text[i] === ']') {
        i++;
        return { value: out, done: true };
      }
      if (text[i] === ',') {
        i++;
        continue;
      }
      const item = parseValue();
      if (!item) return { value: out, done: false };
      out.push(item.value);
      if (!item.done) return { value: out, done: false };
    }
  };

  const parseObject = (): Parsed => {
    i++;
    const out: Record<string, unknown> = {};
    for (;;) {
      skipWhitespace();
      if (i >= text.length) return { value: out, done: false };
      if (text[i] === '}') {
        i++;
        return { value: out, done: true };
      }
      if (text[i] === ',') {
        i++;
        continue;
      }
      if (text[i] !== '"') return { value: out, done: false };
      const key = parseString();
      if (!key.done) return { value: out, done: false };
      skipWhitespace();
      if (text[i] !== ':') return { value: out, done: false };
      i++;
      const item = parseValue();
      if (!item) return { value: out, done: false };
      out[key.value] = item.value;
      if (!item.done) return { value: out, done: false };
    }
  };

  const parseValue = (): Parsed | undefined => {
    skipWhitespace();
    if (i >= text.length) return undefined;
    const ch = text[i];
    if (ch === '"') return parseString();
    if (ch === '{') return parseObject();
    if (ch === '[') return parseArray();

    LITERAL.lastIndex = i;
    const match = LITERAL.exec(text);
    // A literal touching the end of the input may still be growing ("tr" → "true", "12" → "123").
    if (!match || LITERAL.lastIndex >= text.length) {
      i = text.length;
      return undefined;
    }
    i = LITERAL.lastIndex;
    return { value: JSON.parse(match[0]), done: true };
  };

  return parseValue()?.value;
}
