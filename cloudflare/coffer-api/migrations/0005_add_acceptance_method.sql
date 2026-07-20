ALTER TABLE observation_candidates ADD COLUMN acceptance_method TEXT
    CHECK (acceptance_method IN ('automatic', 'manual'));

UPDATE observation_candidates
SET acceptance_method = 'manual'
WHERE status IN ('accepted', 'rejected');
