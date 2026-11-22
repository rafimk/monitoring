import type { AppConfig } from "../config";
import type { Role } from "../types";

export interface Session { name: string; role: Role }

const rank: Record<Role, number> = { viewer: 0, operator: 1, administrator: 2 };
export const hasRole = (s: Session | null, min: Role) => !!s && rank[s.role] >= rank[min];

type Claims = Record<string, unknown>;

const rolesOf = (claims: Claims, cfg: AppConfig["roles"]): string[] => {
  const holder = cfg.source === "client"
    ? (claims.resource_access as Record<string, { roles?: unknown }> | undefined)?.[cfg.clientId]
    : (claims.realm_access as { roles?: unknown } | undefined);
  return Array.isArray(holder?.roles) ? holder.roles.filter((r): r is string => typeof r === "string") : [];
};

/**
 * Display name and the highest monitoring role from the access-token claims, for PRESENTATION only (which buttons to
 * show). The API validates the token and re-checks every permission, so tampering here gains nothing.
 * Returns null role when the user holds no monitoring role.
 */
export function sessionFromClaims(claims: Claims, cfg: AppConfig["roles"]): Session | null {
  const held = new Set(rolesOf(claims, cfg));
  const role: Role | null = held.has(cfg.administrator) ? "administrator" : held.has(cfg.operator) ? "operator" : held.has(cfg.viewer) ? "viewer" : null;
  if (!role) return null;
  const name = [claims.name, claims.preferred_username, claims.email, claims.sub].find((v): v is string => typeof v === "string" && v !== "");
  return { name: name ?? "user", role };
}
