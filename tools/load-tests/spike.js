// Spike test: a sudden burst (e.g. a promotion going live), then back to normal. The system must absorb it
// without errors and recover its normal latency afterwards.
import { check } from 'k6';
import { api, createProducts, deleteProducts, readToken, signIn } from './lib/api.js';

export const options = {
  scenarios: {
    spike: {
      executor: 'ramping-arrival-rate',
      startRate: 20,
      timeUnit: '1s',
      preAllocatedVUs: 100,
      maxVUs: 600,
      stages: [
        { target: 20, duration: '30s' },
        { target: 500, duration: '10s' }, // spike
        { target: 500, duration: '30s' },
        { target: 20, duration: '10s' }, // drop
        { target: 20, duration: '30s' }, // recovery
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.02'],
    checks: ['rate>0.98'],
  },
};

export function setup() {
  const tokens = signIn();
  return { tokens, ids: createProducts(tokens.manager, 20, 1000000, 'Spike') };
}

export default function ({ tokens, ids }) {
  const token = readToken(tokens);
  const response = Math.random() < 0.8 ? api.list(token) : api.get(token, ids[Math.floor(Math.random() * ids.length)]);
  check(response, { 'ok': (r) => r.status === 200 });
}

export function teardown({ tokens, ids }) {
  deleteProducts(tokens.manager, ids);
}
