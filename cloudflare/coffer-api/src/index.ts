import { processPendingObservations } from "./processor";

export interface Env {
  DB: D1Database;
  OBSERVATION_IP_LIMITER: RateLimit;
  ADMIN_TOKEN?: string;
}

interface CofferObservationRequest {
  territoryId: number;
  dataId?: number | null;
  mapId?: number | null;
  worldX: number;
  worldY: number;
  worldZ: number;
  cofferType?: string | null;
  installationHash: string;
  pluginVersion: string;
  gameVersion?: string | null;
  observedAtUtc: string;
}

const MAX_REQUEST_BYTES = 8 * 1024;
const MAX_STRING_LENGTH = 128;
const UTC_TIMESTAMP_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/;
const CANDIDATE_STATUSES = new Set(["pending", "review", "accepted", "rejected"]);
const RETAINED_COFFER_DATA_IDS = new Set([2014741, 2014742, 2014743]);

function jsonResponse(body: unknown, status = 200, extraHeaders: Record<string, string> = {}): Response {
  return Response.json(body, {
    status,
    headers: {
      "Cache-Control": "no-store",
      ...extraHeaders,
    },
  });
}

async function enforceObservationRateLimit(request: Request, env: Env): Promise<Response | null> {
  const key = request.headers.get("CF-Connecting-IP") ?? "local-development";
  const result = await env.OBSERVATION_IP_LIMITER.limit({ key });
  if (result.success) {
    return null;
  }

  return jsonResponse(
    { accepted: false, error: "Rate limit exceeded." },
    429,
    { "Retry-After": "60" },
  );
}

function authorizeAdmin(request: Request, env: Env): Response | null {
  const configuredToken = env.ADMIN_TOKEN?.trim();
  if (!configuredToken) {
    return jsonResponse({ error: "Not found." }, 404);
  }

  if (request.headers.get("Authorization") !== `Bearer ${configuredToken}`) {
    return jsonResponse(
      { error: "Unauthorized." },
      401,
      { "WWW-Authenticate": "Bearer" },
    );
  }

  return null;
}

