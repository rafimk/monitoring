import { formatDuration, formatInterval, formatTime, relative } from "./format";

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

