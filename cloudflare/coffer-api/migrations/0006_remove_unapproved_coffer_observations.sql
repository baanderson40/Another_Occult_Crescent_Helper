-- Keep only the three pot-reveal coffer data IDs. Delete dependent rows first.
DELETE FROM observation_candidate_members
WHERE candidate_id IN (
    SELECT id
    FROM observation_candidates
    WHERE data_id IS NULL
       OR data_id NOT IN (2014741, 2014742, 2014743)
)
OR observation_id IN (
    SELECT id
    FROM observations
    WHERE data_id IS NULL
       OR data_id NOT IN (2014741, 2014742, 2014743)
);

DELETE FROM observation_candidates
WHERE data_id IS NULL
   OR data_id NOT IN (2014741, 2014742, 2014743);

DELETE FROM observations
WHERE data_id IS NULL
   OR data_id NOT IN (2014741, 2014742, 2014743);
