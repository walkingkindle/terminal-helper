/**
 * Time-sortable random id. Uses getRandomValues rather than randomUUID because the
 * latter only exists on https/localhost, and this may be opened over plain http on a LAN.
 */
export function newId(): string {
  const random = Array.from(crypto.getRandomValues(new Uint8Array(6)), (b) =>
    b.toString(16).padStart(2, '0'),
  ).join('');
  return `${Date.now().toString(36)}-${random}`;
}

/** Copies text, falling back to execCommand where the async clipboard API is unavailable (plain http). */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.cssText = 'position:fixed;top:0;left:0;opacity:0';
    document.body.append(area);
    area.select();
    try {
      return document.execCommand('copy');
    } catch {
      return false;
    } finally {
      area.remove();
    }
  }
}

/** Runs `fn` at most once per animation frame, so a fast token stream doesn't re-render per chunk. */
export function frameThrottle(fn: () => void): (() => void) & { cancel(): void } {
  let handle = 0;
  const schedule = () => {
    if (!handle) {
      handle = requestAnimationFrame(() => {
        handle = 0;
        fn();
      });
    }
  };
  return Object.assign(schedule, {
    cancel: () => {
      if (handle) cancelAnimationFrame(handle);
      handle = 0;
    },
  });
}

export function timeAgo(timestamp: number, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - timestamp) / 1000));
  if (seconds < 60) return 'just now';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 7) return `${days}d ago`;
  const date = new Date(timestamp);
  const sameYear = date.getFullYear() === new Date(now).getFullYear();
  return date.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    year: sameYear ? undefined : 'numeric',
  });
}

export const isMac = () => /mac|iphone|ipad/i.test(navigator.platform || navigator.userAgent);

/** True when a key press is meant for a text field rather than for a page shortcut. */
export function isTyping(event: KeyboardEvent): boolean {
  const target = event.target as HTMLElement | null;
  return !!target?.closest('input, textarea, select, [contenteditable="true"]');
}
