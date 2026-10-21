export type Os = 'linux' | 'macos' | 'windows';

/** The user's machine. Every answer is written for this. */
export interface EnvSettings {
  os: Os;
  /** Linux distro, or the WSL distro when the Windows shell is WSL. */
  distro: string;
  shell: string;
  /** Free text the assistant should assume, e.g. "I use yay for AUR packages". */
  notes: string;
}

/** What the API expects (labels, not ids). */
export interface ApiEnv {
  os: string;
  distro: string;
  shell: string;
  notes: string;
}

export const OS_LIST: readonly { id: Os; label: string }[] = [
  { id: 'linux', label: 'Linux' },
  { id: 'macos', label: 'macOS' },
  { id: 'windows', label: 'Windows' },
];

export const SHELLS: Record<Os, readonly string[]> = {
  linux: ['bash', 'zsh', 'fish', 'nushell', 'sh'],
  macos: ['zsh', 'bash', 'fish', 'nushell'],
  windows: ['PowerShell', 'Command Prompt', 'Git Bash', 'WSL'],
};

export const DISTROS: readonly string[] = [
  'Arch Linux',
  'CachyOS',
  'Debian',
  'EndeavourOS',
  'Fedora',
  'Gentoo',
  'Linux Mint',
  'Manjaro',
  'NixOS',
  'openSUSE',
  'Pop!_OS',
  'Ubuntu',
  'Void Linux',
  'Alpine',
];

export const WSL_DEFAULT_DISTRO = 'Ubuntu';

const OS_LABEL: Record<Os, string> = { linux: 'Linux', macos: 'macOS', windows: 'Windows' };

export const usesDistro = (env: EnvSettings) =>
  env.os === 'linux' || (env.os === 'windows' && env.shell === 'WSL');

/** Short label for chips and note tags, e.g. "Arch Linux · bash" or "Windows · Git Bash". */
export function envLabel(env: EnvSettings): string {
  switch (env.os) {
    case 'linux':
      return `${env.distro || 'Linux'} · ${env.shell}`;
    case 'macos':
      return `macOS · ${env.shell}`;
    case 'windows':
      return env.shell === 'WSL'
        ? `Windows · WSL ${env.distro || WSL_DEFAULT_DISTRO}`
        : `Windows · ${env.shell}`;
  }
}

export function toApiEnv(env: EnvSettings): ApiEnv {
  const distro = usesDistro(env)
    ? env.distro || (env.os === 'windows' ? WSL_DEFAULT_DISTRO : '')
    : '';
  return { os: OS_LABEL[env.os], distro, shell: env.shell, notes: env.notes.trim() };
}

/** Switching OS keeps the shell when it still makes sense, otherwise picks that OS's default. */
export function withOs(env: EnvSettings, os: Os): EnvSettings {
  const shell = SHELLS[os].includes(env.shell) ? env.shell : SHELLS[os][0];
  return { ...env, os, shell };
}

/** First-run guess from the browser. The distro can't be detected, so the UI nudges for it. */
export function detectEnv(): EnvSettings {
  const nav = navigator as Navigator & { userAgentData?: { platform?: string } };
  const platform = (nav.userAgentData?.platform || nav.platform || nav.userAgent).toLowerCase();
  const os: Os = platform.includes('win') ? 'windows' : platform.includes('mac') ? 'macos' : 'linux';
  return { os, distro: '', shell: SHELLS[os][0], notes: '' };
}

/** Validates settings read from storage; anything odd falls back to the detected defaults. */
export function sanitizeEnv(value: unknown): EnvSettings {
  const fallback = detectEnv();
  if (!value || typeof value !== 'object') return fallback;
  const v = value as Partial<Record<keyof EnvSettings, unknown>>;
  const os = OS_LIST.some((o) => o.id === v.os) ? (v.os as Os) : fallback.os;
  const shell = typeof v.shell === 'string' && SHELLS[os].includes(v.shell) ? v.shell : SHELLS[os][0];
  const text = (x: unknown, max: number) => (typeof x === 'string' ? x.slice(0, max) : '');
  return { os, shell, distro: text(v.distro, 60), notes: text(v.notes, 500) };
}