function parsePositiveInteger(value: string | null): number | null {
  if (value === null || !/^\d+$/.test(value)) {
    return null;
  }

  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

function parseNonNegativeInteger(value: string | null): number | null {
  if (value === null || !/^\d+$/.test(value)) {
    return null;
  }

  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

async function listCandidates(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const status = url.searchParams.get("status");
  if (status !== null && !CANDIDATE_STATUSES.has(status)) {
    return jsonResponse({ error: "Invalid candidate status." }, 400);
  }

  const territoryId = url.searchParams.get("territoryId");
  const parsedTerritoryId = territoryId === null ? null : parsePositiveInteger(territoryId);
  if (territoryId !== null && parsedTerritoryId === null) {
    return jsonResponse({ error: "Invalid territoryId." }, 400);
  }

  const dataId = url.searchParams.get("dataId");
  const parsedDataId = dataId === null ? null : parsePositiveInteger(dataId);
  if (dataId !== null && parsedDataId === null) {
    return jsonResponse({ error: "Invalid dataId." }, 400);
  }

  const requestedLimit = url.searchParams.get("limit");
  const parsedLimit = requestedLimit === null ? 50 : parsePositiveInteger(requestedLimit);
  if (parsedLimit === null) {
    return jsonResponse({ error: "Invalid limit." }, 400);
  }

  const requestedOffset = url.searchParams.get("offset");
  const parsedOffset = requestedOffset === null ? 0 : parseNonNegativeInteger(requestedOffset);
  if (parsedOffset === null) {
    return jsonResponse({ error: "Invalid offset." }, 400);
  }

  const clauses = ["1 = 1"];
  const values: (string | number)[] = [];
  if (status !== null) {
    clauses.push("status = ?");
    values.push(status);
  }
  if (parsedTerritoryId !== null) {
    clauses.push("territory_id = ?");
    values.push(parsedTerritoryId);
  }
  if (parsedDataId !== null) {
    clauses.push("data_id = ?");
    values.push(parsedDataId);
  }

  const candidates = await env.DB.prepare(`
    SELECT id, territory_id, data_id,
      centroid_x, centroid_y, centroid_z,
      observation_count, distinct_installation_count,
      first_observed_at_utc, last_observed_at_utc,
      status, created_at_utc, updated_at_utc,
      reviewed_at_utc, review_note, acceptance_method
    FROM observation_candidates
    WHERE ${clauses.join(" AND ")}
    ORDER BY updated_at_utc DESC, id DESC
    LIMIT ? OFFSET ?
  `).bind(...values, Math.min(parsedLimit, 100) + 1, parsedOffset).all();

  const pageSize = Math.min(parsedLimit, 100);
  return jsonResponse({
    candidates: candidates.results.slice(0, pageSize),
    hasMore: candidates.results.length > pageSize,
  });
}

async function getCandidateDetail(candidateId: number, env: Env): Promise<Response> {
  const candidate = await env.DB.prepare(`
    SELECT id, territory_id, data_id,
      centroid_x, centroid_y, centroid_z,
      observation_count, distinct_installation_count,
      first_observed_at_utc, last_observed_at_utc,
      status, created_at_utc, updated_at_utc,
      reviewed_at_utc, review_note, acceptance_method
    FROM observation_candidates
    WHERE id = ?
  `).bind(candidateId).first();

  if (candidate === null) {
    return jsonResponse({ error: "Candidate not found." }, 404);
  }

  const members = await env.DB.prepare(`
    SELECT o.id AS observation_id,
      o.territory_id, o.data_id,
      o.world_x, o.world_y, o.world_z,
      o.coffer_type, o.plugin_version,
      o.game_version, o.observed_at_utc,
      o.received_at_utc
    FROM observation_candidate_members m
    JOIN observations o ON o.id = m.observation_id
    WHERE m.candidate_id = ?
    ORDER BY o.observed_at_utc, o.id
  `).bind(candidateId).all();

  return jsonResponse({ candidate, members: members.results });
}

async function listInstallationHashes(env: Env): Promise<Response> {
  const hashes = await env.DB.prepare(`
    SELECT installation_hash,
      COUNT(*) AS observation_count,
      MIN(observed_at_utc) AS first_observed_at_utc,
      MAX(observed_at_utc) AS last_observed_at_utc
    FROM observations
    GROUP BY installation_hash
    ORDER BY installation_hash
  `).all();

  return jsonResponse({
    installationHashes: hashes.results,
    count: hashes.results.length,
  });
}

async function exportAcceptedCandidates(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const territoryId = url.searchParams.get("territoryId");
  const parsedTerritoryId = territoryId === null ? null : parsePositiveInteger(territoryId);
  if (territoryId !== null && parsedTerritoryId === null) {
    return jsonResponse({ error: "Invalid territoryId." }, 400);
  }

  const dataId = url.searchParams.get("dataId");
  const parsedDataId = dataId === null ? null : parsePositiveInteger(dataId);
  if (dataId !== null && parsedDataId === null) {
    return jsonResponse({ error: "Invalid dataId." }, 400);
  }

  const clauses = ["status = 'accepted'"];
  const values: number[] = [];
  if (parsedTerritoryId !== null) {
    clauses.push("territory_id = ?");
    values.push(parsedTerritoryId);
  }
  if (parsedDataId !== null) {
    clauses.push("data_id = ?");
    values.push(parsedDataId);
  }

  const candidates = await env.DB.prepare(`
    SELECT id, territory_id, data_id,
      centroid_x, centroid_y, centroid_z,
      observation_count, distinct_installation_count,
      first_observed_at_utc, last_observed_at_utc,
      acceptance_method
    FROM observation_candidates
    WHERE ${clauses.join(" AND ")}
    ORDER BY territory_id, data_id,
      centroid_x, centroid_y, centroid_z, id
  `).bind(...values).all<{
    id: number;
    territory_id: number;
    data_id: number;
    centroid_x: number;
    centroid_y: number;
    centroid_z: number;
    observation_count: number;
    distinct_installation_count: number;
    first_observed_at_utc: string;
    last_observed_at_utc: string;
    acceptance_method: "automatic" | "manual" | null;
  }>();

  return jsonResponse(
    {
      schemaVersion: 1,
      generatedAtUtc: new Date().toISOString(),
      candidates: candidates.results.map(candidate => ({
        candidateId: candidate.id,
        territoryId: candidate.territory_id,
        dataId: candidate.data_id,
        position: {
          x: candidate.centroid_x,
          y: candidate.centroid_y,
          z: candidate.centroid_z,
        },
        observationCount: candidate.observation_count,
        distinctInstallationCount: candidate.distinct_installation_count,
        firstObservedAtUtc: candidate.first_observed_at_utc,
        lastObservedAtUtc: candidate.last_observed_at_utc,
        acceptanceMethod: candidate.acceptance_method,
      })),
    },
    200,
    { "Content-Disposition": "attachment; filename=\"accepted-candidates.json\"" },
  );
}

async function reviewCandidate(request: Request, candidateId: number, env: Env): Promise<Response> {
  const body = await parseJsonBody(request);
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return jsonResponse({ error: "Body must be a JSON object." }, 400);
  }

  const input = body as { status?: unknown; note?: unknown };
  if (typeof input.status !== "string" || !CANDIDATE_STATUSES.has(input.status)) {
    return jsonResponse({ error: "Invalid candidate status." }, 400);
  }

  if (input.note !== undefined
    && input.note !== null
    && (typeof input.note !== "string" || input.note.length > 512)) {
    return jsonResponse({ error: "Invalid review note." }, 400);
  }

  const existing = await env.DB.prepare(
    "SELECT id FROM observation_candidates WHERE id = ?",
  ).bind(candidateId).first();
  if (existing === null) {
    return jsonResponse({ error: "Candidate not found." }, 404);
  }

  const reviewTimestamp = input.status === "accepted" || input.status === "rejected"
    ? new Date().toISOString()
    : null;
  await env.DB.prepare(`
    UPDATE observation_candidates
    SET status = ?,
      review_note = ?,
      reviewed_at_utc = ?,
      acceptance_method = ?,
      updated_at_utc = CURRENT_TIMESTAMP
    WHERE id = ?
  `).bind(
    input.status,
    typeof input.note === "string" && input.note.trim().length > 0 ? input.note.trim() : null,
    reviewTimestamp,
    input.status === "accepted" || input.status === "rejected" ? "manual" : null,
    candidateId,
  ).run();

  return getCandidateDetail(candidateId, env);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function isIntegerInRange(value: unknown, minimum: number, maximum: number): value is number {
  return typeof value === "number"
    && Number.isInteger(value)
    && value >= minimum
    && value <= maximum;
}

function isAcceptableString(value: unknown, required: boolean, maxLength = MAX_STRING_LENGTH): boolean {
  if (value === null || value === undefined) {
    return !required;
  }

  return typeof value === "string"
    && value.trim().length > 0
    && value.length <= maxLength;
}

function validateObservation(value: unknown): string | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return "Body must be a JSON object.";
  }

  const observation = value as Partial<CofferObservationRequest>;
  if (!isIntegerInRange(observation.territoryId, 1, 100_000)) {
    return "Invalid territoryId.";
  }

  if (!isIntegerInRange(observation.dataId, 1, 4_294_967_295)
    || !RETAINED_COFFER_DATA_IDS.has(observation.dataId)) {
    return "Only approved coffer data IDs are accepted.";
  }

  if (observation.mapId !== null
    && observation.mapId !== undefined
    && !isIntegerInRange(observation.mapId, 1, 100_000)) {
    return "Invalid mapId.";
  }

  if (!isFiniteNumber(observation.worldX)
    || !isFiniteNumber(observation.worldY)
    || !isFiniteNumber(observation.worldZ)) {
    return "Coordinates must be finite numbers.";
  }

  const coordinateLimit = 1_000_000;
  if (Math.abs(observation.worldX) > coordinateLimit
    || Math.abs(observation.worldY) > coordinateLimit
    || Math.abs(observation.worldZ) > coordinateLimit) {
    return "Coordinates are outside the accepted range.";
  }

  if (!isAcceptableString(observation.installationHash, true, 128)) {
    return "installationHash is required.";
  }

  if (!isAcceptableString(observation.pluginVersion, true, 64)) {
    return "pluginVersion is required.";
  }

  if (!isAcceptableString(observation.gameVersion, false, 64)
    || !isAcceptableString(observation.cofferType, false, 64)
    || !isAcceptableString(observation.observedAtUtc, true, 64)) {
    return "One or more string fields are invalid.";
  }

  if (!UTC_TIMESTAMP_PATTERN.test(observation.observedAtUtc!)) {
    return "observedAtUtc must be an ISO-8601 UTC timestamp.";
  }

  const observedAt = Date.parse(observation.observedAtUtc!);
  const now = Date.now();
  if (!Number.isFinite(observedAt)) {
    return "observedAtUtc is invalid.";
  }

  if (observedAt > now + 10 * 60 * 1000) {
    return "Observation is too far in the future.";
  }

  if (observedAt < now - 7 * 24 * 60 * 60 * 1000) {
    return "Observation is too old.";
  }

  return null;
}

