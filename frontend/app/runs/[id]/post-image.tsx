"use client";

import { useEffect, useState } from "react";
import { fetchMediaObjectUrl } from "@/lib/api-client";

/**
 * Renders the run's image by fetching the brand-scoped media proxy through the typed client (the
 * X-Brand-Id header can't ride on an `<img>` element) and binding the resulting blob URL. A degraded
 * (caption-only, DL-029) run shows the reason instead of an image. The blob URL is revoked on unmount.
 */
export function PostImage({
  brandId,
  runId,
  degraded,
  degradedReason,
}: {
  brandId: string;
  runId: string;
  degraded: boolean;
  degradedReason: string | null;
}) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (degraded) {
      return;
    }
    let active = true;
    let objectUrl: string | null = null;
    fetchMediaObjectUrl(brandId, runId)
      .then((url) => {
        if (active) {
          objectUrl = url;
          setSrc(url);
        } else {
          URL.revokeObjectURL(url);
        }
      })
      .catch(() => active && setFailed(true));
    return () => {
      active = false;
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
    };
  }, [brandId, runId, degraded]);

  if (degraded) {
    return (
      <div className="post-image post-image--empty">
        {degradedReason ?? "Caption-only — media was skipped to stay within budget."}
      </div>
    );
  }
  if (failed) {
    return <div className="post-image post-image--empty">Image unavailable.</div>;
  }
  if (!src) {
    return <div className="post-image post-image--empty">Loading image…</div>;
  }
  // eslint-disable-next-line @next/next/no-img-element -- blob object URL, not a static asset
  return <img className="post-image" src={src} alt="Generated post preview" />;
}
