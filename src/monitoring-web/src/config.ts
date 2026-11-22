/** Fallback refresh cadence, used ONLY while the live-update connection is down. */
export const POLL_MS = 30_000;

const env = import.meta.env;
const str = (v: unknown, fallback = ""): string => (typeof v === "string" && v.trim() !== "" ? v.trim() : fallback);

export interface AppConfig {
  apiBaseUrl: string;
  hubUrl: string;
  keycloak: { url: string; realm: string; clientId: string } | null;
  /** Where Keycloak puts the monitoring roles: realm roles, or the roles of one client. */
  roles: { source: "realm" | "client"; clientId: string; viewer: string; operator: string; administrator: string };
  /** The "sign in as role" form: development builds only, never present in a production bundle. */
  devLogin: boolean;
}

/** All deployment settings come from VITE_* variables (see .env.example). None of them is a secret. */
export function readConfig(e: Record<string, unknown> = env): AppConfig {
  const apiBaseUrl = str(e.VITE_API_BASE_URL, "http://localhost:5284").replace(/\/$/, "");
  const url = str(e.VITE_KEYCLOAK_URL).replace(/\/$/, "");
  const realm = str(e.VITE_KEYCLOAK_REALM);
  const clientId = str(e.VITE_KEYCLOAK_CLIENT_ID);
  return {
    apiBaseUrl,
    hubUrl: `${apiBaseUrl}/hubs/monitoring`,
    keycloak: url && realm && clientId ? { url, realm, clientId } : null,
    roles: {
      source: str(e.VITE_KEYCLOAK_ROLE_SOURCE, "realm") === "client" ? "client" : "realm",
      clientId: str(e.VITE_KEYCLOAK_ROLE_CLIENT_ID, clientId),
      viewer: str(e.VITE_ROLE_VIEWER, "monitoring-viewer"),
      operator: str(e.VITE_ROLE_OPERATOR, "monitoring-operator"),
      administrator: str(e.VITE_ROLE_ADMINISTRATOR, "monitoring-administrator"),
    },
    // import.meta.env.DEV is a compile-time constant: false in `vite build`, so this branch and the dev form are removed.
    devLogin: !!e.DEV && str(e.VITE_DEV_LOGIN) === "true",
  };
}

export const config: AppConfig = readConfig();
