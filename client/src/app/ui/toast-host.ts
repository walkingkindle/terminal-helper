import { Component, inject } from '@angular/core';
import { Toasts } from '../core/toasts';

@Component({
  selector: 'app-toast-host',
  template: `
    @for (toast of toasts.items(); track toast.id) {
      <div class="toast" [class.error]="toast.kind === 'error'" role="status">
        <span>{{ toast.message }}</span>
        @if (toast.action; as action) {
          <button type="button" (click)="action.run(); toasts.dismiss(toast.id)">{{ action.label }}</button>
        }
      </div>
    }
  `,
  host: { 'aria-live': 'polite' },
  styles: `
    :host {
      position: fixed;
      top: 52px;
      left: 50%;
      z-index: 50;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 8px;
      transform: translateX(-50%);
      pointer-events: none;
    }
    .toast {
      display: flex;
      align-items: center;
      gap: 12px;
      max-width: min(480px, calc(100vw - 32px));
      padding: 8px 10px 8px 14px;
      border-radius: 8px;
      background: var(--toast-bg);
      color: var(--toast-text);
      font-size: 13.5px;
      box-shadow: var(--shadow-menu);
      pointer-events: auto;
      animation: toast-in 0.18s ease-out;
    }
    .toast.error {
      background: var(--danger);
      color: #fff;
    }
    button {
      padding: 3px 8px;
      border: 0;
      border-radius: 5px;
      background: rgba(255, 255, 255, 0.14);
      color: inherit;
      font: inherit;
      font-weight: 600;
      cursor: pointer;
    }
    button:hover {
      background: rgba(255, 255, 255, 0.24);
    }
    @keyframes toast-in {
      from {
        opacity: 0;
        transform: translateY(-6px);
      }
    }
  `,
})
export class ToastHost {
  protected readonly toasts = inject(Toasts);
}
