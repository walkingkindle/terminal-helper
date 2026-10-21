import { Injectable, inject, signal } from '@angular/core';
import { AssistantApi, Health } from './api';

/** Whether the server is reachable and has an API key; drives the dock's banner and the key form. */
@Injectable({ providedIn: 'root' })
export class ServerStatus {
  private readonly api = inject(AssistantApi);

  /** undefined while checking, null when the server can't be reached. */
  readonly health = signal<Health | null | undefined>(undefined);

  async refresh(): Promise<Health | null> {
    const health = await this.api.health();
    this.health.set(health);
    return health;
  }
}
