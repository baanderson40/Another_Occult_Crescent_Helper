CREATE TABLE observation_candidates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    territory_id INTEGER NOT NULL,
    data_id INTEGER NOT NULL,
    centroid_x REAL NOT NULL,
    centroid_y REAL NOT NULL,
    centroid_z REAL NOT NULL,
    observation_count INTEGER NOT NULL DEFAULT 0,
    distinct_installation_count INTEGER NOT NULL DEFAULT 0,
    first_observed_at_utc TEXT NOT NULL,
    last_observed_at_utc TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'review', 'accepted', 'rejected')),
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_observation_candidates_lookup
ON observation_candidates(territory_id, data_id, status);

CREATE TABLE observation_candidate_members (
    candidate_id INTEGER NOT NULL REFERENCES observation_candidates(id),
    observation_id INTEGER NOT NULL REFERENCES observations(id),
    installation_hash TEXT NOT NULL,
    assigned_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (candidate_id, observation_id),
    UNIQUE (observation_id)
);

CREATE INDEX idx_observation_candidate_members_candidate
ON observation_candidate_members(candidate_id);
