ALTER TABLE observations ADD COLUMN data_id INTEGER;

CREATE INDEX idx_observations_data_id
ON observations(data_id);
