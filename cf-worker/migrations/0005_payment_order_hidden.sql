-- Hide unpaid records without deleting financial history or cancelling provider orders.
CREATE TABLE IF NOT EXISTS payment_order_hidden (
 order_no TEXT PRIMARY KEY REFERENCES orders(order_no),
 hidden_at TEXT NOT NULL
);
