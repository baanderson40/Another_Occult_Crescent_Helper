# AOCCH Coffer Observation API

This Worker accepts confirmed treasure-coffer observations from the AOCCH plugin and stores them in Cloudflare D1.

## Local Setup

```powershell
npm install
# Replace database_id in wrangler.jsonc before remote deployment.
npm run db:migrate:local
npm run dev
```

The local endpoints are:

- `GET http://localhost:8787/health`
- `POST http://localhost:8787/api/v1/observations`

## Cloudflare Setup

```powershell
npx wrangler login
npx wrangler d1 create coffer-observations
# Copy the returned ID into wrangler.jsonc.
npm run db:migrate:remote
npm run deploy
```

Attach the production custom domain through Workers > Settings > Domains & Routes, then configure Cloudflare rate limiting before enabling plugin submissions broadly.

The endpoint is intentionally public. It stores only validated observations, does not expose read or delete routes, and treats installation hashes as duplicate-grouping values rather than identity proof.

The API accepts UTC timestamps ending in either `Z` or `+00:00`, including the seven fractional digits emitted by .NET `DateTimeOffset` serialization. New plugin observations also include the recognized game `dataId`.
Only pot-reveal data IDs `2014741`, `2014742`, and `2014743` are accepted; observations with other or missing data IDs are rejected.

Observation submissions are limited to 60 requests per IP per 60 seconds by the Worker Rate Limiting API. Requests over the limit receive HTTP `429` with a `Retry-After: 60` header. Health checks and scheduled processing are not rate-limited.

## Candidate Review API

The candidate review routes are disabled until an `ADMIN_TOKEN` Worker secret is configured. Set it remotely with:

```powershell
npx wrangler secret put ADMIN_TOKEN
```

List pending candidates:

```powershell
curl.exe -H "Authorization: Bearer $env:ADMIN_TOKEN" "https://aocch-coffer-api.baanderson40.workers.dev/api/v1/admin/candidates?status=pending"
```

Inspect a candidate and its member observations:

```powershell
curl.exe -H "Authorization: Bearer $env:ADMIN_TOKEN" "https://aocch-coffer-api.baanderson40.workers.dev/api/v1/admin/candidates/1"
```

Accept or reject a candidate:

```powershell
$body = @{ status = "accepted"; note = "Verified manually." } | ConvertTo-Json
Invoke-RestMethod `
  -Method Post `
  -Uri "https://aocch-coffer-api.baanderson40.workers.dev/api/v1/admin/candidates/1/review" `
  -Headers @{ Authorization = "Bearer $env:ADMIN_TOKEN" } `
  -ContentType "application/json" `
  -Body $body
```

Export accepted generic locations without installation hashes or raw member observations:

```bash
curl -H "Authorization: Bearer $ADMIN_TOKEN" \
  "https://aocch-coffer-api.baanderson40.workers.dev/api/v1/admin/export/accepted-candidates" \
  > accepted-candidates.json
```

Optional filters are available with `territoryId` and `dataId` query parameters.

The admin API does not expose installation hashes in candidate member responses. Keep the token out of source control and shell history where practical.

## Observation Processing

Every 15 minutes, the Worker processes up to 100 unassigned observations with a non-null `data_id`. It groups observations by territory, `data_id`, and a 1.5-yalm three-dimensional radius.

Candidates are automatically marked `accepted` after reports from three distinct installation hashes. The `acceptance_method` field records this as `automatic`; manual admin changes record `manual`. Raw observations remain stored unchanged. Observations with a null `data_id` remain unprocessed until a legacy grouping policy is defined.

Inspect candidates remotely with:

```powershell
npx wrangler d1 execute coffer-observations --remote --command "SELECT id, territory_id, data_id, centroid_x, centroid_y, centroid_z, observation_count, distinct_installation_count, status FROM observation_candidates ORDER BY updated_at_utc DESC;"
npx wrangler d1 execute coffer-observations --remote --command "SELECT id, data_id, processed FROM observations ORDER BY id DESC;"
npx wrangler d1 execute coffer-observations --remote --command "SELECT * FROM observation_candidates;"
```
