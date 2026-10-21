import { Answer, CommandItem, Explanation, Part } from './models';
import { parsePartialJson } from './partial-json';

export const EMPTY_ANSWER: Answer = { answer: '', commands: [], note: '' };

type Loose = Record<string, unknown>;

const asObject = (value: unknown): Loose =>
  value && typeof value === 'object' && !Array.isArray(value) ? (value as Loose) : {};
const asString = (value: unknown) => (typeof value === 'string' ? value : '');
const asArray = (value: unknown) => (Array.isArray(value) ? value : []);

function toParts(value: unknown): Part[] {
  return asArray(value)
    .map((p) => ({ token: asString(asObject(p)['token']), meaning: asString(asObject(p)['meaning']) }))
    .filter((p) => p.token);
}

function toCommand(value: unknown): CommandItem {
  const c = asObject(value);
  return {
    command: asString(c['command']),
    label: asString(c['label']),
    where: asString(c['where']),
    explanation: asString(c['explanation']),
    parts: toParts(c['parts']),
  };
}

/** Normalizes a (possibly partial) parsed answer into the full shape with defaults. */
export function toAnswer(value: unknown): Answer {
  const a = asObject(value);
  return {
    answer: asString(a['answer']),
    commands: asArray(a['commands'])
      .map(toCommand)
      .filter((c) => c.command),
    note: asString(a['note']),
  };
}

export function toExplanation(value: unknown): Explanation {
  const e = asObject(value);
  return { explanation: asString(e['explanation']), parts: toParts(e['parts']) };
}

/** Strict parse once the stream has finished; falls back to a partial parse for a cut-off answer. */
export function parseFinal(raw: string): unknown {
  try {
    return JSON.parse(raw);
  } catch {
    return parsePartialJson(raw);
  }
}
