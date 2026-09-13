-- Additive upgrade only: never deletes users, codes or existing routing state.
CREATE TABLE IF NOT EXISTS mail_routing_state (
  id TEXT PRIMARY KEY NOT NULL CHECK (id = 'verification'),
  slot INTEGER NOT NULL CHECK (slot IN (0, 1))
);
