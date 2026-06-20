"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useBrand } from "@/components/brand-context";
import { StatusBadge } from "@/components/status-badge";
import { ApiError, createRun, listRuns } from "@/lib/api-client";
import type { Modality, RunSummaryDto } from "@/lib/api-types";

const MODALITIES: readonly Modality[] = ["Image", "Video"];

export default function DashboardPage() {
  const { brandId, ready } = useBrand();
  const router = useRouter();

  const [runs, setRuns] = useState<RunSummaryDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [generating, setGenerating] = useState(false);
  const [modality, setModality] = useState<Modality>("Image");

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
      // Video → text-to-video (TextPrompt): Veo renders the brief like Nano Banana does for images, with NO
      // first-frame image. This key has no image-to-video on any Veo model, so ImageSeed isn't usable here.
      // Image → no body (backend defaults to an image run).
      const { runId } = await createRun(
        brandId,
        modality === "Video" ? { modality: "Video", videoSource: "TextPrompt" } : undefined,
      );
      router.push(`/runs/${runId}`);
    } catch (e) {
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
      setGenerating(false);
    }
  }, [brandId, modality, router]);

  return (
    <main className="page stack">
      <div className="row row--between">
        <div>
          <div className="eyebrow">Dashboard</div>
          <h1>Runs</h1>
        </div>
        <div className="row" style={{ gap: 12 }}>
          <div className="tabs" role="group" aria-label="Content modality">
            {MODALITIES.map((m) => (
              <button
                key={m}
                type="button"
                className={`tab ${modality === m ? "tab--active" : ""}`}
                onClick={() => setModality(m)}
                disabled={generating}
              >
                {m}
              </button>
            ))}
          </div>
          <button className="btn btn--primary" onClick={generate} disabled={!brandId || generating}>
            {generating ? "Starting…" : "Generate a post"}
          </button>
        </div>
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
