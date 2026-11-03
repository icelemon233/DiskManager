export type Theme = 'system' | 'light' | 'dark';
export interface WindowPreferences {
  theme: Theme;
  nativeTheme: 'light' | 'dark';
  closeBehavior: 'tray' | 'quit';
  qa?: boolean;
  error?: string;
}
export interface FileEntry {
  path: string;
  parent: string;
  name: string;
  directory: boolean;
  size: number;
  allocated: number;
  modified: number;
  created: number;
  identity: string;
  attributes: number;
  links: number;
  issue: string | null;
  extension: string;
  onlineOnly: boolean;
  reparse: boolean;
}
export interface Entry {
  file: FileEntry;
  totalSize: number;
  totalAllocated: number;
  fileCount: number;
  folderCount?: number | null;
}
export interface MapNode {
  entry: Entry;
  children: MapNode[];
}
export interface Snapshot {
  id: string;
  label: string;
  roots: string[];
  created: number;
  count: number;
  size: number;
  allocated: number;
  status: string;
  method: string;
  durationMs: number;
  pinned: boolean;
  imported: boolean;
  folderCount?: number;
  timing?: {
    permissionMs: number;
    readMs: number;
    aggregateMs: number;
    readyMs: number;
    saveMs: number | null;
  } | null;
}
export interface Preferences {
  retainCount: number;
  historyBudget: number;
  preferredDirectories: string[] | null;
}
export interface Rule {
  id: string;
  name: string;
  roots: string[];
  pattern: string;
  extension: string;
  minSize: number;
  maxSize: number | null;
  olderDays: number | null;
  exclusions: string[];
}
export interface Overview {
  scanAuthorization?: { state: string; message: string | null };
  saveState?: string;
  drives: {
    root: string;
    label: string;
    format: string;
    total: number;
    free: number;
    ready: boolean;
  }[];
  current: Snapshot | null;
  preferences: Preferences;
  rules: Rule[];
  notice: string | null;
}
export interface Summary {
  count: number;
  size: number;
  allocated: number;
  types: { extension: string; count: number; size: number; allocated: number }[];
  issues: string[];
}
export interface Query {
  scope?: string | null;
  snapshot?: string;
  parent?: string | null;
  search?: string;
  extension?: string;
  minSize?: number;
  maxSize?: number | null;
  before?: number | null;
  after?: number | null;
  sort?: string;
  descending?: boolean;
  offset?: number;
  limit?: number;
  filesOnly?: boolean;
  directoriesOnly?: boolean;
  mode?: string;
  channel?: string;
}
export interface QueryResult {
  count: number;
  rows: Entry[];
}
export interface Job {
  id: string;
  kind: string;
  state: 'queued' | 'running' | 'complete' | 'failed' | 'cancelled';
  phase: string;
  completed: number;
  total: number | null;
  issues: number;
  error: string | null;
  result: unknown;
  started: number;
}
export interface OperationItem {
  path: string;
  destination: string | null;
  size: number;
  allocated: number;
  error: string | null;
}
export interface OperationPlan {
  id: string;
  action: string;
  created: number;
  items: OperationItem[];
  warnings: string[];
}
export interface CacheTarget {
  id: string;
  name: string;
  kind: string;
  root: string;
  impact: string;
  tool: string | null;
}
export interface CachePlan {
  id: string;
  target: CacheTarget;
  created: number;
}
export interface DuplicateGroup {
  id: string;
  hash: string;
  size: number;
  recoverable: number;
  files: FileEntry[];
  suggestedKeep: string;
}
export interface DuplicateResult {
  session: string;
  count: number;
  rows: DuplicateGroup[];
  skipped: string[];
}
export interface DiffRow {
  path: string;
  oldPath: string | null;
  delta: number;
  size: number;
  kind: string;
}
export interface OperationLog {
  id: string;
  action: string;
  time: number;
  name?: string;
  root?: string;
  output?: string;
  results?: { path: string; destination: string | null; state: string; error: string | null }[];
}
export interface NativeEvent {
  name: string;
  data: unknown;
}
export interface Bridge {
  startup: WindowPreferences;
  call(method: string, params?: object): Promise<unknown>;
  folders(): Promise<string[]>;
  reveal(path: string): Promise<void>;
  copy(path: string): Promise<void>;
  execute(id: string, kind: 'files' | 'tool'): Promise<unknown>;
  export(kind: 'csv' | 'snapshot', query: Query): Promise<unknown>;
  import(): Promise<unknown>;
  png(image: string): Promise<string | null>;
  preferences(patch: Partial<WindowPreferences>): Promise<WindowPreferences>;
  window(action: string): void;
  subscribe(callback: (event: NativeEvent) => void): () => void;
}
declare global {
  interface Window {
    diskManager?: Bridge;
  }
}