async function readBodyWithinLimit(request: Request): Promise<string> {
  const contentLength = request.headers.get("Content-Length");
  if (contentLength !== null && Number(contentLength) > MAX_REQUEST_BYTES) {
    throw new Response("Request body is too large.", { status: 413 });
  }

  if (!request.body) {
    return "";
  }

  const reader = request.body.getReader();
  const decoder = new TextDecoder();
  const chunks: string[] = [];
  let byteLength = 0;

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      byteLength += value.byteLength;
      if (byteLength > MAX_REQUEST_BYTES) {
        await reader.cancel();
        throw new Response("Request body is too large.", { status: 413 });
      }

      chunks.push(decoder.decode(value, { stream: true }));
    }
  } finally {
    reader.releaseLock();
  }

  chunks.push(decoder.decode());
  return chunks.join("");
}

async function parseJsonBody(request: Request): Promise<unknown> {
  const contentType = request.headers.get("Content-Type") ?? "";
  const mediaType = contentType.split(";", 1)[0].trim().toLowerCase();
  if (mediaType !== "application/json") {
    throw new Response("Content-Type must be application/json.", { status: 415 });
  }

  const bodyText = await readBodyWithinLimit(request);
  try {
    return JSON.parse(bodyText);
  } catch {
    throw new Response("Invalid JSON.", { status: 400 });
  }
}

