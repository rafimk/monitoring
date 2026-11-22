// Checks a running Monitoring.Api against the local Keycloak dev realm (docker compose --profile keycloak up -d).
// Local testing only: it uses the realm's "monitoring-dev-cli" password-grant client and the sample users.
//   node scripts/verify-keycloak.mjs [apiUrl] [keycloakRealmUrl]
const api = (process.argv[2] ?? "http://localhost:5284").replace(/\/$/, "");
const realm = (process.argv[3] ?? "http://localhost:8080/realms/monitoring").replace(/\/$/, "");

const token = async (username) => {
  const res = await fetch(`${realm}/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "password", client_id: "monitoring-dev-cli", username, password: "Dev_Passw0rd!" }),
  });
  if (!res.ok) throw new Error(`token for ${username}: HTTP ${res.status}`);
  return (await res.json()).access_token;
};
const claims = (t) => JSON.parse(Buffer.from(t.split(".")[1], "base64url").toString());
const call = async (method, path, t) =>
  (await fetch(`${api}${path}`, { method, headers: t ? { Authorization: `Bearer ${t}` } : {} })).status;

let failures = 0;
const check = (name, actual, expected) => {
  const ok = Array.isArray(expected) ? expected.includes(actual) : actual === expected;
  if (!ok) failures++;
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}: got ${actual}, expected ${expected}`);
};

const tokens = Object.fromEntries(await Promise.all(["viewer", "operator", "admin", "norole"].map(async (u) => [u, await token(u)])));
const c = claims(tokens.viewer);
console.log(`issuer=${c.iss} aud=${JSON.stringify(c.aud)} realm roles=${JSON.stringify(c.realm_access?.roles)}`);

check("anonymous GET /jobs", await call("GET", "/api/v1/jobs"), 401);
check("garbage token", await call("GET", "/api/v1/jobs", "not.a.jwt"), 401);
check("norole GET /jobs (no monitoring role)", await call("GET", "/api/v1/jobs", tokens.norole), 403);
for (const u of ["viewer", "operator", "admin"]) check(`${u} GET /jobs`, await call("GET", "/api/v1/jobs", tokens[u]), 200);
check("viewer POST disable", await call("POST", "/api/v1/jobs/sample.job/disable", tokens.viewer), 403);
check("operator POST enable", await call("POST", "/api/v1/jobs/sample.job/enable", tokens.operator), [204, 404]);
check("operator GET /audit", await call("GET", "/api/v1/audit", tokens.operator), 403);
check("admin GET /audit", await call("GET", "/api/v1/audit", tokens.admin), 200);
check("hub negotiate anonymous", await call("POST", "/hubs/monitoring/negotiate?negotiateVersion=1"), 401);
check("hub negotiate norole", await call("POST", "/hubs/monitoring/negotiate?negotiateVersion=1", tokens.norole), 403);
check("hub negotiate viewer", await call("POST", "/hubs/monitoring/negotiate?negotiateVersion=1", tokens.viewer), [200, 400]);
check("token in query string on a REST path is ignored", await call("GET", `/api/v1/jobs?access_token=${tokens.admin}`), 401);

console.log(failures === 0 ? "\nAll checks passed." : `\n${failures} check(s) FAILED.`);
process.exit(failures === 0 ? 0 : 1);
