/** One piece of a command, man-page style: `-v` → "verbose: print debug info". */
export interface Part {
  token: string;
  meaning: string;
}

/** One command in an answer. `explanation` and `parts` are only filled in verbose mode. */
export interface CommandItem {
  command: string;
  label: string;
  where: string;
  explanation: string;
  parts: Part[];
}

/** The structured answer the API streams back (see server/…/Prompts.cs for the schema). */
export interface Answer {
  answer: string;
  commands: CommandItem[];
  note: string;
}

export interface Explanation {
  explanation: string;
  parts: Part[];
}

export const NOTE_COLORS = [
  'yellow',
  'orange',
  'red',
  'pink',
  'purple',
  'blue',
  'green',
  'brown',
  'gray',
] as const;

export type NoteColor = (typeof NOTE_COLORS)[number];

/** A sticky note on the board. */
export interface Note {
  id: string;
  label: string;
  command: string;
  where: string;
  explanation: string;
  parts: Part[];
  /** The user's own notes about the command. */
  memo: string;
  /** The question that produced it, if it came from an answer. */
  question: string;
  /** Environment label at pin time, e.g. "Arch Linux · bash". */
  env: string;
  color: NoteColor;
  /** `turnId:index` of the answer it was pinned from, so the Pin button can toggle. */
  origin: string;
  createdAt: number;
  updatedAt: number;
}
