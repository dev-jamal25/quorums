---
paths:
  - "frontend/**/*.{ts,tsx}"
---

# Frontend (Next.js)

<!-- Loads when Claude touches the frontend. The dashboard renders state and drives the human gate; it holds no business logic. -->

## Boundary
- All server access goes through the typed `lib/api-client.ts`. No `fetch` scattered across components, no business logic in the UI — the UI renders state the API computed.

## Secrets
- No secrets, tokens, Vault calls, or DB access in the client bundle. Server-only env stays server-side; never expose it via `NEXT_PUBLIC_`.

## Human gate UX
- The approval view shows the pending checkpoint (the draft + intended action) and requires an explicit user confirm before POSTing to the gate endpoint. Never auto-confirm.
- The run/trace viewer is read-only over run state.

## Done bar
- `npx tsc --noEmit` and `npm run lint` clean, `npm run build` passes, before a frontend task is done.
