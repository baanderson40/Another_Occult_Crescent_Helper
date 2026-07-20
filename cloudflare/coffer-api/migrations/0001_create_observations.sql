CREATE TABLE observations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    territory_id INTEGER NOT NULL,
    map_id INTEGER,
    world_x REAL NOT NULL,
    world_y REAL NOT NULL,
    world_z REAL NOT NULL,
    coffer_type TEXT,
    installation_hash TEXT NOT NULL,
    plugin_version TEXT NOT NULL,
    game_version TEXT,
    observed_at_utc TEXT NOT NULL,
    received_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    processed INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_observations_unprocessed
ON observations(processed);

CREATE INDEX idx_observations_territory
ON observations(territory_id);

CREATE INDEX idx_observations_duplicate_window
ON observations(installation_hash, territory_id, received_at_utc);

CREATE INDEX idx_observations_received
ON observations(received_at_utc);
