const MAX_OBSERVATIONS_PER_RUN = 100;
const CLUSTER_RADIUS = 1.5;
const REVIEW_REPORTER_THRESHOLD = 3;

interface DatabaseEnv {
  DB: D1Database;
}

interface PendingObservation {
  id: number;
  territory_id: number;
  data_id: number;
  world_x: number;
  world_y: number;
  world_z: number;
  installation_hash: string;
  observed_at_utc: string;
}

interface Candidate {
  id: number;
  centroid_x: number;
  centroid_y: number;
  centroid_z: number;
}

export interface ProcessorResult {
  scanned: number;
  assigned: number;
  failed: number;
}

export async function processPendingObservations(env: DatabaseEnv): Promise<ProcessorResult> {
  const pending = await env.DB.prepare(`
    SELECT
      o.id,
      o.territory_id,
      o.data_id,
      o.world_x,
      o.world_y,
      o.world_z,
      o.installation_hash,
      o.observed_at_utc
    FROM observations o
    WHERE o.processed = 0
      AND o.data_id IS NOT NULL
      AND NOT EXISTS (
        SELECT 1
        FROM observation_candidate_members m
        WHERE m.observation_id = o.id
      )
    ORDER BY o.id
    LIMIT ?
  `).bind(MAX_OBSERVATIONS_PER_RUN).all<PendingObservation>();

  let assigned = 0;
  let failed = 0;
  for (const observation of pending.results) {
    try {
      let candidate = await findCandidate(env.DB, observation);
      if (candidate === null) {
        candidate = await createCandidate(env.DB, observation);
      }

      await assignObservation(env.DB, candidate.id, observation);
      assigned++;
    } catch (error) {
      failed++;
      console.error(`Failed to process observation ${observation.id}`, error);
    }
  }

  return {
    scanned: pending.results.length,
    assigned,
    failed,
  };
}

async function findCandidate(
  db: D1Database,
  observation: PendingObservation,
): Promise<Candidate | null> {
  const candidates = await db.prepare(`
    SELECT id, centroid_x, centroid_y, centroid_z
    FROM observation_candidates
    WHERE territory_id = ?
      AND data_id = ?
      AND status != 'rejected'
      AND ABS(centroid_x - ?) <= ?
      AND ABS(centroid_y - ?) <= ?
      AND ABS(centroid_z - ?) <= ?
  `).bind(
    observation.territory_id,
    observation.data_id,
    observation.world_x,
    CLUSTER_RADIUS,
    observation.world_y,
    CLUSTER_RADIUS,
    observation.world_z,
    CLUSTER_RADIUS,
  ).all<Candidate>();

  const radiusSquared = CLUSTER_RADIUS * CLUSTER_RADIUS;
  return candidates.results
    .map(candidate => ({
      candidate,
      distanceSquared: distanceSquared(candidate, observation),
    }))
    .filter(entry => entry.distanceSquared <= radiusSquared)
    .sort((left, right) => left.distanceSquared - right.distanceSquared)[0]?.candidate ?? null;
}

async function createCandidate(
  db: D1Database,
  observation: PendingObservation,
): Promise<Candidate> {
  const result = await db.prepare(`
    INSERT INTO observation_candidates (
      territory_id,
      data_id,
      centroid_x,
      centroid_y,
      centroid_z,
      first_observed_at_utc,
      last_observed_at_utc
    ) VALUES (?, ?, ?, ?, ?, ?, ?)
  `).bind(
    observation.territory_id,
    observation.data_id,
    observation.world_x,
    observation.world_y,
    observation.world_z,
    observation.observed_at_utc,
    observation.observed_at_utc,
  ).run();

  if (!result.success || result.meta.last_row_id === undefined) {
    throw new Error("Candidate insert failed.");
  }

  return {
    id: result.meta.last_row_id,
    centroid_x: observation.world_x,
    centroid_y: observation.world_y,
    centroid_z: observation.world_z,
  };
}

