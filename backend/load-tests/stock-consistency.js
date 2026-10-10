// Correctness under load: many concurrent orders, spread over all API replicas, for one product with limited
// stock. Exactly `STOCK` orders may succeed, the rest must be rejected with 409, and the final stock must be 0.
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { api, createProducts, deleteProducts, signIn, writeToken } from './lib/api.js';

const STOCK = 100;
const ORDERS = 300;

const succeeded = new Counter('orders_succeeded');
const rejected = new Counter('orders_rejected_insufficient_stock');
const unexpected = new Counter('orders_unexpected_status');

export const options = {
  scenarios: {
    orders: {
      executor: 'shared-iterations',
      vus: 30,
      iterations: ORDERS,
      maxDuration: '2m',
    },
  },
  thresholds: {
    orders_succeeded: [`count==${STOCK}`],
    orders_rejected_insufficient_stock: [`count==${ORDERS - STOCK}`],
    orders_unexpected_status: ['count==0'],
    checks: ['rate==1.0'],
  },
};

export function setup() {
  const tokens = signIn();
  return { tokens, id: createProducts(tokens.manager, 1, STOCK, 'Consistency')[0] };
}

export default function ({ tokens, id }) {
  // A well-behaved client: when rate limited, wait as told and retry, so every order is eventually processed.
  let response = api.decrement(writeToken(tokens), id, 1, [200, 409, 429]);
  for (let attempt = 0; response.status === 429 && attempt < 10; attempt++) {
    sleep(Number(response.headers['Retry-After'] || 1));
    response = api.decrement(writeToken(tokens), id, 1, [200, 409, 429]);
  }
  if (response.status === 200) {
    succeeded.add(1);
  } else if (response.status === 409) {
    rejected.add(1);
  } else {
    unexpected.add(1);
  }
}

export function teardown({ tokens, id }) {
  const final = api.get(tokens.manager, id);
  check(final, { 'final stock is exactly 0 (no overselling, no lost updates)': (r) => r.json('stockQuantity') === 0 });
  deleteProducts(tokens.manager, [id]);
}
