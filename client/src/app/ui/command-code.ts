import { Component, computed, input } from '@angular/core';
import { tokenizeShell } from '../core/shell-tokens';

/** A shell command with light syntax highlighting. Rendered as text nodes, never as HTML. */
@Component({
  selector: 'app-command-code',
  // Short flags never wrap at their own hyphen ("-" / "Online"); long ones still may.
  template: `@for (t of tokens(); track $index) {<span [class]="t.kind" [class.nowrap]="t.kind === 'flag' && t.text.length <= 24">{{ t.text }}</span>}@if (caret()) {<span class="caret" aria-hidden="true"></span>}`,
  host: { class: 'sh' },
  styles: `
    :host {
      font-family: var(--mono);
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      tab-size: 4;
    }
    .cmd {
      color: var(--tok-cmd);
      font-weight: 600;
    }
    .flag {
      color: var(--tok-flag);
    }
    .nowrap {
      white-space: nowrap;
    }
    .str {
      color: var(--tok-str);
    }
    .var {
      color: var(--tok-var);
    }
    .op {
      color: var(--tok-op);
    }
    .ph {
      color: var(--tok-ph);
      font-style: italic;
    }
    .comment {
      color: var(--tok-comment);
      font-style: italic;
    }
    .caret {
      display: inline-block;
      width: 0.55em;
      height: 1.1em;
      margin-left: 1px;
      vertical-align: text-bottom;
      background: currentColor;
      opacity: 0.55;
      animation: blink 1s steps(1) infinite;
    }
    @keyframes blink {
      50% {
        opacity: 0;
      }
    }
  `,
})
export class CommandCode {
  readonly code = input.required<string>();
  readonly caret = input(false);
  protected readonly tokens = computed(() => tokenizeShell(this.code()));
}
