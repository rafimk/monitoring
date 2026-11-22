import { useQueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { configureApi } from "./api";
import { createKeycloakBackend, type AuthBackend } from "./auth/backend";
import { sessionFromClaims, type Session } from "./auth/session";
import { config } from "./config";

export { hasRole } from "./auth/session";
export type { Session } from "./auth/session";

export type AuthStatus =
  | "loading"          // restoring a session (silent SSO)
  | "authenticated"
  | "unauthenticated"  // nobody signed in
  | "expired"          // had a session; it can no longer be refreshed
  | "forbidden"        // signed in, but the account has no monitoring role
  | "error";           // sign-in service unreachable or misconfigured

interface AuthCtx {
  status: AuthStatus;
  session: Session | null;
  error: string | null;
  login: () => void;
  logout: () => void;
  retry: () => void;
  /** Development builds only (undefined in production). */
  devLogin?: (username: string, role: string) => Promise<void>;
  getToken: () => Promise<string | null>;
}
const Ctx = createContext<AuthCtx | null>(null);

function defaultBackend(): AuthBackend | null {
  return config.keycloak ? createKeycloakBackend(config.keycloak) : null;
}

export function AuthProvider({ children, backend: injected }: { children: ReactNode; backend?: AuthBackend | null }) {
  const qc = useQueryClient();
  const backendRef = useRef<AuthBackend | null | undefined>(undefined);
  if (backendRef.current === undefined) backendRef.current = injected !== undefined ? injected : defaultBackend();
  const initRef = useRef<Promise<{ authenticated: boolean }> | null>(null);

  const [status, setStatus] = useState<AuthStatus>("loading");
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState<string | null>(null);

  const adopt = useCallback((b: AuthBackend) => {
    const s = sessionFromClaims(b.claims() ?? {}, config.roles);
    if (!s) { setSession(null); setStatus("forbidden"); return; }
    setSession(s); setError(null); setStatus("authenticated");
  }, []);

  const expire = useCallback(() => {
    qc.clear();
    setSession(null);
    setStatus((s) => (s === "authenticated" ? "expired" : s));
  }, [qc]);

  const start = useCallback(() => {
    const b = backendRef.current;
    if (!b) {
      setStatus(import.meta.env.DEV && config.devLogin ? "unauthenticated" : "error");
      setError(import.meta.env.DEV && config.devLogin ? null
        : "Sign-in is not configured. Set VITE_KEYCLOAK_URL, VITE_KEYCLOAK_REALM and VITE_KEYCLOAK_CLIENT_ID.");
      return;
    }
    setStatus("loading");
    b.onSessionExpired(expire);
    // keycloak-js may be initialised only once per instance; React StrictMode runs effects twice in development.
    (initRef.current ??= b.init()).then(
      (r) => { if (r.authenticated) adopt(b); else setStatus("unauthenticated"); },
      () => {
        initRef.current = null;
        setError("The sign-in service (Keycloak) cannot be reached. Check your connection or try again in a moment.");
        setStatus("error");
      },
    );
  }, [adopt, expire]);

  useEffect(start, [start]);

  const getToken = useCallback(async () => (await backendRef.current?.getToken()) ?? null, []);
  // Configure synchronously so the very first queries already use the token provider.
  configureApi({ getToken, onUnauthorized: expire });

  const login = useCallback(() => {
    backendRef.current?.login().catch(() => {
      setError("The sign-in service (Keycloak) cannot be reached. Check your connection or try again in a moment.");
      setStatus("error");
    });
  }, []);

  const logout = useCallback(() => {
    const b = backendRef.current;
    qc.clear();
    setSession(null);
    setStatus("unauthenticated");
    void b?.logout();
  }, [qc]);

  const retry = useCallback(() => {
    initRef.current = null;
    // A failed keycloak-js init cannot be repeated on the same instance: rebuild the page state cleanly.
    if (injected === undefined && config.keycloak) window.location.reload(); else start();
  }, [injected, start]);

  const devLogin = useMemo(() => {
    if (!(import.meta.env.DEV && config.devLogin)) return undefined;
    return async (username: string, role: string) => {
      const { createDevBackend } = await import("./auth/devBackend");
      const dev = createDevBackend();
      await dev.signIn(username, role);
      backendRef.current = dev;
      adopt(dev);
    };
  }, [adopt]);

  const value = useMemo<AuthCtx>(
    () => ({ status, session, error, login, logout, retry, devLogin, getToken }),
    [status, session, error, login, logout, retry, devLogin, getToken],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useAuth(): AuthCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error("useAuth must be used inside AuthProvider");
  return c;
}
