import { useQuery } from "@tanstack/react-query";
import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useAuth } from "../auth";
import { fakeBackend, realmClaims, renderWithAuth } from "../test-utils";
import { RealtimeProvider, useFallbackPoll, useLiveConnection } from "./RealtimeProvider";
import type { HubLike } from "./realtimeClient";

class FakeHub implements HubLike {
  static instances: FakeHub[] = [];
  handlers = new Map<string, (...a: any[]) => void>();
  reconnecting: () => void = () => {};
  reconnected: () => void = () => {};
  closed: () => void = () => {};
  started = 0;
  stopped = 0;
  constructor() { FakeHub.instances.push(this); }
  async start() { this.started++; }
  async stop() { this.stopped++; }
  on(m: string, h: (...a: any[]) => void) { this.handlers.set(m, h); }
  onreconnecting(cb: () => void) { this.reconnecting = cb; }
  onreconnected(cb: () => void) { this.reconnected = cb; }
  onclose(cb: () => void) { this.closed = cb; }
}

let polledCalls = 0;
function Page() {
  const poll = useFallbackPoll();
  const live = useLiveConnection();
  useQuery({ queryKey: ["jobs"], queryFn: async () => { polledCalls++; return []; }, refetchInterval: poll });
  const { status, logout } = useAuth();
  return (
    <div>
      <span data-testid="live">{live ? "live" : "polling"}</span>
      <span data-testid="poll">{String(poll)}</span>
      <span data-testid="status">{status}</span>
      <button onClick={logout}>Sign out</button>
    </div>
  );
}

const tree = (b: ReturnType<typeof fakeBackend>, tokens: string[] = []) =>
  renderWithAuth(
    <RealtimeProvider createConnection={(getToken) => { void getToken().then((t) => tokens.push(t ?? "")); return new FakeHub(); }}>
      <Page />
    </RealtimeProvider>,
    b,
  );

beforeEach(() => { FakeHub.instances = []; polledCalls = 0; });

describe("RealtimeProvider", () => {
  it("opens exactly one connection per authenticated session and none before sign-in", async () => {
    const out = tree(fakeBackend({ authenticated: false }));
    await screen.findByTestId("status");
    expect(FakeHub.instances).toHaveLength(0);
    out.unmount();

    tree(fakeBackend({ claims: realmClaims(["monitoring-viewer"]) }));
    await waitFor(() => expect(FakeHub.instances).toHaveLength(1));
    await waitFor(() => expect(screen.getByTestId("live")).toHaveTextContent("live"));
    expect(FakeHub.instances[0].started).toBe(1);
  });

  it("asks for a fresh token when the connection is created", async () => {
    const tokens: string[] = [];
    tree(fakeBackend({ token: "kc-token-7" }), tokens);
    await waitFor(() => expect(tokens).toEqual(["kc-token-7"]));
  });

  it("stops the connection on sign-out and goes back to polling", async () => {
    tree(fakeBackend());
    await waitFor(() => expect(screen.getByTestId("live")).toHaveTextContent("live"));
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(FakeHub.instances[0].stopped).toBe(1));
    expect(screen.getByTestId("live")).toHaveTextContent("polling");
  });

  it("stops the connection when the session expires", async () => {
    const b = fakeBackend();
    tree(b);
    await waitFor(() => expect(screen.getByTestId("live")).toHaveTextContent("live"));
    act(() => b.expire());
    await waitFor(() => expect(FakeHub.instances[0].stopped).toBe(1));
  });

  it("polls (slowly) only while disconnected, and not at all while live", async () => {
    tree(fakeBackend());
    await waitFor(() => expect(screen.getByTestId("live")).toHaveTextContent("live"));
    expect(screen.getByTestId("poll")).toHaveTextContent("false");

    act(() => FakeHub.instances[0].reconnecting());
    await waitFor(() => expect(screen.getByTestId("live")).toHaveTextContent("polling"));
    expect(screen.getByTestId("poll")).toHaveTextContent("30000"); // low-frequency fallback

    act(() => FakeHub.instances[0].reconnected());
    await waitFor(() => expect(screen.getByTestId("poll")).toHaveTextContent("false"));
    expect(FakeHub.instances).toHaveLength(1); // reconnecting never creates a second connection
  });

  it("a pushed notification refetches the affected query from the REST API", async () => {
    tree(fakeBackend());
    await waitFor(() => expect(screen.getByTestId("live")).toHaveTextContent("live"));
    await waitFor(() => expect(polledCalls).toBeGreaterThanOrEqual(1));
    const before = polledCalls;

    act(() => FakeHub.instances[0].handlers.get("JobChanged")!({ jobKey: "j1", reason: "enabled" }));
    await waitFor(() => expect(polledCalls).toBeGreaterThan(before), { timeout: 3000 });
  });

  it("reconnecting refetches everything to recover events missed while offline", async () => {
    tree(fakeBackend());
    await waitFor(() => expect(screen.getByTestId("live")).toHaveTextContent("live"));
    await waitFor(() => expect(polledCalls).toBeGreaterThanOrEqual(1));
    const before = polledCalls;
    act(() => FakeHub.instances[0].reconnecting());
    act(() => FakeHub.instances[0].reconnected());
    await waitFor(() => expect(polledCalls).toBeGreaterThan(before));
  });
});
