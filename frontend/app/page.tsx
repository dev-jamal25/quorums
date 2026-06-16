"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useBrand } from "@/components/brand-context";
import { StatusBadge } from "@/components/status-badge";
import { ApiError, createRun, listRuns } from "@/lib/api-client";
import type { RunSummaryDto } from "@/lib/api-types";

export default function DashboardPage() {
  const { brandId, ready } = useBrand();
  const router = useRouter();

  const [runs, setRuns] = useState<RunSummaryDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [generating, setGenerating] = useState(false);

  const load = useCallback(async () => {
    if (!brandId) {
      setRuns(null);
      return;
    }
    try {
      setError(null);
      setRuns(await listRuns(brandId));
    } catch (e) {
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
    }
  }, [brandId]);

  useEffect(() => {
    if (!ready) {
      return;
    }
    void load();
    // Light polling so a freshly-triggered run advances Queued → Awaiting approval in view.
    const timer = window.setInterval(() => void load(), 4000);
    return () => window.clearInterval(timer);
  }, [ready, load]);

  const generate = useCallback(async () => {
    if (!brandId) {
      return;
    }
    setGenerating(true);
    try {
      const { runId } = await createRun(brandId);
      router.push(`/runs/${runId}`);
    } catch (e) {
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
      setGenerating(false);
    }
  }, [brandId, router]);

  return (
    <main className="page stack">
      <div className="row row--between">
        <div>
          <div className="eyebrow">Dashboard</div>
          <h1>Runs</h1>
        </div>
        <button className="btn btn--primary" onClick={generate} disabled={!brandId || generating}>
          {generating ? "Starting…" : "Generate a post"}
        </button>
      </div>

      {!brandId && ready && (
        <div className="banner banner--warn">
          Enter a brand id in the header to load its runs. (Onboard a brand via the API, then paste its id.)
        </div>
      )}

      {error && <div className="banner banner--danger">{error}</div>}

      {brandId && (
        <div className="card">
          {runs === null ? (
            <div className="card__body spin">Loading…</div>
          ) : runs.length === 0 ? (
            <div className="card__body muted">No runs yet. Generate a post to begin.</div>
          ) : (
            <div className="runlist">
              {runs.map((run) => (
                <Link key={run.runId} href={`/runs/${run.runId}`} className="runrow">
                  <span className="runrow__id">{run.runId.slice(0, 8)}</span>
                  <span className="row">
                    <span className="runrow__time muted">{new Date(run.createdAt).toLocaleString()}</span>
                    <StatusBadge status={run.status} />
                  </span>
                </Link>
              ))}
            </div>
          )}
        </div>
      )}
    </main>
  );
}
