// Small formatters shared by the tree, the status bar and the overview. No VS Code imports, so unit tests run in Node.

/** 3276 → "3,276"; 2533862 → "2.5M"; the compact form is for tiles and badges. */
export function count(n: number, compact = false): string {
  if (!compact || Math.abs(n) < 10_000) {
    return n.toLocaleString('en-US');
  }
  const units: [number, string][] = [[1e9, 'B'], [1e6, 'M'], [1e3, 'k']];
  for (const [size, unit] of units) {
    if (Math.abs(n) >= size) {
      const value = n / size;
      return `${value >= 100 ? Math.round(value) : Number(value.toFixed(1))}${unit}`;
    }
  }
  return String(n);
}

/** 1536 → "1.5 KB"; for the daemon's memory. */
export function bytes(n: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = n;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 ? value : value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`;
}

/** "just now", "5 min ago", "3 h ago", "2 days ago"; a date older than a week is written out. */
export function ago(iso: string | undefined, now: Date = new Date()): string {
  if (!iso) {
    return 'never';
  }
  const then = new Date(iso);
  if (Number.isNaN(then.getTime())) {
    return iso;
  }
  const seconds = Math.max(0, (now.getTime() - then.getTime()) / 1000);
  if (seconds < 45) {
    return 'just now';
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)} min ago`;
  }
  if (seconds < 86_400) {
    return `${Math.round(seconds / 3600)} h ago`;
  }
  if (seconds < 7 * 86_400) {
    const days = Math.round(seconds / 86_400);
    return `${days} day${days === 1 ? '' : 's'} ago`;
  }
  return then.toISOString().slice(0, 10);
}

/** The first seven characters of a commit, or "?" when the graph does not record one. */
export function shortCommit(commit: string | undefined): string {
  return commit ? commit.slice(0, 7) : '?';
}

/** "csharp" → "C#"; the graph's language names, as people write them. */
export function languageName(language: string): string {
  const names: Record<string, string> = {
    csharp: 'C#',
    typescript: 'TypeScript',
    javascript: 'JavaScript',
    java: 'Java',
    kotlin: 'Kotlin',
    scala: 'Scala',
    python: 'Python',
    go: 'Go',
    rust: 'Rust',
  };
  return names[language] ?? language;
}

/** "1 file" / "3 files". */
export function plural(n: number, word: string, many = `${word}s`): string {
  return `${count(n)} ${n === 1 ? word : many}`;
}
