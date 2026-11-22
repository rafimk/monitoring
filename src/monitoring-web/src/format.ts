import { createContext, useContext } from "react";

/** "local" uses the browser zone; otherwise an IANA zone such as "UTC". */
export type TzChoice = "local" | "UTC";
export const TzContext = createContext<TzChoice>("local");
export const useTz = () => useContext(TzContext);

export function formatTime(iso: string | null | undefined, tz: TzChoice = "local"): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return new Intl.DateTimeFormat(undefined, {
    year: "numeric", month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
    hour12: false, ...(tz === "UTC" ? { timeZone: "UTC", timeZoneName: "short" } : {}),
  }).format(d);
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "—";
  if (ms < 1000) return `${ms} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  const m = Math.floor(s / 60);
  return `${m}m ${Math.round(s - m * 60)}s`;
}

export function formatInterval(seconds: number | null | undefined): string {
  if (!seconds) return "—";
  if (seconds % 3600 === 0) return `every ${seconds / 3600} h`;
  if (seconds % 60 === 0) return `every ${seconds / 60} min`;
  return `every ${seconds} s`;
}

export function relative(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "—";
  const diff = Math.round((new Date(iso).getTime() - now) / 1000);
  const abs = Math.abs(diff);
  const unit = abs < 60 ? `${abs}s` : abs < 3600 ? `${Math.round(abs / 60)}m` : `${Math.round(abs / 3600)}h`;
  return diff >= 0 ? `in ${unit}` : `${unit} ago`;
}
