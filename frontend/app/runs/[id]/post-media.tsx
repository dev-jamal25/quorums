"use client";

import { useEffect, useState } from "react";
import { fetchMediaObjectUrl } from "@/lib/api-client";
import type { Modality } from "@/lib/api-types";

/**
 * Renders the run's generated media by fetching the brand-scoped media proxy through the typed client
 * (the X-Brand-Id header can't ride on an `<img>`/`<video>` element) and binding the resulting blob URL.
 * Branches on the run's <see cref="Modality"/> (DL-058): a video run plays a `<video>`, an image run
 * shows an `<img>`. A degraded (caption-only, DL-029) run shows the reason instead — no broken media
 * element. The blob URL is revoked on unmount.
 */
export function PostMedia({
  brandId,
  runId,
  modality,
  degraded,
  degradedReason,
}: {
  brandId: string;
  runId: string;
  modality: Modality;
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

  const isVideo = modality === "Video";
  const noun = isVideo ? "video" : "image";

  if (degraded) {
    return (
      <div className="post-image post-image--empty">
        {degradedReason ?? "Caption-only — no media for this post."}
      </div>
    );
  }
  if (failed) {
    return <div className="post-image post-image--empty">{isVideo ? "Video" : "Image"} unavailable.</div>;
  }
  if (!src) {
    return <div className="post-image post-image--empty">Loading {noun}…</div>;
  }
  if (isVideo) {
    return <video className="post-image post-image--video" src={src} controls playsInline />;
  }
  // eslint-disable-next-line @next/next/no-img-element -- blob object URL, not a static asset
  return <img className="post-image" src={src} alt="Generated post preview" />;
}
