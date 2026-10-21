import {
  afterRenderEffect,
  Component,
  effect,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { AssistantApi, Health } from '../core/api';
import { AskStore } from '../core/ask-store';
import { Settings } from '../core/settings';
import { Icon } from '../ui/icon';
import { EnvMenu } from './env-menu';
import { TurnView } from './turn';

/** The docked panel under the board: the conversation and the question box. */
@Component({
  selector: 'app-dock',
  imports: [EnvMenu, Icon, TurnView],
  templateUrl: './dock.html',
  styleUrl: './dock.css',
})
export class Dock {
  protected readonly ask = inject(AskStore);
  protected readonly settings = inject(Settings);
  private readonly api = inject(AssistantApi);
  private readonly input = viewChild.required<ElementRef<HTMLTextAreaElement>>('input');
  private readonly thread = viewChild.required<ElementRef<HTMLElement>>('thread');

  protected readonly draft = signal('');
  /** undefined while checking, null when the API can't be reached. */
  protected readonly health = signal<Health | null | undefined>(undefined);
  protected readonly examples = [
    'print the working directory',
    'append a line to a file',
    'ssh into a Windows machine',
    'find files bigger than 100 MB',
    "what's using port 3000?",
  ];

  /** Follow the answer as it streams, unless the user scrolled up to read something. */
  private followAnswer = true;

  constructor() {
    // Checked on load and after every answer. While something is wrong (API down, no key) it keeps
    // checking, so the banner goes away by itself once the server is fixed and restarted.
    effect((onCleanup) => {
      if (this.ask.busy()) return;
      let stopped = false;
      let timer: ReturnType<typeof setTimeout> | undefined;
      const check = async () => {
        const health = await this.api.health();
        if (stopped) return;
        this.health.set(health);
        if (!health?.credentials) timer = setTimeout(check, 4000);
      };
      void check();
      onCleanup(() => {
        stopped = true;
        clearTimeout(timer);
      });
    });
    afterRenderEffect(() => {
      this.ask.turns();
      const thread = this.thread().nativeElement;
      if (this.followAnswer) thread.scrollTop = thread.scrollHeight;
    });
  }

  focusInput() {
    this.input().nativeElement.focus();
  }

  protected submit() {
    if (!this.draft().trim()) return;
    this.ask.ask(this.draft());
    this.draft.set('');
    this.followAnswer = true;
    requestAnimationFrame(() => this.autosize());
  }

  protected askExample(question: string) {
    this.ask.ask(question);
    this.followAnswer = true;
  }

  protected onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      this.submit();
    }
  }

  protected onInput(value: string) {
    this.draft.set(value);
    this.autosize();
  }

  protected onScroll() {
    const thread = this.thread().nativeElement;
    this.followAnswer = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 40;
  }

  private autosize() {
    const area = this.input().nativeElement;
    area.style.height = 'auto';
    area.style.height = `${Math.min(area.scrollHeight, 160)}px`;
  }
}
