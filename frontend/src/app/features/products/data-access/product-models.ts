/** A product as returned by the API. Mirrors `ProductResponse` in the backend. */
export interface Product {
  id: number;
  name: string;
  description: string | null;
  price: number;
  stockQuantity: number;
  categoryId: number;
  categoryName: string;
  createdAt: string;
  updatedAt: string;
  /** Concurrency token; sent back as `If-Match` so stale edits are rejected (HTTP 412). */
  version: number;
}

/** Body of create (POST) and update (PUT) requests. */
export interface ProductInput {
  name: string;
  description: string | null;
  price: number;
  stockQuantity: number;
  categoryId: number;
}

export interface Category {
  id: number;
  name: string;
}

/** Which of the API's list endpoints to use. They cannot be combined, so the filter is a union. */
export type ProductFilter =
  | { kind: 'all' }
  | { kind: 'name'; name: string }
  | { kind: 'stock'; min: number | null; max: number | null };

export type ProductFilterKind = ProductFilter['kind'];

export interface ProductQuery {
  filter: ProductFilter;
  /** 1-based, as in the API. */
  page: number;
  pageSize: number;
}

export type StockOperation = 'remove' | 'add';

/** Business limits, kept in line with `ProductRules` in the backend (the API re-validates anyway). */
export const PRODUCT_LIMITS = {
  nameMaxLength: 200,
  descriptionMaxLength: 2000,
  minPrice: 0.01,
  maxPrice: 1_000_000,
  maxStock: 1_000_000,
} as const;

/** At or below this quantity a product is highlighted as running low. */
export const LOW_STOCK_THRESHOLD = 10;

export const DEFAULT_PAGE_SIZE = 20;

/** Headline numbers for the dashboard cards. */
export interface InventorySummary {
  totalProducts: number;
  lowStock: number;
  outOfStock: number;
}
