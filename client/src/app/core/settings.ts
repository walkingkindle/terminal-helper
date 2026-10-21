import { computed, effect, Injectable, signal } from '@angular/core';
import { envLabel, EnvSettings, sanitizeEnv } from './env';
import { local, readJson, writeJson } from './storage';

export type Theme = 'system' | 'light' | 'dark';

/** Also read by the inline script in index.html to set the theme before first paint. */
const KEY = 'th.settings';

interface Stored {
  env?: unknown;
  verbose?: boolean;
  theme?: Theme;
  envConfirmed?: boolean;
}

@Injectable({ providedIn: 'root' })
export class Settings {
  private readonly stored = readJson<Stored>(local(), KEY) ?? {};

  readonly env = signal<EnvSettings>(sanitizeEnv(this.stored.env));
  readonly verbose = signal(this.stored.verbose === true);
  readonly theme = signal<Theme>(
    this.stored.theme === 'light' || this.stored.theme === 'dark' ? this.stored.theme : 'system',
  );
  /** False until the user has opened the environment menu once; the distro can't be auto-detected. */
  readonly envConfirmed = signal(this.stored.envConfirmed === true);

  readonly envLabel = computed(() => envLabel(this.env()));

  constructor() {
    effect(() => {
      writeJson(local(), KEY, {
        env: this.env(),
        verbose: this.verbose(),
        theme: this.theme(),
        envConfirmed: this.envConfirmed(),
      } satisfies Stored);
    });
    effect(() => {
      const theme = this.theme();
      const root = document.documentElement;
      if (theme === 'system') delete root.dataset['theme'];
      else root.dataset['theme'] = theme;
    });
  }
}
