/**
 * A client-side Idempotency-Key. Generated once per order attempt and reused on every retry of
 * that attempt; a new order gets a new key. Unrelated to the server's Snowflake ids.
 */
export function newKey(): string {
  return crypto.randomUUID();
}
