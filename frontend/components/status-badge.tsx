import type { RunStatus } from "@/lib/api-types";

const TONE: Record<RunStatus, string> = {
  Queued: "neutral",
  Running: "active",
  AwaitingApproval: "warn",
  Publishing: "active",
  Done: "ok",
  Failed: "danger",
  Rejected: "danger",
  Scheduled: "warn",
  Cancelled: "neutral",
};

const LABEL: Record<RunStatus, string> = {
  Queued: "Queued",
  Running: "Running",
  AwaitingApproval: "Awaiting approval",
  Publishing: "Publishing",
  Done: "Published",
  Failed: "Failed",
  Rejected: "Rejected",
  Scheduled: "Scheduled",
  Cancelled: "Cancelled",
};

/** Renders a run's lifecycle status as a coloured pill. Presentation only. */
export function StatusBadge({ status }: { status: RunStatus }) {
  return <span className={`badge badge--${TONE[status]}`}>{LABEL[status]}</span>;
}
