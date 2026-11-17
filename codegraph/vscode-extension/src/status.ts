// The shape of `codegraph-index status --json` (StatusCommand.cs). Optional fields are left out of the JSON when null.

export interface LiveStatus {
  pid: number;
  solution: string;
  dbPath: string;
  projects: number;
  documents: number;
  startedAt: string;
  lastIndexedAt?: string;
  lastIndexSeconds?: number;
  pending: number;
  indexing: boolean;
  loading: boolean;
  workingSetBytes?: number;
  managedBytes?: number;
  idleExitMinutes?: number;
}

export interface LanguageReport {
  name: string;
  nodes: number;
  files: number;
  target?: string;
  tool?: string;
  compiler?: string;
  newestLanguageVersion?: string;
  languageVersions?: string;
  compileErrors?: number;
  firstCompileError?: string;
  languageVersionErrors?: number;
  firstLanguageVersionError?: string;
  unmapped?: string;
  notes?: string[];
  indexedAt?: string;
}

export interface GraphStatus {
  solution?: string;
  worktreeRoot?: string;
  indexedAt?: string;
  mode?: string;
  schemaVersion?: string;
  schemaCurrent: boolean;
  commit?: string;
  head?: string;
  headMoved: boolean;
  stale: boolean;
  changedCount: number;
  changedFiles: string[];
  missingCount: number;
  missingFiles: string[];
  nodes: number;
  edges: number;
  languages: LanguageReport[];
}

export interface UsageReport {
  calls: number;
  errors: number;
  sourceTokens: number;
  resultTokens: number;
  tokensSaved: number;
  topTools: { tool: string; calls: number }[];
  allTimeCalls: number;
  firstCallAt: string;
  line: string;
}

export interface StatusReport {
  version: string;
  db: string;
  exists: boolean;
  graph?: GraphStatus;
  daemon?: LiveStatus;
  usage?: UsageReport;
  error?: string;
}

/** Parses the command's output. Throws when it is not the status JSON, so a wrong binary is reported, not rendered. */
export function parseStatus(text: string): StatusReport {
  const value = JSON.parse(text) as Partial<StatusReport>;
  if (typeof value !== 'object' || value === null || typeof value.db !== 'string' || typeof value.exists !== 'boolean') {
    throw new Error('codegraph-index status --json did not return a status report');
  }
  return value as StatusReport;
}

export type Health = 'missing' | 'error' | 'stale' | 'fresh';

/** One word for a graph's state, which picks its icon and colour everywhere. */
export function healthOf(status: StatusReport | undefined): Health {
  if (!status || !status.exists) {
    return 'missing';
  }
  if (status.error || !status.graph) {
    return 'error';
  }
  return status.graph.stale ? 'stale' : 'fresh';
}

export type DaemonState = 'stopped' | 'loading' | 'indexing' | 'watching';

export function daemonStateOf(daemon: LiveStatus | undefined): DaemonState {
  if (!daemon) {
    return 'stopped';
  }
  if (daemon.loading) {
    return 'loading';
  }
  return daemon.indexing || daemon.pending > 0 ? 'indexing' : 'watching';
}
