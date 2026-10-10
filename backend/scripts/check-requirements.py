#!/usr/bin/env python3
"""Checks the running system against the assessment PDF, requirement by requirement.

    scripts/check-requirements.py                      # against http://localhost:8080 (the gateway)
    BASE_URL=http://localhost:5080 scripts/check-requirements.py

Every check calls the real API through the gateway, so with several replicas the requests are spread over them.
It creates its own products (named "PDF check ...") and deletes them at the end. Credentials default to the local
demo consumers documented in the README (local development values only).
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

BASE_URL = os.environ.get("BASE_URL", "http://localhost:8080").rstrip("/")
CLIENT_ID = os.environ.get("CHECK_CLIENT_ID", "manager")
CLIENT_SECRET = os.environ.get("CHECK_CLIENT_SECRET", "manager")
RUN = str(int(time.time()))

results = []
created = []


def call(method, path, body=None, token=None, retries=12):
    """One HTTP call; waits and retries when rate limited (429), like a well-behaved client."""
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json", "Cache-Control": "no-cache"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    for attempt in range(retries):
        request = urllib.request.Request(BASE_URL + path, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                text = response.read().decode()
                return response.status, (json.loads(text) if text else None)
        except urllib.error.HTTPError as error:
            text = error.read().decode()
            if error.code == 429 and attempt < retries - 1:
                time.sleep(float(error.headers.get("Retry-After", "1")))
                continue
            return error.code, (json.loads(text) if text else None)
    raise RuntimeError("unreachable")


def check(requirement, description, ok, detail=""):
    results.append((requirement, description, bool(ok), detail))
    mark = "PASS" if ok else "FAIL"
    print(f"  [{mark}] {description}" + (f"  ({detail})" if detail else ""))


def product_body(name, stock=10, price=19.99, category=1):
    return {"name": name, "description": "Created by check-requirements.py", "price": price,
            "stockQuantity": stock, "categoryId": category}


def six_digits(value):
    return isinstance(value, int) and 100000 <= value <= 999999


def main():
    print(f"Checking {BASE_URL} against the assessment PDF\n")

    status, body = call("POST", "/api/auth/token", {"clientId": CLIENT_ID, "clientSecret": CLIENT_SECRET})
    if status != 200:
        print(f"Cannot get a token ({status}): is the cluster running? {body}")
        return 2
    token = body["accessToken"]

    print("CRUD endpoints")
    status, product = call("POST", "/api/products", product_body(f"PDF check lens {RUN}"), token)
    check("POST /api/products", "creates a product (201) with a generated 6-digit id",
          status == 201 and six_digits(product.get("id")), f"id {product.get('id') if product else None}")
    pid = product["id"]
    created.append(pid)

    status, page = call("GET", "/api/products?page=1&pageSize=20", token=token)
    check("GET /api/products", "lists products (200), each with its stock",
          status == 200 and page["items"] and all("stockQuantity" in p for p in page["items"]),
          f"{page.get('totalCount')} products in total")

    status, one = call("GET", f"/api/products/{pid}", token=token)
    check("GET /api/products/{id}", "returns the product (200) with its stock",
          status == 200 and one["id"] == pid and one["stockQuantity"] == 10)

    status, updated = call("PUT", f"/api/products/{pid}", product_body(f"PDF check lens {RUN} v2", stock=12), token)
    check("PUT /api/products/{id}", "updates the product (200)",
          status == 200 and updated["name"].endswith("v2") and updated["stockQuantity"] == 12)

    status, _ = call("DELETE", f"/api/products/{pid}", token=token)
    status_after, _ = call("GET", f"/api/products/{pid}", token=token)
    check("DELETE /api/products/{id}", "deletes the product (204), then it is gone (404)",
          status == 204 and status_after == 404)
    created.remove(pid)

    print("\nAdditional endpoints")
    _, product = call("POST", "/api/products", product_body(f"PDF check Frame Alpha {RUN}", stock=10), token)
    pid = product["id"]
    created.append(pid)

    status, after_add = call("POST", f"/api/products/{pid}/add-to-stock/25", token=token)
    check("add-to-stock/{quantity}", "OK (200) and stock incremented by the quantity: 10 + 25 = 35",
          status == 200 and after_add["stockQuantity"] == 35)

    status, after_remove = call("POST", f"/api/products/{pid}/decrement-stock/7", token=token)
    check("decrement-stock/{quantity}", "OK (200) and stock decremented by the quantity: 35 - 7 = 28",
          status == 200 and after_remove["stockQuantity"] == 28)

    status, problem = call("POST", f"/api/products/{pid}/decrement-stock/1000", token=token)
    _, current = call("GET", f"/api/products/{pid}", token=token)
    check("decrement-stock/{quantity}", "refuses to go below zero (409) and leaves the stock unchanged",
          status == 409 and current["stockQuantity"] == 28, (problem or {}).get("errorCode", ""))

    status, found = call("GET", f"/api/products/search?name=frame%20alpha%20{RUN}", token=token)
    check("search?name={name}", "partial, case-insensitive match finds the product, with its stock",
          status == 200 and any(p["id"] == pid for p in found["items"])
          and all("stockQuantity" in p for p in found["items"]))

    status, in_range = call("GET", "/api/products/stock-level?min=20&max=30&pageSize=100", token=token)
    check("stock-level?min={min}&max={max}", "returns only products within the range, each with its stock",
          status == 200 and all(20 <= p["stockQuantity"] <= 30 for p in in_range["items"])
          and in_range["totalCount"] > 0, f"{in_range.get('totalCount')} products with 20-30 in stock")

    print("\nRequirements")
    def create(i):
        return call("POST", "/api/products", product_body(f"PDF check concurrent {RUN} {i}"), token)
    with ThreadPoolExecutor(max_workers=20) as pool:
        responses = list(pool.map(create, range(40)))
    ids = [body["id"] for status, body in responses if status == 201]
    statuses = sorted({status for status, _ in responses})
    created.extend(ids)
    check("6-digit ids, no duplicates with several instances",
          "40 products created concurrently (spread over the replicas): 40 unique 6-digit ids",
          len(ids) == 40 and len(set(ids)) == 40 and all(six_digits(i) for i in ids),
          f"{len(ids)} created, {len(set(ids))} unique, statuses {statuses}")

    status, problem = call("POST", "/api/products", {"name": "", "price": -1, "stockQuantity": -5, "categoryId": 1}, token)
    fields = sorted((problem or {}).get("errors", {}).keys())
    check("validation on create", "missing/invalid fields are refused (400) and named",
          status == 400 and {"name", "price", "stockquantity"} <= {f.lower() for f in fields},
          ", ".join(fields))

    status, problem = call("PUT", f"/api/products/{pid}", {"name": "x", "price": 0, "stockQuantity": 1, "categoryId": 999}, token)
    check("validation on update", "invalid values are refused (400)", status == 400,
          ", ".join(sorted((problem or {}).get("errors", {}).keys())))

    lists = {
        "GET /api/products": "/api/products?pageSize=5",
        "search": f"/api/products/search?name={RUN}&pageSize=5",
        "stock-level": "/api/products/stock-level?min=0&max=1000000&pageSize=5",
    }
    every = all(
        all("stockQuantity" in p for p in call("GET", path, token=token)[1]["items"]) for path in lists.values())
    check("stock in every list response", "every list endpoint includes each product's stock", every)

    print("\nCleaning up")
    for product_id in list(created):
        call("DELETE", f"/api/products/{product_id}", token=token)
    print(f"  deleted {len(created)} test products")

    failed = [r for r in results if not r[2]]
    print(f"\n{len(results) - len(failed)} of {len(results)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
