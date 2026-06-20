// Typed mirror of the backend contract (Backend.Api.Dtos + the Core enums it surfaces). The API
// serializes enums as their names (targeted JsonStringEnumConverter) and properties as camelCase, so
// these string-literal unions and interfaces match the wire shape exactly. Keep in sync with the C#
// records — this file is the single source of the frontend↔backend contract.

export type RunStatus =
  | "Queued"
  | "Running"
  | "AwaitingApproval"
  | "Publishing"
  | "Done"
  | "Failed"
  | "Rejected"
  | "Scheduled"
  | "Cancelled";

export type GraphPhase =
  | "Strategy"
  | "Creative"
  | "Generation"
  | "Assembled"
  | "AwaitingApproval"
  | "Publishing"
  | "Done";

export type PostSurface = "FeedImage" | "FeedVideo" | "Reel" | "Story";

/** Per-run content modality (DL-058). Serialized as the enum name. */
export type Modality = "Image" | "Video";

/** How a video run feeds Veo (DL-058): image-seed (default) vs text-to-video. Null for an image run. */
export type VideoSource = "ImageSeed" | "TextPrompt";

export type Confidence = "Low" | "Medium" | "High";

/** The server-computed gate actions (DL-041). The UI renders these verbatim — it never recomputes policy. */
export type GateAction = "Approve" | "Reject" | "Regenerate" | "Cancel";

export interface GroundingDto {
  grounded: boolean;
  chunkIdsUsed: string[];
  confidence: Confidence;
}

export interface SelectedAngleDto {
  chosenIndex: number;
  pillar: string;
  objective: string;
  angle: string;
  rationale: string;
}

export interface AngleSummaryDto {
  index: number;
  pillar: string;
  objective: string;
  angle: string;
}

export interface BudgetDto {
  tokensSpent: number;
  tokenBudget: number;
  mediaSpent: number;
  mediaBudget: number;
}

export interface RegenerateAvailabilityDto {
  remaining: number;
  modes: string[];
}

export interface TimelineEntryDto {
  occurredAt: string;
  kind: string;
  label: string;
  detail: string | null;
}

/** The review payload the approval screen renders (DL-040, DL-041). */
export interface RunReviewDto {
  runId: string;
  status: RunStatus;
  surface: PostSurface;
  modality: Modality;
  videoSource: VideoSource | null;
  imageUrl: string | null;
  caption: string;
  hashtags: string[];
  grounding: GroundingDto | null;
  budgetDegraded: boolean;
  budgetDegradedReason: string | null;
  budget: BudgetDto;
  selectedAngle: SelectedAngleDto | null;
  alternativeAngles: AngleSummaryDto[];
  scheduledFor: string | null;
  traceUrl: string | null;
  availableActions: GateAction[];
  regenerate: RegenerateAvailabilityDto | null;
  timeline: TimelineEntryDto[];
}

export interface RunSummaryDto {
  runId: string;
  status: RunStatus;
  createdAt: string;
  updatedAt: string;
}

export interface RunStatusResponse {
  runId: string;
  status: RunStatus;
  phase: GraphPhase | null;
  modality: Modality;
  videoSource: VideoSource | null;
}

/** The optional POST /runs body that selects the modality (DL-058). Omit it (or send Image) for an image run. */
export interface CreateRunRequest {
  modality?: Modality | null;
  videoSource?: VideoSource | null;
}

export interface CreateRunResponse {
  runId: string;
  modality: Modality;
  videoSource: VideoSource | null;
}

// --- decision request bodies (mirror Backend.Api.Dtos) -------------------------------------------

/** Discriminator for the gate decision. Sent lowercase to match the documented wire form. */
export type GateDecision = "approve" | "reject" | "regenerate";

/** Regenerate mode, kebab-cased to match the server's exact-match validation (DL-036). */
export type RegenerateMode = "same-angle" | "reselect-angle";

/** The only editable surface at the gate (DL-035): caption text and hashtags. */
export interface ApprovalEdits {
  caption: string | null;
  hashtags: string[] | null;
}

export interface ApprovalRequest {
  decision: GateDecision;
  edits?: ApprovalEdits | null;
  /** UTC ISO-8601; when set, the publish is scheduled rather than immediate (DL-037). */
  scheduledFor?: string | null;
  reason?: string | null;
  mode?: RegenerateMode | null;
}

export interface CancelRequest {
  reason: string | null;
}
