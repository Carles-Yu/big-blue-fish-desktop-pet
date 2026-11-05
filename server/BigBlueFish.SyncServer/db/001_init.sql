CREATE TABLE IF NOT EXISTS account (
  id uuid PRIMARY KEY,
  created_at timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS device (
  id uuid PRIMARY KEY,
  account_id uuid NOT NULL REFERENCES account(id),
  name text NOT NULL,
  token_hash text NOT NULL UNIQUE,
  created_at timestamptz NOT NULL,
  revoked_at timestamptz
);
CREATE TABLE IF NOT EXISTS pairing (
  code_hash text PRIMARY KEY,
  account_id uuid NOT NULL REFERENCES account(id),
  expires_at timestamptz NOT NULL,
  consumed_at timestamptz
);
CREATE TABLE IF NOT EXISTS profile (
  account_id uuid NOT NULL REFERENCES account(id),
  profile_id uuid NOT NULL,
  epoch bigint NOT NULL,
  PRIMARY KEY (account_id, profile_id)
);
CREATE TABLE IF NOT EXISTS record_log (
  server_seq bigserial PRIMARY KEY,
  account_id uuid NOT NULL,
  profile_id uuid NOT NULL,
  record_id uuid NOT NULL,
  revision bigint NOT NULL,
  epoch bigint NOT NULL,
  ciphertext text NOT NULL,
  nonce text NOT NULL,
  deleted boolean NOT NULL
);
CREATE INDEX IF NOT EXISTS record_log_pull ON record_log (account_id, profile_id, server_seq);
CREATE TABLE IF NOT EXISTS operation_log (
  device_id uuid NOT NULL,
  operation_id uuid NOT NULL,
  status text NOT NULL,
  server_revision bigint,
  server_seq bigint,
  PRIMARY KEY (device_id, operation_id)
);
CREATE TABLE IF NOT EXISTS envelope (
  code_hash text PRIMARY KEY,
  account_id uuid NOT NULL REFERENCES account(id),
  wrapped text NOT NULL,
  expires_at timestamptz NOT NULL,
  consumed_at timestamptz
);
