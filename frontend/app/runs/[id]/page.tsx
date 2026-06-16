"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useBrand } from "@/components/brand-context";
import { StatusBadge } from "@/components/status-badge";
import { ApiError, getReview } from "@/lib/api-client";
import type { RunReviewDto } from "@/lib/api-types";
import { PostImage } from "./post-image";
import { DecisionPanel } from "./decision-panel";

// Statuses where the worker is mid-flight, so the screen polls to catch the outcome (a regenerate
// returning to the gate, an approve reaching Done). Settled states stop polling on their own.
const LIVE: ReadonlySet<RunReviewDto["status"]> = new Set(["Queued", "Running", "Publishing"]);

export default function RunReviewPage() {
  const params = useParams<{ id: string }>();
  const runId = params.id;
  const { brandId, ready } = useBrand();

  const [review, setReview] = useState<RunReviewDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!brandId) {
      return;
    }
    try {
      setError(null);
      setReview(await getReview(brandId, runId));
    } catch (e) {
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
    }
  }, [brandId, runId]);

  useEffect(() => {
    if (!ready) {
      return;
    }
    void load();
    const timer = window.setInterval(() => void load(), 4000);
    return () => window.clearInterval(timer);
  }, [ready, load]);

  return (
    <main className="page stack">
      <div className="row row--between">
        <div>
          <div className="eyebrow">
            <Link href="/">← Runs</Link>
          </div>
          <h1>Review</h1>
        </div>
        {review && <StatusBadge status={review.status} />}
      </div>

      {!brandId && ready && (
        <div className="banner banner--warn">Enter a brand id in the header to load this run.</div>
      )}
      {error && <div className="banner banner--danger">{error}</div>}

      {review && (
        <>
          {review.budgetDegraded && (
            <div className="banner banner--warn">
              {review.budgetDegradedReason ?? "This run degraded to caption-only to stay within budget."}
            </div>
          )}

          <div className="grid-2">
            {/* Left: the content the reviewer is approving. */}
            <div className="stack">
              <PostImage
                brandId={brandId}
                runId={runId}
                degraded={review.budgetDegraded}
                degradedReason={review.budgetDegradedReason}
              />
              <div className="card">
                <div className="card__body stack">
                  <p className="caption">{review.caption || <span className="muted">No caption yet.</span>}</p>
                  {review.hashtags.length > 0 && (
                    <div className="hashtags">
                      {review.hashtags.map((tag) => (
                        <span key={tag} className="tag">
                          {tag}
                        </span>
                      ))}
                    </div>
                  )}
                </div>
              </div>
            </div>

            {/* Right: why this content, alternatives, budget — the decide-with-context surface. */}
            <div className="stack">
              <Provenance review={review} />
              <DecisionPanel brandId={brandId} review={review} onActed={() => void load()} />
            </div>
          </div>

          <Timeline review={review} />
        </>
      )}

      {!review && brandId && !error && <div className="spin">Loading review…</div>}
    </main>
  );
}

function Provenance({ review }: { review: RunReviewDto }) {
  return (
    <div className="card">
      <div className="card__body stack">
        <div className="eyebrow">Why this content</div>

        {review.selectedAngle ? (
          <dl className="kvs">
            <dt>Pillar</dt>
            <dd>{review.selectedAngle.pillar}</dd>
            <dt>Objective</dt>
            <dd>{review.selectedAngle.objective}</dd>
            <dt>Angle</dt>
            <dd>{review.selectedAngle.angle}</dd>
            <dt>Rationale</dt>
            <dd>{review.selectedAngle.rationale}</dd>
          </dl>
        ) : (
          <p className="muted">No strategy selected yet.</p>
        )}

        {review.grounding && (
          <p className="muted field__hint">
            Grounding: {review.grounding.grounded ? "grounded" : "ungrounded"} · {review.grounding.confidence}{" "}
            confidence · {review.grounding.chunkIdsUsed.length} source chunk(s)
          </p>
        )}

        {review.alternativeAngles.length > 0 && (
          <>
            <hr className="divider" />
            <div className="eyebrow">Alternative angles</div>
            <div className="stack" style={{ gap: 8 }}>
              {review.alternativeAngles.map((angle) => (
                <div
                  key={angle.index}
                  className={`angle ${review.selectedAngle?.chosenIndex === angle.index ? "angle--current" : ""}`}
                >
                  <strong>{angle.pillar}</strong> · {angle.objective}
                  <div className="muted">{angle.angle}</div>
                </div>
              ))}
            </div>
          </>
        )}

        <hr className="divider" />
        <p className="muted field__hint">
          Budget: {review.budget.tokensSpent.toLocaleString()} / {review.budget.tokenBudget.toLocaleString()} tokens ·
          ${review.budget.mediaSpent.toFixed(2)} / ${review.budget.mediaBudget.toFixed(2)} media
        </p>
      </div>
    </div>
  );
}

function Timeline({ review }: { review: RunReviewDto }) {
  if (review.timeline.length === 0) {
    return null;
  }
  return (
    <div className="card">
      <div className="card__body stack">
        <div className="eyebrow">Timeline</div>
        <div className="timeline">
          {review.timeline.map((entry, i) => (
            <div key={`${entry.occurredAt}-${i}`} className="timeline__item">
              <span className={`timeline__dot ${entry.kind === "publish" ? "timeline__dot--publish" : ""}`} />
              <div>
                <div>
                  <strong>{entry.label}</strong>
                  {entry.detail && <span className="muted"> — {entry.detail}</span>}
                </div>
                <div className="timeline__time muted">{new Date(entry.occurredAt).toLocaleString()}</div>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
