-- The tables the routine reads and writes, as the legacy schema defines them (portable SQL: H2 in tests).
CREATE TABLE IF NOT EXISTS customers (
    id   BIGINT       PRIMARY KEY,
    name VARCHAR(200) NOT NULL
);

CREATE TABLE IF NOT EXISTS invoices (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    customer_id BIGINT         NOT NULL REFERENCES customers (id),
    amount      DECIMAL(12, 2) NOT NULL,
    note        VARCHAR(200)
);
