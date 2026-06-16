"use client";

import { useMemo, useState } from "react";
import { ApiError, cancelRun, postApproval } from "@/lib/api-client";
import type { GateAction, RegenerateMode, RunReviewDto } from "@/lib/api-types";

// Client-side PlatformConstraints hints mirroring the server (DL-030). The server is the source of
// truth — these only guide the reviewer and disable an obviously-invalid submit.
const MAX_CAPTION = 2200;
const MAX_HASHTAGS = 30;

const MODE_LABEL: Record<RegenerateMode, string> = {
  "same-angle": "Same angle (new creative)",
  "reselect-angle": "Reselect angle (different banked angle)",
};

/**
 * The human gate. Renders ONLY the actions the server marked available (DL-041) — it never recomputes
 * gate policy — and posts each decision through the typed client. On success it asks the page to
 * refresh so the new status/outcome shows.
 */
export function DecisionPanel({
  brandId,
  review,
  onActed,
}: {
  brandId: string;
  review: RunReviewDto;
  onActed: () => void;
}) {
  const actions = review.availableActions;
  const gateActions = actions.filter((a): a is Exclude<GateAction, "Cancel"> => a !== "Cancel");

  const [tab, setTab] = useState<GateAction>(gateActions[0] ?? actions[0]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await action();
      onActed();
    } catch (e) {
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
    } finally {
      setBusy(false);
    }
  }

  if (actions.length === 0) {
    return (
      <div className="card">
        <div className="card__body muted">
          No actions available — this run is {isTerminal(review.status) ? "complete" : "in flight"}.
        </div>
      </div>
    );
  }

  // Scheduled run: the only action is cancel (a separate endpoint).
  if (actions.includes("Cancel")) {
    return (
      <div className="card">
        <div className="card__body stack">
          <h3>Scheduled</h3>
          {review.scheduledFor && (
            <p className="muted">Publishes {new Date(review.scheduledFor).toLocaleString()}.</p>
          )}
          <CancelForm busy={busy} onCancel={(reason) => run(() => cancelRun(brandId, review.runId, { reason }))} />
          {error && <div className="banner banner--danger">{error}</div>}
        </div>
      </div>
    );
  }

  return (
    <div className="card">
      <div className="card__body stack">
        <div className="tabs">
          {gateActions.map((a) => (
            <button
              key={a}
              className={`tab ${tab === a ? "tab--active" : ""}`}
              onClick={() => setTab(a)}
            >
              {a}
            </button>
          ))}
        </div>

        {tab === "Approve" && (
          <ApproveForm
            review={review}
            busy={busy}
            onApprove={(body) => run(() => postApproval(brandId, review.runId, body))}
          />
        )}

        {tab === "Regenerate" && review.regenerate && (
          <RegenerateForm
            modes={review.regenerate.modes as RegenerateMode[]}
            remaining={review.regenerate.remaining}
            busy={busy}
            onRegenerate={(mode, reason) =>
              run(() => postApproval(brandId, review.runId, { decision: "regenerate", mode, reason }))
            }
          />
        )}

        {tab === "Reject" && (
          <RejectForm
            busy={busy}
            onReject={(reason) =>
              run(() => postApproval(brandId, review.runId, { decision: "reject", reason }))
            }
          />
        )}

        {error && <div className="banner banner--danger">{error}</div>}
      </div>
    </div>
  );
}

