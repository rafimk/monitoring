import type { ReactNode } from "react";
import { formatTime, useTz } from "./format";
import type { RunStatus } from "./types";

const STATUS_CLASS: Record<RunStatus, string> = {
  Queued: "neutral", Running: "info", Succeeded: "ok", Failed: "bad", Cancelled: "warn", TimedOut: "bad", Abandoned: "warn",
};

/** Status is conveyed by text + a symbol, never colour alone. */
const STATUS_ICON: Record<RunStatus, string> = {
  Queued: "◷", Running: "▶", Succeeded: "✓", Failed: "✕", Cancelled: "⊘", TimedOut: "⏱", Abandoned: "⚠",
};

export function StatusBadge({ status }: { status: RunStatus }) {
  return (
    <span className={`badge ${STATUS_CLASS[status]}`}>
      <span aria-hidden="true">{STATUS_ICON[status]}</span> {status}
    </span>
  );
}

export function Time({ iso }: { iso: string | null | undefined }) {
  const tz = useTz();
  return <time dateTime={iso ?? undefined}>{formatTime(iso, tz)}</time>;
}

export function Loading({ what = "data" }: { what?: string }) {
  return <p role="status" className="muted">Loading {what}…</p>;
}

export function ErrorBox({ error, retry }: { error: unknown; retry?: () => void }) {
  const msg = error instanceof Error ? error.message : "Something went wrong.";
  return (
    <div role="alert" className="error-box">
      <strong>Could not load: </strong>{msg}
      {retry && <button className="btn small" onClick={retry}>Retry</button>}
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>;
}

export function Card({ label, value, hint, tone }: { label: string; value: ReactNode; hint?: string; tone?: "ok" | "bad" | "warn" }) {
  return (
    <div className={`card ${tone ?? ""}`}>
      <div className="card-label">{label}</div>
      <div className="card-value">{value}</div>
      {hint && <div className="card-hint">{hint}</div>}
    </div>
  );
}
