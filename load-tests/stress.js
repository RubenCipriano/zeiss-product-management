// Stress test: keep increasing the request rate to find where latency and errors start to degrade.
// No hard thresholds: the result is the curve, read alongside `kubectl top pods`.
import { check } from 'k6';
import { api, createProducts, deleteProducts, signIn, tokenForVu } from './lib/api.js';

export const options = {
  scenarios: {
    stress: {
      executor: 'ramping-arrival-rate',
      startRate: 50,
      timeUnit: '1s',
      preAllocatedVUs: 100,
      maxVUs: 800,
      stages: [
        { target: 200, duration: '1m' },
        { target: 400, duration: '1m' },
        { target: 600, duration: '1m' },
        { target: 800, duration: '1m' },
        { target: 0, duration: '20s' },
      ],
    },
  },
  thresholds: {
    // Reported, not enforced: tells us whether the system stayed within its SLO under stress.
    http_req_duration: [{ threshold: 'p(95)<1000', abortOnFail: false }],
    http_req_failed: [{ threshold: 'rate<0.05', abortOnFail: false }],
  },
};

export function setup() {
  const tokens = signIn();
  return { tokens, ids: createProducts(tokens.manager, 40, 1000000, 'Stress') };
}

export default function ({ tokens, ids }) {
  const token = tokenForVu(tokens);
  const id = ids[Math.floor(Math.random() * ids.length)];
  const roll = Math.random();
  const response =
    roll < 0.5 ? api.list(token) : roll < 0.8 ? api.get(token, id) : roll < 0.9 ? api.search(token, 'lens') : api.decrement(token, id, 1);
  check(response, { 'not a server error': (r) => r.status < 500 });
}

export function teardown({ tokens, ids }) {
  deleteProducts(tokens.manager, ids);
}
