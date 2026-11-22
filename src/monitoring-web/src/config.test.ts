import { readConfig } from "./config";

describe("readConfig", () => {
  it("reads Keycloak and API settings from the environment", () => {
    const c = readConfig({ VITE_API_BASE_URL: "https://api.example.com/", VITE_KEYCLOAK_URL: "https://kc.example.com/", VITE_KEYCLOAK_REALM: "monitoring", VITE_KEYCLOAK_CLIENT_ID: "monitoring-web" });
    expect(c.apiBaseUrl).toBe("https://api.example.com");
    expect(c.hubUrl).toBe("https://api.example.com/hubs/monitoring");
    expect(c.keycloak).toEqual({ url: "https://kc.example.com", realm: "monitoring", clientId: "monitoring-web" });
  });

  it("treats incomplete Keycloak settings as not configured", () => {
    expect(readConfig({ VITE_KEYCLOAK_URL: "https://kc", VITE_KEYCLOAK_REALM: "m" }).keycloak).toBeNull();
  });

  it("never enables the dev login outside a dev build, even when asked to", () => {
    // Vitest runs with import.meta.env.DEV === true, so simulate the production constant explicitly.
    const prod = readConfig({ VITE_DEV_LOGIN: "true", DEV: false });
    expect(prod.devLogin).toBe(false);
  });

  it("contains no secret-bearing setting names", () => {
    expect(JSON.stringify(readConfig({}))).not.toMatch(/secret/i);
  });
});
