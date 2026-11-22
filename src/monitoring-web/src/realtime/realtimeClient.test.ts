import { RealtimeClient, type HubLike, type QueryKey } from "./realtimeClient";

class FakeHub implements HubLike {
  handlers = new Map<string, (...a: any[]) => void>();
  reconnecting: () => void = () => {};
  reconnected: () => void = () => {};
  closed: () => void = () => {};
  startCalls = 0;
  stopCalls = 0;
  failStarts = 0; // the next N start() calls reject
  async start() { this.startCalls++; if (this.failStarts > 0) { this.failStarts--; throw new Error("offline"); } }
  async stop() { this.stopCalls++; }
  on(m: string, h: (...a: any[]) => void) { this.handlers.set(m, h); }
  onreconnecting(cb: () => void) { this.reconnecting = cb; }
  onreconnected(cb: () => void) { this.reconnected = cb; }
  onclose(cb: () => void) { this.closed = cb; }
  emit(m: string, ...a: any[]) { this.handlers.get(m)!(...a); }
}

function setup(retryDelaysMs = [1000, 2000, 5000], failStarts = 0) {
  const hubs: FakeHub[] = [];
  const invalidated: QueryKey[][] = [];
  const states: boolean[] = [];
  const client = new RealtimeClient({
    createConnection: () => { const h = new FakeHub(); h.failStarts = failStarts; hubs.push(h); return h; },
    invalidate: (k) => invalidated.push(k),
    onConnectionChange: (c) => states.push(c),
    retryDelaysMs, debounceMs: 100,
  });
  const keys = (batch: QueryKey[]) => batch.map((k) => JSON.stringify(k));
  return { client, hubs, invalidated, states, keys };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe("RealtimeClient", () => {
  it("creates one connection, reports connected, and refetches everything once on connect", async () => {
    const { client, hubs, invalidated, states, keys } = setup();
    client.start();
    client.start(); // idempotent: no second connection
    await vi.advanceTimersByTimeAsync(0);
    expect(hubs).toHaveLength(1);
    expect(hubs[0].startCalls).toBe(1);
    expect(states).toEqual([true]);
    expect(invalidated).toHaveLength(1);
    expect(keys(invalidated[0])).toEqual(['["summary"]', '["workers"]', '["jobs"]', '["job"]', '["runs"]', '["run"]']);
  });

  it("turns notifications into targeted, coalesced query invalidations", async () => {
    const { client, hubs, invalidated, keys } = setup();
    client.start(); await vi.advanceTimersByTimeAsync(0);
    invalidated.length = 0;

    hubs[0].emit("RunChanged", { runId: "r1", jobKey: "j1", reason: "started" });
    hubs[0].emit("RunChanged", { runId: "r1", jobKey: "j1", reason: "succeeded" }); // burst: same run twice
    hubs[0].emit("RunChanged", { runId: "r2", jobKey: "j1", reason: "queued" });
    expect(invalidated).toHaveLength(0); // debounced
    await vi.advanceTimersByTimeAsync(150);

    expect(invalidated).toHaveLength(1); // one batch for the whole burst
    const k = keys(invalidated[0]);
    expect(k).toEqual(expect.arrayContaining(['["runs"]', '["summary"]', '["run","r1"]', '["run","r2"]', '["job","j1"]', '["jobs"]']));
    expect(new Set(k).size).toBe(k.length); // no duplicates
  });

  it("maps job, worker and summary notifications to their queries", async () => {
    const { client, hubs, invalidated, keys } = setup();
    client.start(); await vi.advanceTimersByTimeAsync(0);

    invalidated.length = 0;
    hubs[0].emit("JobChanged", { jobKey: "j9", reason: "disabled" });
    await vi.advanceTimersByTimeAsync(150);
    expect(keys(invalidated[0])).toEqual(expect.arrayContaining(['["jobs"]', '["job","j9"]', '["summary"]']));

    invalidated.length = 0;
    hubs[0].emit("WorkersChanged", { reason: "registered" });
    await vi.advanceTimersByTimeAsync(150);
    expect(keys(invalidated[0])).toEqual(expect.arrayContaining(['["workers"]', '["jobs"]', '["summary"]']));

    invalidated.length = 0;
    hubs[0].emit("SummaryChanged");
    await vi.advanceTimersByTimeAsync(150);
    expect(keys(invalidated[0])).toEqual(['["summary"]']);

    invalidated.length = 0;
    hubs[0].emit("RunChanged", { runId: null, jobKey: null, reason: "bulk" }); // flood summary from the server
    await vi.advanceTimersByTimeAsync(150);
    expect(keys(invalidated[0])).toEqual(expect.arrayContaining(['["runs"]', '["job"]', '["run"]']));
  });

  it("retries the first connection with backoff until it succeeds, without creating more connections", async () => {
    const { client, hubs, states, invalidated } = setup([1000, 2000, 5000], 3);
    client.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(hubs[0].startCalls).toBe(1);
    await vi.advanceTimersByTimeAsync(999); expect(hubs[0].startCalls).toBe(1);
    await vi.advanceTimersByTimeAsync(1); expect(hubs[0].startCalls).toBe(2); // after 1 s
    await vi.advanceTimersByTimeAsync(2000); expect(hubs[0].startCalls).toBe(3); // then 2 s
    await vi.advanceTimersByTimeAsync(5000); expect(hubs[0].startCalls).toBe(4); // then 5 s: succeeds
    expect(states).toEqual([true]);
    expect(invalidated).toHaveLength(1);
    expect(hubs).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hubs[0].startCalls).toBe(4); // no timer left running
  });

  it("goes offline while reconnecting, then refetches everything to recover missed events", async () => {
    const { client, hubs, states, invalidated } = setup();
    client.start(); await vi.advanceTimersByTimeAsync(0);
    invalidated.length = 0;

    hubs[0].reconnecting();
    expect(states).toEqual([true, false]);
    hubs[0].reconnected();
    expect(states).toEqual([true, false, true]);
    expect(invalidated).toHaveLength(1);
    expect(invalidated[0].length).toBe(6);
  });

  it("restarts the connection with backoff when the server closes it (token expiry, standby instance)", async () => {
    const { client, hubs, states } = setup([1000]);
    client.start(); await vi.advanceTimersByTimeAsync(0);
    hubs[0].closed();
    expect(states).toEqual([true, false]);
    await vi.advanceTimersByTimeAsync(1000);
    expect(hubs[0].startCalls).toBe(2);
    expect(states).toEqual([true, false, true]);
    expect(hubs).toHaveLength(1);
  });

  it("stop() closes the connection, cancels every timer and ignores late events", async () => {
    const { client, hubs, invalidated, states } = setup([1000, 2000, 5000], 5);
    client.start();
    await vi.advanceTimersByTimeAsync(0);
    hubs[0].emit("SummaryChanged"); // queued, not flushed yet
    await client.stop();
    expect(hubs[0].stopCalls).toBe(1);
    await vi.advanceTimersByTimeAsync(120_000);
    expect(hubs[0].startCalls).toBe(1); // the pending retry was cancelled
    expect(invalidated).toHaveLength(0); // the queued notification was dropped
    expect(states.at(-1) ?? false).toBe(false);

    hubs[0].closed(); // a late close callback must not resurrect the connection
    await vi.advanceTimersByTimeAsync(120_000);
    expect(hubs[0].startCalls).toBe(1);
  });
});
