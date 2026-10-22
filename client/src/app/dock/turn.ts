import { Component, computed, inject, input, signal } from '@angular/core';
import { AskStore, breakdownOf, Turn } from '../core/ask-store';
import { BoardStore } from '../core/board-store';
import { CommandItem, Explanation } from '../core/models';
import { Toasts } from '../core/toasts';
import { copyText } from '../core/util';
import { SettingsDialogState } from '../topbar/settings-dialog';
import { CommandCode } from '../ui/command-code';
import { Icon } from '../ui/icon';

/** One question and its answer: the command(s), plus a breakdown in verbose mode or on demand. */
@Component({
  selector: 'app-turn',
  imports: [CommandCode, Icon],
  templateUrl: './turn.html',
  styleUrl: './turn.css',
})
export class TurnView {
  readonly turn = input.required<Turn>();

  protected readonly ask = inject(AskStore);
  protected readonly settingsDialog = inject(SettingsDialogState);
  private readonly board = inject(BoardStore);
  private readonly toasts = inject(Toasts);

  protected readonly copied = signal<number | null>(null);
  protected readonly streaming = computed(() => this.turn().status === 'streaming');
  /** A lone command needs no title; steps and verbose answers get one. */
  protected readonly showLabels = computed(() => this.turn().answer.commands.length > 1 || this.turn().verbose);
  protected readonly empty = computed(() => {
    const { answer, commands, note } = this.turn().answer;
    return !answer && !commands.length && !note;
  });

  protected breakdown(index: number, command: CommandItem): Explanation {
    return breakdownOf(this.turn(), index, command);
  }

  protected isPinned(index: number) {
    return this.board.origins().has(this.origin(index));
  }

  protected async copy(index: number, command: string) {
    if (await copyText(command)) {
      this.copied.set(index);
      setTimeout(() => this.copied.update((i) => (i === index ? null : i)), 1200);
    } else {
      this.toasts.show("Couldn't copy to the clipboard", { kind: 'error' });
    }
  }

  protected togglePin(index: number, command: CommandItem) {
    const origin = this.origin(index);
    if (this.isPinned(index)) {
      this.board.removeByOrigin(origin);
      return;
    }
    const turn = this.turn();
    const { explanation, parts } = this.breakdown(index, command);
    this.board.add({
      label: command.label || turn.question,
      command: command.command,
      where: command.where,
      explanation,
      parts,
      question: turn.question,
      env: turn.env,
      origin,
    });
  }

  private origin(index: number) {
    return `${this.turn().id}:${index}`;
  }
}
