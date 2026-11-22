import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { api } from "./api";
import { useAuth } from "./auth";
import Login from "./pages/Login";
import { fakeBackend, realmClaims, renderWithAuth } from "./test-utils";

// A configured Keycloak, and no development login form, whatever the local .env.local says.
vi.mock("./config", async (orig) => {
  const m = await orig<typeof import("./config")>();
  return { ...m, config: { ...m.config, devLogin: false, keycloak: { url: "https://kc.example.com", realm: "monitoring", clientId: "monitoring-web" } } };
});

/** Mirrors what App does: Login unless authenticated. */
function Shell() {
  const { status, session, logout } = useAuth();
  if (status !== "authenticated" || !session) return <Login />;
  return (
    <div>
      <span data-testid="who">{session.name}:{session.role}</span>
      <button onClick={logout}>Sign out</button>
    </div>
  );
}

const asRole = (...roles: string[]) => fakeBackend({ claims: realmClaims(roles, { name: "Pat" }) });

afterEach(() => vi.unstubAllGlobals());

describe("sign-in", () => {
  it("offers Keycloak sign-in (and nothing else) when nobody is signed in, and starts the redirect on click", async () => {
    const b = fakeBackend({ authenticated: false });
    renderWithAuth(<Shell />, b);
    await userEvent.click(await screen.findByRole("button", { name: "Sign in with Keycloak" }));
    expect(b.loginCalls).toBe(1);
    expect(screen.queryByRole("textbox")).not.toBeInTheDocument(); // no token paste box, no dev form
  });

  it("restores a signed-in session on page load and derives name and role from the token", async () => {
    renderWithAuth(<Shell />, asRole("monitoring-operator"));
    expect(await screen.findByTestId("who")).toHaveTextContent("Pat:operator");
  });

  it("shows a loading state while the session is being restored", async () => {
    renderWithAuth(<Shell />, fakeBackend({ initDelayMs: 50, claims: realmClaims(["monitoring-viewer"]) }));
    expect(screen.getByRole("status")).toHaveTextContent(/Signing you in/);
    expect(await screen.findByTestId("who")).toBeInTheDocument();
  });

  it("tells a signed-in user without a monitoring role that they have no access", async () => {
    const b = asRole("offline_access", "uma_authorization");
    renderWithAuth(<Shell />, b);
    expect(await screen.findByRole("alert")).toHaveTextContent(/no Job Monitoring role/);
    expect(screen.queryByTestId("who")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    expect(b.logoutCalls).toBe(1);
  });

  it("shows a visible error when Keycloak cannot be reached", async () => {
    renderWithAuth(<Shell />, fakeBackend({ initError: new Error("network") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/cannot be reached/i);
  });
});

describe("sign-out and session expiry", () => {
  it("signs out through the identity provider and clears cached data", async () => {
    const b = asRole("monitoring-viewer");
    const { qc } = renderWithAuth(<Shell />, b);
    qc.setQueryData(["jobs"], [{ key: "secret-ish" }]);
    await userEvent.click(await screen.findByRole("button", { name: "Sign out" }));
    expect(b.logoutCalls).toBe(1);
    expect(qc.getQueryData(["jobs"])).toBeUndefined(); // the next user must not see the previous user's data
    expect(screen.queryByTestId("who")).not.toBeInTheDocument();
  });

  it("shows an expiry notice when the session can no longer be refreshed", async () => {
    const b = asRole("monitoring-viewer");
    renderWithAuth(<Shell />, b);
    await screen.findByTestId("who");
    b.expire();
    expect(await screen.findByRole("alert")).toHaveTextContent(/session has expired/i);
    expect(screen.queryByTestId("who")).not.toBeInTheDocument();
  });

  it("treats a 401 from the API as an expired session", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 401 })));
    const b = asRole("monitoring-viewer");
    function Probe() { useQuery({ queryKey: ["jobs"], queryFn: api.jobs }); return null; }
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { AuthProvider } = await import("./auth");
    render(<QueryClientProvider client={qc}><AuthProvider backend={b}><Shell /><Probe /></AuthProvider></QueryClientProvider>);
    expect(await screen.findByRole("alert")).toHaveTextContent(/session has expired/i);
  });
});

describe("API requests carry a fresh token", () => {
  it("asks the token provider for each request and sends it as a bearer token", async () => {
    const fetchMock = vi.fn(async () => Response.json([]));
    vi.stubGlobal("fetch", fetchMock);
    const b = asRole("monitoring-viewer");
    renderWithAuth(<Shell />, b);
    await screen.findByTestId("who");

    b.setToken("token-A");
    await api.jobs();
    b.setToken("token-B"); // e.g. refreshed by keycloak-js between two requests
    await api.workers();

    const auth = (i: number) => ((fetchMock.mock.calls[i] as unknown[])[1] as RequestInit).headers as Record<string, string>;
    expect(auth(0).Authorization).toBe("Bearer token-A");
    expect(auth(1).Authorization).toBe("Bearer token-B");
  });

  it("never persists tokens in browser storage", async () => {
    localStorage.clear(); sessionStorage.clear();
    vi.stubGlobal("fetch", vi.fn(async () => Response.json([])));
    renderWithAuth(<Shell />, asRole("monitoring-viewer"));
    await screen.findByTestId("who");
    await api.jobs();
    const dump = JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage });
    expect(dump).not.toMatch(/token/i);
    expect(localStorage.length + sessionStorage.length).toBe(0);
  });
});
