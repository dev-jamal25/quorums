// Typed API client lives here. Scaffold only: just the base URL seam.
// All brand scope and data come from the backend API over HTTP; no business
// logic in the frontend. Endpoint methods are added alongside the API surface.
export const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:8080";
