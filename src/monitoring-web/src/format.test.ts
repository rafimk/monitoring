import { formatDuration, formatInterval, formatTime, relative } from "./format";
import { parseToken, hasRole } from "./auth";

describe("formatDuration", () => {
  it.each([[null, "—"], [250, "250 ms"], [2000, "2.0 s"], [90_000, "1m 30s"]])("%s -> %s", (ms, out) => {
    expect(formatDuration(ms as number | null)).toBe(out);
  });
});

describe("formatInterval", () => {
  it.each([[60, "every 1 min"], [120, "every 2 min"], [45, "every 45 s"], [3600, "every 1 h"], [null, "—"]])("%s -> %s", (s, out) => {
    expect(formatInterval(s as number | null)).toBe(out);
  });
});

describe("formatTime", () => {
  it("renders UTC explicitly when asked", () => {
    expect(formatTime("2026-01-01T12:00:00Z", "UTC")).toContain("12:00:00");
  });
  it("handles missing and invalid values", () => {
    expect(formatTime(null)).toBe("—");
    expect(formatTime("not a date")).toBe("—");
  });
});

describe("relative", () => {
  const now = Date.parse("2026-01-01T12:00:00Z");
  it("describes past and future", () => {
    expect(relative("2026-01-01T11:59:30Z", now)).toBe("30s ago");
    expect(relative("2026-01-01T12:02:00Z", now)).toBe("in 2m");
  });
});

function jwt(payload: object) {
  const b64 = (o: object) => btoa(JSON.stringify(o)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return `${b64({ alg: "none" })}.${b64(payload)}.sig`;
}

describe("parseToken / hasRole", () => {
  it("reads role and name", () => {
    const s = parseToken(jwt({ role: "operator", name: "Pat", exp: Date.now() / 1000 + 600 }));
    expect(s?.role).toBe("operator");
    expect(hasRole(s, "viewer")).toBe(true);
    expect(hasRole(s, "operator")).toBe(true);
    expect(hasRole(s, "administrator")).toBe(false);
  });
  it("rejects expired, unknown-role and malformed tokens", () => {
    expect(parseToken(jwt({ role: "viewer", exp: 1 }))).toBeNull();
    expect(parseToken(jwt({ role: "root" }))).toBeNull();
    expect(parseToken("garbage")).toBeNull();
  });
});
