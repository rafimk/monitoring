// DEVELOPMENT ONLY. This module is only reachable through `if (import.meta.env.DEV)`, which is a compile-time false in
// production builds, so none of it (including the dev-token call) ships in `npm run build`.
import { config } from "../config";
import type { AuthBackend } from "./backend";

export interface DevBackend extends AuthBackend {
  signIn(username: string, role: string): Promise<void>;
}

/** Signs in through the API's Development-only token endpoint. The token is kept in memory only. */
export function createDevBackend(): DevBackend {
  let token: string | null = null;
  let claims: Record<string, unknown> | null = null;
  return {
    async init() { return { authenticated: false }; },
    async login() {},
    async logout() { token = null; claims = null; },
    getToken: async () => token,
    claims: () => claims,
    onSessionExpired() {},
    async signIn(username, role) {
      const res = await fetch(`${config.apiBaseUrl}/api/v1/auth/dev-token`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username, role }),
      });
      if (!res.ok) throw new Error(`Dev sign-in failed (${res.status}). Is Api:EnableDevTokens on?`);
      token = (await res.json()).token as string;
      const payload = JSON.parse(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
      // Shape it like Keycloak's token so the same role/name derivation applies.
      claims = { name: payload.name, realm_access: { roles: [config.roles[payload.role as "viewer" | "operator" | "administrator"]] } };
    },
  };
}
