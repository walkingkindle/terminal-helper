import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { EMPTY_ANSWER, toAnswer, toExplanation } from './answer';
import { ApiError, AssistantApi } from './api';
import { BoardStore } from './board-store';
import { envLabel, toApiEnv } from './env';
import { Answer, CommandItem, Explanation, Part } from './models';
import { readJson, session, writeJson } from './storage';
import { newId } from './util';
import { Settings } from './settings';

export type TurnStatus = 'streaming' | 'done' | 'error' | 'stopped';

export interface ExplainState {
  status: 'streaming' | 'done' | 'error';
  explanation: Explanation;
  error: string;
}

/** One question and its answer in the dock. */
export interface Turn {
  id: string;
  question: string;
  verbose: boolean;
  /** Environment label at the time of asking. */
  env: string;
  status: TurnStatus;
  /** The raw JSON the model produced; sent back as history for follow-up questions. */
  raw: string;
  answer: Answer;
  stopReason: string | null;
  /** The model that answered. */
  model: string;
  error: string;
  /** Why it failed: auth and setup mean Settings needs a look. */
  errorCode: string;
  /** On-demand breakdowns, keyed by command index. */
  explains: Partial<Record<number, ExplainState>>;
}

/** Thread survives a reload (sessionStorage) but not a new tab; the board is what's kept. */
const KEY = 'th.thread';
const MAX_TURNS = 40;
const HISTORY_TURNS = 4;

@Injectable({ providedIn: 'root' })
export class AskStore {
  private readonly api = inject(AssistantApi);
  private readonly settings = inject(Settings);
  private readonly board = inject(BoardStore);

  readonly turns = signal<Turn[]>(loadThread());
  readonly busy = computed(() => this.turns().some((t) => t.status === 'streaming'));

  private current: AbortController | null = null;
  private readonly explaining = new Map<string, AbortController>();

  constructor() {
    // Saved between answers, not on every streamed frame.
    effect(() => {
      const turns = this.turns();
      if (!turns.some((t) => t.status === 'streaming')) writeJson(session(), KEY, turns);
    });
  }

  /** Asking while an answer is still streaming stops that one and starts the new question. */
  ask(question: string) {
    question = question.trim();
    if (!question) return;
    this.stop();
    const env = this.settings.env();
    const turn: Turn = {
      id: newId(),
      question,
      verbose: this.settings.verbose(),
      env: envLabel(env),
      status: 'streaming',
      raw: '',
      answer: EMPTY_ANSWER,
      stopReason: null,
      model: '',
      error: '',
      errorCode: '',
      explains: {},
    };
    const history = this.historyBefore(this.turns().length);
    this.turns.update((turns) => [...turns, turn].slice(-MAX_TURNS));
    void this.run(turn, history);
  }

  retry(id: string) {
    const index = this.turns().findIndex((t) => t.id === id);
    if (index === -1) return;
    this.stop();
    const turn: Turn = {
      ...this.turns()[index],
      env: this.settings.envLabel(),
      status: 'streaming',
      raw: '',
      answer: EMPTY_ANSWER,
      stopReason: null,
      model: '',
      error: '',
      errorCode: '',
      explains: {},
    };
    this.patch(id, turn);
    void this.run(turn, this.historyBefore(index));
  }

  stop() {
    this.current?.abort();
  }

  clear() {
    this.stop();
    this.explaining.forEach((controller) => controller.abort());
    this.turns.set([]);
  }

  /** Fetches a man-page style breakdown for one command of a concise answer. */
  async explain(turnId: string, index: number) {
    const turn = this.turns().find((t) => t.id === turnId);
    const command = turn?.answer.commands[index]?.command;
    if (!turn || !command) return;

    const key = `${turnId}:${index}`;
    this.explaining.get(key)?.abort();
    const controller = new AbortController();
    this.explaining.set(key, controller);

    const setExplain = (state: ExplainState) =>
      this.turns.update((turns) =>
        turns.map((t) => (t.id === turnId ? { ...t, explains: { ...t.explains, [index]: state } } : t)),
      );
    const empty: Explanation = { explanation: '', parts: [] };
    let latest = empty;
    setExplain({ status: 'streaming', explanation: empty, error: '' });

    try {
      const { value } = await this.api.stream(
        '/api/explain',
        { command, env: toApiEnv(this.settings.env()) },
        controller.signal,
        toExplanation,
        (partial) => setExplain({ status: 'streaming', explanation: (latest = partial), error: '' }),
      );
      setExplain({ status: 'done', explanation: value, error: '' });
      // Pinned before the breakdown arrived? Give the note the breakdown too.
      const note = this.board.findByOrigin(key);
      if (note && !note.parts.length) this.board.update(note.id, value);
    } catch (error) {
      if (controller.signal.aborted) return;
      setExplain({ status: 'error', explanation: latest, error: messageOf(error) });
    } finally {
      if (this.explaining.get(key) === controller) this.explaining.delete(key);
    }
  }

  /** Previous successful answers, newest last, for follow-up questions like "and recursively?". */
  private historyBefore(index: number) {
    return this.turns()
      .slice(0, index)
      .filter((t) => t.status === 'done' && t.raw)
      .slice(-HISTORY_TURNS)
      .map((t) => ({ question: t.question, answer: t.raw }));
  }

  private async run(turn: Turn, history: { question: string; answer: string }[]) {
    const controller = new AbortController();
    this.current = controller;
    try {
      const result = await this.api.stream(
        '/api/ask',
        { question: turn.question, env: toApiEnv(this.settings.env()), verbose: turn.verbose, history },
        controller.signal,
        toAnswer,
        (answer, raw) => this.patch(turn.id, { answer, raw }),
      );
      this.patch(turn.id, {
        status: 'done',
        answer: result.value,
        raw: result.raw,
        stopReason: result.stopReason,
        model: result.model,
      });
    } catch (error) {
      this.patch(
        turn.id,
        controller.signal.aborted
          ? { status: 'stopped' }
          : { status: 'error', error: messageOf(error), errorCode: error instanceof ApiError ? error.code : '' },
      );
    } finally {
      if (this.current === controller) this.current = null;
    }
  }

  private patch(id: string, patch: Partial<Turn>) {
    this.turns.update((turns) => turns.map((t) => (t.id === id ? { ...t, ...patch } : t)));
  }
}

/** The breakdown to show for a command: from a verbose answer, else from an on-demand explain. */
export function breakdownOf(turn: Turn, index: number, command: CommandItem): Explanation {
  if (command.explanation || command.parts.length) {
    return { explanation: command.explanation, parts: command.parts };
  }
  return turn.explains[index]?.explanation ?? { explanation: '', parts: [] as Part[] };
}

function messageOf(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  return 'Something went wrong. Try again.';
}

function loadThread(): Turn[] {
  const turns = readJson<Turn[]>(session(), KEY);
  if (!Array.isArray(turns)) return [];
  // A reload cuts off whatever was streaming.
  return turns.map((t) => ({
    ...t,
    model: t.model ?? '',
    errorCode: t.errorCode ?? '',
    status: t.status === 'streaming' ? 'stopped' : t.status,
    explains: Object.fromEntries(Object.entries(t.explains ?? {}).filter(([, e]) => e?.status === 'done')),
  }));
}
