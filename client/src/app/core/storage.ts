/**
 * Web Storage that never throws: private windows, disabled storage and full quotas all
 * degrade to "nothing saved" instead of breaking the app.
 */
export const local = (): Storage | undefined => {
  try {
    return window.localStorage;
  } catch {
    return undefined;
  }
};

export const session = (): Storage | undefined => {
  try {
    return window.sessionStorage;
  } catch {
    return undefined;
  }
};

export function readJson<T>(store: Storage | undefined, key: string): T | undefined {
  try {
    const raw = store?.getItem(key);
    return raw ? (JSON.parse(raw) as T) : undefined;
  } catch {
    return undefined;
  }
}

/** Returns false when the write failed (usually a full quota). */
export function writeText(store: Storage | undefined, key: string, value: string): boolean {
  try {
    store?.setItem(key, value);
    return store !== undefined;
  } catch {
    return false;
  }
}

export function writeJson(store: Storage | undefined, key: string, value: unknown): boolean {
  return writeText(store, key, JSON.stringify(value));
}