function ApproveForm({
  review,
  busy,
  onApprove,
}: {
  review: RunReviewDto;
  busy: boolean;
  onApprove: (body: {
    decision: "approve";
    edits?: { caption: string | null; hashtags: string[] | null } | null;
    scheduledFor?: string | null;
  }) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [scheduling, setScheduling] = useState(false);
  const [caption, setCaption] = useState(review.caption);
  const [hashtagText, setHashtagText] = useState(review.hashtags.join(" "));
  const [when, setWhen] = useState("");

  const hashtags = useMemo(() => parseHashtags(hashtagText), [hashtagText]);
  const captionOver = editing && caption.length > MAX_CAPTION;
  const hashtagsOver = editing && hashtags.length > MAX_HASHTAGS;
  const scheduleInvalid = scheduling && (!when || new Date(when).getTime() <= Date.now());
  const blocked = busy || captionOver || hashtagsOver || scheduleInvalid;

  function submit() {
    onApprove({
      decision: "approve",
      edits: editing ? { caption, hashtags } : null,
      scheduledFor: scheduling && when ? new Date(when).toISOString() : null,
    });
  }

  return (
    <div className="stack">
      <label className="row">
        <input type="checkbox" checked={editing} onChange={(e) => setEditing(e.target.checked)} />
        <span>Edit caption &amp; hashtags before publishing</span>
      </label>

      {editing && (
        <>
          <div className="field">
            <label>Caption</label>
            <textarea rows={5} value={caption} onChange={(e) => setCaption(e.target.value)} />
            <span className={`field__hint ${captionOver ? "field__hint--over" : "muted"}`}>
              {caption.length} / {MAX_CAPTION}
            </span>
          </div>
          <div className="field">
            <label>Hashtags (space-separated)</label>
            <input
              type="text"
              value={hashtagText}
              onChange={(e) => setHashtagText(e.target.value)}
            />
            <span className={`field__hint ${hashtagsOver ? "field__hint--over" : "muted"}`}>
              {hashtags.length} / {MAX_HASHTAGS}
            </span>
          </div>
        </>
      )}

      <label className="row">
        <input type="checkbox" checked={scheduling} onChange={(e) => setScheduling(e.target.checked)} />
        <span>Schedule for later</span>
      </label>

      {scheduling && (
        <div className="field">
          <label>Publish at</label>
          <input type="datetime-local" value={when} onChange={(e) => setWhen(e.target.value)} />
          {scheduleInvalid && <span className="field__hint field__hint--over">Pick a future time.</span>}
        </div>
      )}

      <div>
        <button className="btn btn--primary" disabled={blocked} onClick={submit}>
          {scheduling ? "Schedule publish" : "Approve & publish"}
        </button>
      </div>
    </div>
  );
}

function RegenerateForm({
  modes,
  remaining,
  busy,
  onRegenerate,
}: {
  modes: RegenerateMode[];
  remaining: number;
  busy: boolean;
  onRegenerate: (mode: RegenerateMode, reason: string | null) => void;
}) {
  const [mode, setMode] = useState<RegenerateMode>(modes[0] ?? "same-angle");
  const [reason, setReason] = useState("");

  return (
    <div className="stack">
      <p className="muted">{remaining} regenerate{remaining === 1 ? "" : "s"} remaining.</p>
      <div className="field">
        <label>Mode</label>
        {modes.map((m) => (
          <label key={m} className="row">
            <input type="radio" name="mode" checked={mode === m} onChange={() => setMode(m)} />
            <span>{MODE_LABEL[m]}</span>
          </label>
        ))}
      </div>
      <div className="field">
        <label>Reason (optional)</label>
        <textarea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
      </div>
      <div>
        <button
          className="btn btn--primary"
          disabled={busy}
          onClick={() => onRegenerate(mode, reason.trim() || null)}
        >
          Regenerate
        </button>
      </div>
    </div>
  );
}

function RejectForm({ busy, onReject }: { busy: boolean; onReject: (reason: string | null) => void }) {
  const [reason, setReason] = useState("");
  return (
    <div className="stack">
      <div className="field">
        <label>Reason (optional)</label>
        <textarea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
      </div>
      <div>
        <button className="btn btn--danger" disabled={busy} onClick={() => onReject(reason.trim() || null)}>
          Reject
        </button>
      </div>
    </div>
  );
}

function CancelForm({ busy, onCancel }: { busy: boolean; onCancel: (reason: string | null) => void }) {
  const [reason, setReason] = useState("");
  return (
    <div className="stack">
      <div className="field">
        <label>Reason (optional)</label>
        <textarea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
      </div>
      <div>
        <button className="btn btn--danger" disabled={busy} onClick={() => onCancel(reason.trim() || null)}>
          Cancel scheduled publish
        </button>
      </div>
    </div>
  );
}

function parseHashtags(text: string): string[] {
  return text.split(/\s+/).map((t) => t.trim()).filter((t) => t.length > 0);
}

function isTerminal(status: RunReviewDto["status"]): boolean {
  return status === "Done" || status === "Failed" || status === "Rejected" || status === "Cancelled";
}
