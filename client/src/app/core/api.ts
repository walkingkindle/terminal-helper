import { Injectable } from '@angular/core';
import { parseFinal } from './answer';
import { ApiEnv } from './env';
import { parsePartialJson } from './partial-json';
import { readSse } from './sse';
import { frameThrottle } from './util';

export interface AskBody {
  question: string;
  env: ApiEnv;
  verbose: boolean;
  history: { question: string; answer: string }[];
}

export interface ExplainBody {
  command: string;
  env: ApiEnv;
}

export interface Health {
  /** The provider that answers: anthropic, openai or local. */
  provider: string;
  model: string;
  /** False until that provider has what it needs (a key, a model, a server). */
  ready: boolean;
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly code: string,
  ) {
    super(message);
  }
}

export interface StreamResult<T> {
  value: T;
  raw: string;
  stopReason: string | null;
  /** The model that answered, e.g. claude-haiku-5-5 or gpt-5.4-mini. */
  model: string;
}

@Injectable({ providedIn: 'root' })
export class AssistantApi {
  async health(): Promise<Health | null> {
    try {
      const response = await fetch('/api/health');
      return response.ok ? ((await response.json()) as Health) : null;
    } catch {
      return null;
    }
  }

  /**
   * POSTs and streams back a JSON answer. `onPartial` gets the best-effort parse of what
   * has arrived so far (at most once per frame); the promise resolves with the final value.
   */
  async stream<T>(
    path: '/api/ask' | '/api/explain',
    body: AskBody | ExplainBody,
    signal: AbortSignal,
    map: (value: unknown) => T,
    onPartial: (value: T, raw: string) => void,
  ): Promise<StreamResult<T>> {
    let response: Response;
    try {
      response = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
        body: JSON.stringify(body),
        signal,
      });
    } catch (error) {
      if (signal.aborted) throw error;
      throw new ApiError("Can't reach the server. Is the API running? (see README)", 'network');
    }
    if (!response.ok || !response.body) throw await toApiError(response);

    let raw = '';
    const flush = frameThrottle(() => onPartial(map(parsePartialJson(raw)), raw));
    try {
      for await (const event of readSse(response.body)) {
        const data = JSON.parse(event.data) as Record<string, unknown>;
        if (event.event === 'delta') {
          raw += data['text'] as string;
          flush();
        } else if (event.event === 'done') {
          flush.cancel();
          return {
            value: map(parseFinal(raw)),
            raw,
            stopReason: (data['stopReason'] as string) ?? null,
            model: (data['model'] as string) ?? '',
          };
        } else if (event.event === 'error') {
          throw new ApiError(data['message'] as string, data['code'] as string);
        }
      }
      throw new ApiError('The connection dropped before the answer finished.', 'network');
    } catch (error) {
      flush.cancel();
      // Hand back whatever arrived, so a stopped or failed answer still shows its partial text.
      if (raw) onPartial(map(parsePartialJson(raw)), raw);
      if (error instanceof ApiError || signal.aborted) throw error;
      throw new ApiError('The connection dropped before the answer finished.', 'network');
    }
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  if (response.status === 429) {
    return new ApiError('Slow down a little — too many questions this minute.', 'rate_limit');
  }
  if (response.status === 400) {
    try {
      const problem = (await response.json()) as { errors?: Record<string, string[]> };
      const [field, messages] = Object.entries(problem.errors ?? {})[0] ?? [];
      if (field) return new ApiError(`${field}: ${messages?.[0] ?? 'invalid'}`, 'bad_request');
    } catch {
      /* fall through */
    }
    return new ApiError('The server rejected that question.', 'bad_request');
  }
  // The dev server's proxy answers 5xx/404 when the .NET API isn't running.
  return new ApiError(
    `The API server isn't responding (HTTP ${response.status}). Is it running? (see README)`,
    'network',
  );
}
