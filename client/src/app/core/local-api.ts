import { Injectable } from '@angular/core';
import { ApiError } from './api';

export type ProviderId = 'anthropic' | 'openai' | 'local';

export interface KeyState {
  set: boolean;
  /** The last four characters, e.g. "…3fQx". */
  hint: string | null;
  source: 'saved' | 'config' | 'environment' | null;
}

/** One AI provider as Settings shows it. */
export interface ProviderState {
  id: ProviderId;
  name: string;
  /** The one that answers questions. */
  active: boolean;
  /** Has what it needs: a key, a model, a server. */
  ready: boolean;
  model: string;
  baseUrl: string | null;
  requiresKey: boolean;
  editableBaseUrl: boolean;
  apiKey: KeyState;
}

export interface AppSettings {
  active: ProviderId;
  providers: ProviderState[];
  dataDir: string;
}

/** Leave a field out to keep what's saved. */
export interface ProviderUpdate {
  apiKey?: string;
  model?: string;
  baseUrl?: string;
}

export interface ProviderSaved {
  provider: ProviderState;
  /** False when the provider couldn't be reached to check the key. */
  verified: boolean;
  models: string[];
}

export interface StoredBoard {
  revision: number;
  notes: unknown[];
}

export type SaveOutcome = { saved: true; revision: number } | { saved: false; board: StoredBoard };

/** The server's own storage: the board in notes.json, and the AI provider settings, both in the data folder. */
@Injectable({ providedIn: 'root' })
export class LocalApi {
  async board(): Promise<StoredBoard> {
    return (await send('GET', '/api/notes')).json() as Promise<StoredBoard>;
  }

  /** Takes the notes already serialized, since the caller needs that string anyway. */
  async saveBoard(revision: number, notesJson: string): Promise<SaveOutcome> {
    const response = await send('PUT', '/api/notes', `{"revision":${revision},"notes":${notesJson}}`, [409]);
    if (response.status === 409) return { saved: false, board: (await response.json()) as StoredBoard };
    return { saved: true, revision: ((await response.json()) as { revision: number }).revision };
  }

  async settings(): Promise<AppSettings> {
    return (await send('GET', '/api/settings')).json() as Promise<AppSettings>;
  }

  /** Checks the values with the provider, saves them and makes it the one that answers. */
  async saveProvider(id: ProviderId, update: ProviderUpdate): Promise<ProviderSaved> {
    return (await send('PUT', `/api/settings/providers/${id}`, JSON.stringify(update))).json() as Promise<ProviderSaved>;
  }

  async removeKey(id: ProviderId): Promise<ProviderState> {
    return (await send('DELETE', `/api/settings/providers/${id}/api-key`)).json() as Promise<ProviderState>;
  }

  /** What models a server offers, for the model picker. Saves nothing. */
  async models(id: ProviderId, probe: ProviderUpdate): Promise<string[]> {
    const response = await send('POST', `/api/settings/providers/${id}/models`, JSON.stringify(probe));
    return ((await response.json()) as { models: string[] }).models;
  }
}

async function send(method: string, path: string, body?: string, allowed: number[] = []): Promise<Response> {
  let response: Response;
  try {
    response = await fetch(path, {
      method,
      body,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
    });
  } catch {
    throw new ApiError("Can't reach the Terminal Helper server. Is it still running?", 'network');
  }
  if (!response.ok && !allowed.includes(response.status)) throw await problemOf(response);
  return response;
}

/** Turns an ASP.NET problem response into a readable message. */
async function problemOf(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as { detail?: string; title?: string; errors?: Record<string, string[]> };
    const firstError = Object.values(problem.errors ?? {})[0]?.[0];
    const message = firstError ?? problem.detail ?? problem.title;
    if (message) return new ApiError(message, String(response.status));
  } catch {
    /* not JSON */
  }
  return new ApiError(`The server answered HTTP ${response.status}.`, String(response.status));
}
