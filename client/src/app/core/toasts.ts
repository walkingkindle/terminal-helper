import { Injectable, signal } from '@angular/core';

export interface Toast {
  id: number;
  message: string;
  kind: 'info' | 'error';
  action?: { label: string; run: () => void };
}

@Injectable({ providedIn: 'root' })
export class Toasts {
  private nextId = 1;
  readonly items = signal<Toast[]>([]);

  show(
    message: string,
    options: { kind?: Toast['kind']; action?: Toast['action']; duration?: number } = {},
  ) {
    const toast: Toast = { id: this.nextId++, message, kind: options.kind ?? 'info', action: options.action };
    this.items.update((items) => [...items.slice(-2), toast]);
    setTimeout(() => this.dismiss(toast.id), options.duration ?? (toast.action ? 6000 : 2500));
  }

  dismiss(id: number) {
    this.items.update((items) => items.filter((t) => t.id !== id));
  }
}
