import http from 'k6/http';
import { check, fail } from 'k6';
import { BASE_URL, LOAD_TEST_USERS, TOKEN_URL } from './config.js';

// Each virtual user acts as one of several registered API consumers, so per-user rate limits are exercised
// realistically. Tokens come from the API's own token endpoint.
function requestToken(clientId, clientSecret) {
  const response = http.post(TOKEN_URL, JSON.stringify({ clientId, clientSecret }), {
    headers: { 'Content-Type': 'application/json' },
    tags: { name: 'POST /auth/token' },
  });
  if (response.status !== 200) {
    fail(`Could not get a token for ${clientId}: ${response.status} ${response.body}`);
  }
  return response.json('accessToken');
}

/** Tokens for the load-test users (stock-clerk) plus one manager token for setup and teardown. */
export function signIn() {
  const users = [];
  for (let i = 1; i <= LOAD_TEST_USERS; i++) {
    users.push(requestToken(`load-tester-${String(i).padStart(2, '0')}`, 'load-test'));
  }
  return { users, manager: requestToken('manager', 'manager') };
}

export function tokenForVu(tokens) {
  return tokens.users[(__VU - 1) % tokens.users.length];
}

function params(token, name, extra = {}) {
  return {
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    tags: { name },
    ...extra,
  };
}

export const api = {
  list: (token, page = 1, pageSize = 20) =>
    http.get(`${BASE_URL}/api/products?page=${page}&pageSize=${pageSize}`, params(token, 'GET /products')),
  get: (token, id) => http.get(`${BASE_URL}/api/products/${id}`, params(token, 'GET /products/{id}')),
  search: (token, name) =>
    http.get(`${BASE_URL}/api/products/search?name=${encodeURIComponent(name)}`, params(token, 'GET /products/search')),
  stockLevel: (token, min, max, page = 1) =>
    http.get(`${BASE_URL}/api/products/stock-level?min=${min}&max=${max}&page=${page}`, params(token, 'GET /products/stock-level')),
  categories: (token) => http.get(`${BASE_URL}/api/categories`, params(token, 'GET /categories')),
  decrement: (token, id, quantity = 1, expectedStatuses = [200, 409]) =>
    http.post(`${BASE_URL}/api/products/${id}/decrement-stock/${quantity}`, null,
      params(token, 'POST /products/{id}/decrement-stock', { responseCallback: http.expectedStatuses(...expectedStatuses) })),
  add: (token, id, quantity = 1) =>
    http.post(`${BASE_URL}/api/products/${id}/add-to-stock/${quantity}`, null, params(token, 'POST /products/{id}/add-to-stock')),
  create: (token, body) => http.post(`${BASE_URL}/api/products`, JSON.stringify(body), params(token, 'POST /products')),
  remove: (token, id) => http.del(`${BASE_URL}/api/products/${id}`, null, params(token, 'DELETE /products/{id}')),
};

/** Creates products for a test run (manager) and returns their ids. */
export function createProducts(managerToken, count, stockQuantity, prefix = 'Load test') {
  const ids = [];
  for (let i = 0; i < count; i++) {
    const response = api.create(managerToken, {
      name: `${prefix} product ${i + 1} ${Date.now()}`,
      description: 'Created by a k6 load test; deleted in teardown.',
      price: 10 + i,
      stockQuantity,
      categoryId: 1 + (i % 6),
    });
    if (!check(response, { 'setup: product created': (r) => r.status === 201 })) {
      fail(`Setup could not create a product: ${response.status} ${response.body}`);
    }
    ids.push(response.json('id'));
  }
  return ids;
}

export function deleteProducts(managerToken, ids) {
  for (const id of ids) {
    api.remove(managerToken, id);
  }
}
