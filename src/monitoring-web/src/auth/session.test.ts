import { readConfig } from "../config";
import { hasRole, sessionFromClaims } from "./session";

const roles = readConfig({}).roles;

describe("sessionFromClaims (presentation only)", () => {
  it("picks the highest monitoring role and a display name", () => {
    const s = sessionFromClaims({ name: "Pat Doe", preferred_username: "pat", realm_access: { roles: ["monitoring-viewer", "monitoring-operator", "offline_access"] } }, roles);
    expect(s).toEqual({ name: "Pat Doe", role: "operator" });
    expect(hasRole(s, "viewer")).toBe(true);
    expect(hasRole(s, "operator")).toBe(true);
    expect(hasRole(s, "administrator")).toBe(false);
  });

  it("falls back from name to username, email, then subject", () => {
    const r = { realm_access: { roles: ["monitoring-viewer"] } };
    expect(sessionFromClaims({ ...r, preferred_username: "pat" }, roles)?.name).toBe("pat");
    expect(sessionFromClaims({ ...r, email: "p@example.com" }, roles)?.name).toBe("p@example.com");
    expect(sessionFromClaims({ ...r, sub: "abc" }, roles)?.name).toBe("abc");
  });

  it("returns null when the user holds no monitoring role", () => {
    expect(sessionFromClaims({ realm_access: { roles: ["offline_access", "viewer", "admin"] } }, roles)).toBeNull();
    expect(sessionFromClaims({}, roles)).toBeNull();
    expect(sessionFromClaims({ realm_access: { roles: "monitoring-administrator" } }, roles)).toBeNull();
  });

  it("reads client roles only when configured to", () => {
    const claims = { resource_access: { "monitoring-web": { roles: ["monitoring-administrator"] } }, realm_access: { roles: ["monitoring-viewer"] } };
    const client = readConfig({ VITE_KEYCLOAK_ROLE_SOURCE: "client", VITE_KEYCLOAK_ROLE_CLIENT_ID: "monitoring-web" }).roles;
    expect(sessionFromClaims(claims, client)?.role).toBe("administrator");
    expect(sessionFromClaims(claims, roles)?.role).toBe("viewer");
    expect(sessionFromClaims({ resource_access: { other: { roles: ["monitoring-administrator"] } } }, client)).toBeNull();
  });

  it("honours custom role names", () => {
    const custom = readConfig({ VITE_ROLE_OPERATOR: "ops" }).roles;
    expect(sessionFromClaims({ realm_access: { roles: ["ops"] } }, custom)?.role).toBe("operator");
  });
});
