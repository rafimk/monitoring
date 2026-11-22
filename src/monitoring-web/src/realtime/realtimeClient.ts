export type QueryKey = readonly unknown[];

/** The slice of a SignalR HubConnection used here, so the reconnect/invalidation logic is testable without a server. */
export interface HubLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  on(method: string, handler: (...args: any[]) => void): void;
  onreconnecting(cb: () => void): void;
  onreconnected(cb: () => void): void;
  onclose(cb: () => void): void;
}

export interface RealtimeOptions {
  createConnection: () => HubLike;
  invalidate: (keys: QueryKey[]) => void;
  onConnectionChange: (connected: boolean) => void;
  /** Delay before the n-th failed attempt to (re)connect, capped at the last value. */
  retryDelaysMs?: number[];
  /** How long to coalesce a burst of notifications into one invalidation. */
  debounceMs?: number;
}

export const DEFAULT_RETRY_DELAYS_MS = [1_000, 2_000, 5_000, 10_000, 30_000];

/** Every query a dashboard shows: refetched after (re)connecting, because notifications sent while offline are lost. */
const ALL: QueryKey[] = [["summary"], ["workers"], ["jobs"], ["job"], ["runs"], ["run"]];

/**
 * One live connection for one authenticated session. Notifications only say WHAT changed; this turns them into query
 * invalidations so React Query refetches the authoritative data from the REST API.
 */
export class RealtimeClient {
  private conn: HubLike | null = null;
  private stopped = true;
  private connected = false;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private flushTimer: ReturnType<typeof setTimeout> | null = null;
  private pending = new Map<string, QueryKey>();
  private attempts = 0;
  /** Number of connections ever created: stays at 1 while reconnecting through the same connection object. */
  connectionsCreated = 0;

  constructor(private readonly o: RealtimeOptions) {}

  start(): void {
    if (!this.stopped) return; // idempotent: never two connections or two timers
    this.stopped = false;
    this.conn = this.o.createConnection();
    this.connectionsCreated++;
    const c = this.conn;

    c.on("RunChanged", (m: { runId?: string | null; jobKey?: string | null } = {}) => {
      this.queue(["runs"], ["summary"]);
      if (m.runId) this.queue(["run", m.runId]);
      if (m.jobKey) this.queue(["job", m.jobKey]);
      if (!m.jobKey) this.queue(["job"], ["run"]); // bulk notification: anything may have changed
      this.queue(["jobs"]); // last run / running count are part of the job list
    });
    c.on("JobChanged", (m: { jobKey?: string | null } = {}) => {
      this.queue(["jobs"], ["summary"]);
      this.queue(m.jobKey ? ["job", m.jobKey] : ["job"]);
    });
    c.on("WorkersChanged", () => this.queue(["workers"], ["jobs"], ["job"], ["summary"]));
    c.on("SummaryChanged", () => this.queue(["summary"]));

    c.onreconnecting(() => this.setConnected(false));
    c.onreconnected(() => { this.setConnected(true); this.o.invalidate(ALL); }); // catch up on what was missed
    c.onclose(() => {
      // Automatic reconnect gave up, the server closed us (token expired, not the dispatcher instance), or stop().
      this.setConnected(false);
      if (!this.stopped) this.scheduleConnect();
    });

    void this.connect();
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    if (this.flushTimer) clearTimeout(this.flushTimer);
    this.retryTimer = this.flushTimer = null;
    this.pending.clear();
    const c = this.conn;
    this.conn = null;
    this.setConnected(false);
    try { await c?.stop(); } catch { /* already closed */ }
  }

  private async connect(): Promise<void> {
    const c = this.conn;
    if (!c || this.stopped) return;
    try {
      await c.start();
      if (this.stopped) { await c.stop(); return; }
      this.attempts = 0;
      this.setConnected(true);
      this.o.invalidate(ALL); // refetch everything once: events before this moment were not delivered
    } catch {
      if (!this.stopped) this.scheduleConnect();
    }
  }

  private scheduleConnect(): void {
    if (this.retryTimer || this.stopped) return;
    const delays = this.o.retryDelaysMs ?? DEFAULT_RETRY_DELAYS_MS;
    const delay = delays[Math.min(this.attempts++, delays.length - 1)];
    this.retryTimer = setTimeout(() => { this.retryTimer = null; void this.connect(); }, delay);
  }

  private setConnected(v: boolean): void {
    if (this.connected === v) return;
    this.connected = v;
    this.o.onConnectionChange(v);
  }

  private queue(...keys: QueryKey[]): void {
    for (const k of keys) this.pending.set(JSON.stringify(k), k);
    if (this.flushTimer) return;
    this.flushTimer = setTimeout(() => {
      this.flushTimer = null;
      const batch = [...this.pending.values()];
      this.pending.clear();
      if (!this.stopped && batch.length) this.o.invalidate(batch);
    }, this.o.debounceMs ?? 200);
  }
}
