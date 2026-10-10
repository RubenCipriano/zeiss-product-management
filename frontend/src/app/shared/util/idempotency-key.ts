/**
 * A new idempotency key (a random UUID, version 4) for one user action: the API applies a request with the same key
 * only once, so a retried or double-clicked change is not applied twice.
 *
 * `crypto.randomUUID()` exists only in secure contexts (HTTPS, or http://localhost). The app is also opened over plain
 * HTTP on a server's address (e.g. http://192.168.1.10:8080, the local deployment on a k3s server), where it is
 * missing; `crypto.getRandomValues()` is available everywhere, so the UUID is built from it there.
 */
export function newIdempotencyKey(): string {
  if (typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // variant 10 (RFC 9562)
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