async function submitObservation(request: Request, env: Env): Promise<Response> {
  const body = await parseJsonBody(request);
  const validationError = validateObservation(body);
  if (validationError !== null) {
    return jsonResponse({ accepted: false, error: validationError }, 400);
  }

  const observation = body as CofferObservationRequest;
  const observedAtUtc = new Date(observation.observedAtUtc).toISOString();
  const result = await env.DB.prepare(`
    INSERT INTO observations (
      territory_id, data_id, map_id, world_x, world_y, world_z,
      coffer_type, installation_hash, plugin_version,
      game_version, observed_at_utc
    )
    SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?
    WHERE NOT EXISTS (
      SELECT 1
      FROM observations
      WHERE installation_hash = ?
        AND territory_id = ?
        AND ABS(world_x - ?) <= 0.1
        AND ABS(world_y - ?) <= 0.1
        AND ABS(world_z - ?) <= 0.1
        AND received_at_utc >= datetime('now', '-10 minutes')
    )
  `).bind(
    observation.territoryId,
    observation.dataId ?? null,
    observation.mapId ?? null,
    observation.worldX,
    observation.worldY,
    observation.worldZ,
    observation.cofferType?.trim() || null,
    observation.installationHash.trim(),
    observation.pluginVersion.trim(),
    observation.gameVersion?.trim() || null,
    observedAtUtc,
    observation.installationHash.trim(),
    observation.territoryId,
    observation.worldX,
    observation.worldY,
    observation.worldZ,
  ).run();

  if (!result.success) {
    return jsonResponse({ accepted: false, error: "Database insert failed." }, 500);
  }

  if (result.meta.changes === 0) {
    return jsonResponse({ accepted: true, duplicate: true });
  }

  return jsonResponse({
    accepted: true,
    duplicate: false,
    observationId: result.meta.last_row_id,
  }, 201);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return jsonResponse({ status: "ok" });
    }

    if (url.pathname.startsWith("/api/v1/admin/")) {
      const authorizationError = authorizeAdmin(request, env);
      if (authorizationError !== null) {
        return authorizationError;
      }

      try {
        if (request.method === "GET" && url.pathname === "/api/v1/admin/candidates") {
          return await listCandidates(request, env);
        }

        if (request.method === "GET" && url.pathname === "/api/v1/admin/installation-hashes") {
          return await listInstallationHashes(env);
        }

        if (request.method === "GET" && url.pathname === "/api/v1/admin/export/accepted-candidates") {
          return await exportAcceptedCandidates(request, env);
        }

        const candidateDetailMatch = url.pathname.match(/^\/api\/v1\/admin\/candidates\/(\d+)$/);
        if (candidateDetailMatch !== null && request.method === "GET") {
          const candidateId = parsePositiveInteger(candidateDetailMatch[1]);
          return candidateId === null
            ? jsonResponse({ error: "Invalid candidate ID." }, 400)
            : await getCandidateDetail(candidateId, env);
        }

        const candidateReviewMatch = url.pathname.match(/^\/api\/v1\/admin\/candidates\/(\d+)\/review$/);
        if (candidateReviewMatch !== null && request.method === "POST") {
          const candidateId = parsePositiveInteger(candidateReviewMatch[1]);
          return candidateId === null
            ? jsonResponse({ error: "Invalid candidate ID." }, 400)
            : await reviewCandidate(request, candidateId, env);
        }

        return jsonResponse({ error: "Not found." }, 404);
      } catch (error) {
        if (error instanceof Response) {
          return error;
        }

        console.error(error);
        return jsonResponse({ error: "Unexpected server error." }, 500);
      }
    }

    if (request.method === "POST" && url.pathname === "/api/v1/observations") {
      try {
        const rateLimitResponse = await enforceObservationRateLimit(request, env);
        if (rateLimitResponse !== null) {
          return rateLimitResponse;
        }

        return await submitObservation(request, env);
      } catch (error) {
        if (error instanceof Response) {
          return error;
        }

        console.error(error);
        return jsonResponse({ accepted: false, error: "Unexpected server error." }, 500);
      }
    }

    return jsonResponse({ error: "Not found." }, 404);
  },

  async scheduled(_controller: ScheduledController, env: Env, _ctx: ExecutionContext): Promise<void> {
    const result = await processPendingObservations(env);
    console.log("Observation processor completed", result);
  },
} satisfies ExportedHandler<Env>;
