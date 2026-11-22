import Keycloak from "keycloak-js";
import type { AppConfig } from "../config";
import { TokenManager } from "./tokenManager";

/** What the auth provider needs from an identity source. Keycloak in production; fakes in tests; a dev form in dev builds. */
export interface AuthBackend {
  /** Restores a session on page load (silent SSO). Rejects when the identity server is unreachable. */
  init(): Promise<{ authenticated: boolean }>;
  login(): Promise<void>;
  logout(): Promise<void>;
  /** A token valid for at least ~30 s, refreshed if needed (one shared refresh for concurrent callers); null if none. */
  getToken(): Promise<string | null>;
  /** Claims of the current access token (presentation only). */
  claims(): Record<string, unknown> | null;
  /** Called when the session can no longer be refreshed. */
  onSessionExpired(cb: () => void): void;
}

/**
 * Authorization Code + PKCE (S256) against a PUBLIC Keycloak client: no client secret exists in the bundle.
 * Tokens live only in keycloak-js's memory; nothing is written to localStorage/sessionStorage by this app.
 */
export function createKeycloakBackend(cfg: NonNullable<AppConfig["keycloak"]>): AuthBackend {
  const kc = new Keycloak({ url: cfg.url, realm: cfg.realm, clientId: cfg.clientId });
  let expired: () => void = () => {};
  const manager = new TokenManager(kc, () => expired());
  kc.onAuthRefreshError = () => expired();

  return {
    async init() {
      const authenticated = await kc.init({
        onLoad: "check-sso",
        pkceMethod: "S256",
        silentCheckSsoRedirectUri: `${window.location.origin}/silent-check-sso.html`,
        checkLoginIframe: false, // not needed: expiry is detected on the next refresh
      });
      return { authenticated };
    },
    login: () => kc.login({ redirectUri: window.location.href }),
    logout: () => kc.logout({ redirectUri: window.location.origin }),
    getToken: () => (kc.authenticated ? manager.getToken() : Promise.resolve(null)),
    claims: () => (kc.tokenParsed as Record<string, unknown> | undefined) ?? null,
    onSessionExpired: (cb) => { expired = cb; },
  };
}
