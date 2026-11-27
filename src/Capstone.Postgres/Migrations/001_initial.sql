CREATE SCHEMA IF NOT EXISTS capstone;

CREATE TABLE IF NOT EXISTS capstone.schema_version (
    version integer PRIMARY KEY,
    purpose text NOT NULL CHECK (purpose = 'mse907-synthetic-transfer-lab')
);

CREATE TABLE capstone.accounts (
    id bigint PRIMARY KEY CHECK (id > 0),
    balance numeric(19,2) NOT NULL CHECK (balance >= 0),
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    status text NOT NULL CHECK (status IN ('active', 'inactive'))
);

CREATE TABLE capstone.transfers (
    transfer_id uuid PRIMARY KEY,
    logical_request_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    source_account bigint NOT NULL REFERENCES capstone.accounts(id),
    destination_account bigint NOT NULL REFERENCES capstone.accounts(id),
    amount numeric(19,2) NOT NULL CHECK (amount > 0),
    currency text NOT NULL CHECK (currency = 'NZD'),
    CHECK (source_account <> destination_account)
);
CREATE INDEX transfers_logical_request ON capstone.transfers(logical_request_id);

CREATE TABLE capstone.ledger_entries (
    transfer_id uuid NOT NULL REFERENCES capstone.transfers(transfer_id),
    account_id bigint NOT NULL REFERENCES capstone.accounts(id),
    entry_type text NOT NULL CHECK (entry_type IN ('debit', 'credit')),
    signed_amount numeric(19,2) NOT NULL,
    PRIMARY KEY (transfer_id, entry_type),
    CHECK ((entry_type = 'debit' AND signed_amount < 0)
        OR (entry_type = 'credit' AND signed_amount > 0))
);

CREATE TABLE capstone.idempotency_records (
    scope text NOT NULL,
    endpoint text NOT NULL,
    key text NOT NULL,
    fingerprint bytea NOT NULL,
    status text NOT NULL CHECK (status IN ('processing', 'completed')),
    response jsonb,
    transfer_id uuid REFERENCES capstone.transfers(transfer_id),
    completed_at timestamptz,
    PRIMARY KEY (scope, endpoint, key),
    CHECK ((status = 'processing' AND response IS NULL AND transfer_id IS NULL AND completed_at IS NULL)
        OR (status = 'completed' AND response IS NOT NULL AND completed_at IS NOT NULL))
);
