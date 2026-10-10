// Smoke test: one virtual user exercises every endpoint (reads as the viewer, changes stock as the clerk). Run it
// after each deployment.
import { check, sleep } from 'k6';
import { api, createProducts, deleteProducts, readToken, signIn, writeToken } from './lib/api.js';

export const options = {
  vus: 1,
  duration: '30s',
  thresholds: {
    checks: ['rate==1.0'],
    http_req_failed: ['rate==0'],
    http_req_duration: ['p(95)<500'],
  },
};

export function setup() {
  const tokens = signIn();
  return { tokens, ids: createProducts(tokens.manager, 3, 50, 'Smoke') };
}

export default function ({ tokens, ids }) {
  const token = readToken(tokens);
  const writer = writeToken(tokens);
  const id = ids[0];

  check(api.list(token), { 'list 200': (r) => r.status === 200 && r.json('totalCount') > 0 });
  check(api.get(token, id), { 'get 200 with ETag': (r) => r.status === 200 && !!r.headers['Etag'] });
  check(api.search(token, 'smoke'), { 'search finds created products': (r) => r.status === 200 && r.json('totalCount') >= 3 });
  check(api.stockLevel(token, 0, 100), { 'stock-level 200': (r) => r.status === 200 });
  check(api.categories(token), { 'categories 200': (r) => r.status === 200 });
  check(api.decrement(writer, id, 1), { 'decrement 200': (r) => r.status === 200 });
  check(api.add(writer, id, 1), { 'add 200': (r) => r.status === 200 });
  sleep(1);
}

export function teardown({ tokens, ids }) {
  deleteProducts(tokens.manager, ids);
}