async function assignObservation(
  db: D1Database,
  candidateId: number,
  observation: PendingObservation,
): Promise<void> {
  await db.prepare(`
    INSERT OR IGNORE INTO observation_candidate_members (
      candidate_id,
      observation_id,
      installation_hash
    ) VALUES (?, ?, ?)
  `).bind(candidateId, observation.id, observation.installation_hash).run();

  await db.prepare(`
    UPDATE observation_candidates
    SET observation_count = (
          SELECT COUNT(*)
          FROM observation_candidate_members
          WHERE candidate_id = ?
        ),
        distinct_installation_count = (
          SELECT COUNT(DISTINCT installation_hash)
          FROM observation_candidate_members
          WHERE candidate_id = ?
        ),
        centroid_x = (
          SELECT AVG(o.world_x)
          FROM observation_candidate_members m
          JOIN observations o ON o.id = m.observation_id
          WHERE m.candidate_id = ?
        ),
        centroid_y = (
          SELECT AVG(o.world_y)
          FROM observation_candidate_members m
          JOIN observations o ON o.id = m.observation_id
          WHERE m.candidate_id = ?
        ),
        centroid_z = (
          SELECT AVG(o.world_z)
          FROM observation_candidate_members m
          JOIN observations o ON o.id = m.observation_id
          WHERE m.candidate_id = ?
        ),
        first_observed_at_utc = (
          SELECT MIN(o.observed_at_utc)
          FROM observation_candidate_members m
          JOIN observations o ON o.id = m.observation_id
          WHERE m.candidate_id = ?
        ),
        last_observed_at_utc = (
          SELECT MAX(o.observed_at_utc)
          FROM observation_candidate_members m
          JOIN observations o ON o.id = m.observation_id
          WHERE m.candidate_id = ?
        ),
        status = CASE
          WHEN status = 'pending'
            AND (
              SELECT COUNT(DISTINCT installation_hash)
              FROM observation_candidate_members
              WHERE candidate_id = ?
            ) >= ?
            THEN 'accepted'
          ELSE status
        END,
        acceptance_method = CASE
          WHEN status = 'pending'
            AND (
              SELECT COUNT(DISTINCT installation_hash)
              FROM observation_candidate_members
              WHERE candidate_id = ?
            ) >= ?
            THEN 'automatic'
          ELSE acceptance_method
        END,
        reviewed_at_utc = CASE
          WHEN status = 'pending'
            AND (
              SELECT COUNT(DISTINCT installation_hash)
              FROM observation_candidate_members
              WHERE candidate_id = ?
            ) >= ?
            THEN COALESCE(reviewed_at_utc, CURRENT_TIMESTAMP)
          ELSE reviewed_at_utc
        END,
        review_note = CASE
          WHEN status = 'pending'
            AND (
              SELECT COUNT(DISTINCT installation_hash)
              FROM observation_candidate_members
              WHERE candidate_id = ?
            ) >= ?
            THEN 'Automatically accepted after three distinct installation reports.'
          ELSE review_note
        END,
        updated_at_utc = CURRENT_TIMESTAMP
    WHERE id = ?
  `).bind(
    candidateId,
    candidateId,
    candidateId,
    candidateId,
    candidateId,
    candidateId,
    candidateId,
    candidateId,
    REVIEW_REPORTER_THRESHOLD,
    candidateId,
    REVIEW_REPORTER_THRESHOLD,
    candidateId,
    REVIEW_REPORTER_THRESHOLD,
    candidateId,
    REVIEW_REPORTER_THRESHOLD,
    candidateId,
  ).run();

  await db.prepare(`
    UPDATE observations
    SET processed = 1
    WHERE id = ?
  `).bind(observation.id).run();
}

function distanceSquared(candidate: Candidate, observation: PendingObservation): number {
  const deltaX = candidate.centroid_x - observation.world_x;
  const deltaY = candidate.centroid_y - observation.world_y;
  const deltaZ = candidate.centroid_z - observation.world_z;
  return deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
}
