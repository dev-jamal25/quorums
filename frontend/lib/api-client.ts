// Typed API client — the single seam between the dashboard and the backend (frontend.md). No `fetch`
// lives in components; no business logic lives here. Brand scope travels as the `X-Brand-Id` header on
// every call (the server binds RLS from it), so it is a required argument, never caller-supplied data
// baked into a path. The contract types live in `api-types.ts`.

import type {
  ApprovalRequest,
  CancelRequest,
  CreateRunRequest,
  CreateRunResponse,
  RunReviewDto,
  RunStatusResponse,
  RunSummaryDto,
} from "@/lib/api-types";

export const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:8080";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

function url(path: string): string {
  return `${apiBaseUrl}/${path.replace(/^\//, "")}`;
}

async function request<T>(
  path: string,
  brandId: string,
  init: RequestInit = {},
): Promise<T> {
  const response = await fetch(url(path), {
    ...init,
    headers: {
      "X-Brand-Id": brandId,
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...init.headers,
    },
    cache: "no-store",
  });

  if (!response.ok) {
    throw new ApiError(response.status, await safeError(response));
  }

  // 202 Accepted / 200 OK with a JSON body; 200 OK with no body (the gate endpoints return empty).
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

async function safeError(response: Response): Promise<string> {
  try {
    const text = await response.text();
    return text || response.statusText;
  } catch {
    return response.statusText;
  }
}

/**
 * Triggers a new run (enqueues the durable job, returns the run id + resolved modality). Omit
 * <c>request</c> (or pass an Image modality) for an image run — the backend defaults to image when the
 * body is absent (DL-058). A video run sends `{ modality: "Video", videoSource: "ImageSeed" }`.
 */
export function createRun(
  brandId: string,
  options?: CreateRunRequest,
): Promise<CreateRunResponse> {
  return request<CreateRunResponse>("runs", brandId, {
    method: "POST",
    ...(options ? { body: JSON.stringify(options) } : {}),
  });
}

/** Lists the brand's runs (newest first) for the dashboard. */
export function listRuns(brandId: string): Promise<RunSummaryDto[]> {
  return request<RunSummaryDto[]>("runs", brandId);
}

/** The lightweight status (+ graph phase) of one run. */
export function getRun(brandId: string, runId: string): Promise<RunStatusResponse> {
  return request<RunStatusResponse>(`runs/${runId}`, brandId);
}

/** The full server-computed review projection for the approval screen. */
export function getReview(brandId: string, runId: string): Promise<RunReviewDto> {
  return request<RunReviewDto>(`runs/${runId}/review`, brandId);
}

/**
 * Fetches the run's media as an object URL the browser can `<img src>`. The brand header cannot ride
 * on an `<img>` element, so the proxy is fetched here (with the header) and handed back as a blob URL;
 * the caller revokes it when done.
 */
export async function fetchMediaObjectUrl(brandId: string, runId: string): Promise<string> {
  const response = await fetch(url(`runs/${runId}/media`), {
    headers: { "X-Brand-Id": brandId },
    cache: "no-store",
  });
  if (!response.ok) {
    throw new ApiError(response.status, response.statusText);
  }
  return URL.createObjectURL(await response.blob());
}

/** Posts a gate decision (approve / reject / regenerate). */
export function postApproval(
  brandId: string,
  runId: string,
  body: ApprovalRequest,
): Promise<void> {
  return request<void>(`runs/${runId}/approval`, brandId, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

/** Cancels a Scheduled run before it fires (DL-037). */
export function cancelRun(
  brandId: string,
  runId: string,
  body: CancelRequest,
): Promise<void> {
  return request<void>(`runs/${runId}/cancel`, brandId, {
    method: "POST",
    body: JSON.stringify(body),
  });
}
