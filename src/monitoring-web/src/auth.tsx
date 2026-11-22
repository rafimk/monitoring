import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { configureApi } from "./api";
import type { Role } from "./types";

const STORAGE_KEY = "monitoring.token";

export interface Session { token: string; name: string; role: Role }

/** Decodes the JWT payload for UI display/gating only. The API re-checks every permission server-side. */
export function parseToken(token: string): Session | null {
  try {
    const payload = JSON.parse(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
    const role = payload.role as Role;
    if (!["viewer", "operator", "administrator"].includes(role)) return null;
    if (payload.exp && payload.exp * 1000 < Date.now()) return null;
    return { token, name: payload.name ?? "user", role };
  } catch {
    return null;
  }
}

const rank: Record<Role, number> = { viewer: 0, operator: 1, administrator: 2 };
export const hasRole = (s: Session | null, min: Role) => !!s && rank[s.role] >= rank[min];

interface AuthCtx { session: Session | null; login: (token: string) => boolean; logout: () => void }
const Ctx = createContext<AuthCtx | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => {
    const t = sessionStorage.getItem(STORAGE_KEY);
    return t ? parseToken(t) : null;
  });

  const logout = useCallback(() => { sessionStorage.removeItem(STORAGE_KEY); setSession(null); }, []);
  const login = useCallback((token: string) => {
    const s = parseToken(token.trim());
    if (!s) return false;
    sessionStorage.setItem(STORAGE_KEY, s.token);
    setSession(s);
    return true;
  }, []);

  useEffect(() => {
    configureApi({ getToken: () => session?.token ?? null, onUnauthorized: logout });
  }, [session, logout]);
  // Configure synchronously for the first render's queries too.
  configureApi({ getToken: () => session?.token ?? null, onUnauthorized: logout });

  const value = useMemo(() => ({ session, login, logout }), [session, login, logout]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useAuth(): AuthCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error("useAuth must be used inside AuthProvider");
  return c;
}
