import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider, useAuth } from "./auth";
import type { AuthBackend } from "./auth/backend";

export const realmClaims = (roles: string[], extra: Record<string, unknown> = {}) =>
  ({ preferred_username: "tester", realm_access: { roles }, ...extra });

export interface FakeBackend extends AuthBackend {
  loginCalls: number;
  logoutCalls: number;
  tokenCalls: number;
  expire(): void;
  setToken(t: string | null): void;
}

/** A controllable identity source: nothing here talks to Keycloak. */
export function fakeBackend(opts: {
  authenticated?: boolean;
  claims?: Record<string, unknown>;
  initError?: Error;
  token?: string | null;
  initDelayMs?: number;
} = {}): FakeBackend {
  let expiredCb: () => void = () => {};
  let token = opts.token === undefined ? "token-1" : opts.token;
  const b: FakeBackend = {
    loginCalls: 0, logoutCalls: 0, tokenCalls: 0,
    async init() {
      if (opts.initDelayMs) await new Promise((r) => setTimeout(r, opts.initDelayMs));
      if (opts.initError) throw opts.initError;
      return { authenticated: opts.authenticated ?? true };
    },
    async login() { b.loginCalls++; },
    async logout() { b.logoutCalls++; },
    async getToken() { b.tokenCalls++; return token; },
    claims: () => opts.claims ?? realmClaims(["monitoring-viewer"]),
    onSessionExpired: (cb) => { expiredCb = cb; },
    expire: () => expiredCb(),
    setToken: (t) => { token = t; },
  };
  return b;
}

/** Renders children only once the (fake) sign-in finished, like App does. */
export function RequireAuth({ children }: { children: ReactNode }) {
  return useAuth().status === "authenticated" ? <>{children}</> : null;
}

export function renderWithAuth(ui: ReactNode, backend: AuthBackend, opts: { route?: string } = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const utils = render(
    <QueryClientProvider client={qc}>
      <AuthProvider backend={backend}>
        <MemoryRouter initialEntries={[opts.route ?? "/"]}>{ui}</MemoryRouter>
      </AuthProvider>
    </QueryClientProvider>,
  );
  return { ...utils, qc };
}
