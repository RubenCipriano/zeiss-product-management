// The product feature's data shapes (as the API sends and receives them) and its business constants.

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
  /**
   * Increases on every change. Sent back in the `If-Match` header when editing or deleting: if someone else changed
   * the product meanwhile, the versions differ and the API answers 412 instead of overwriting their change.
   */
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

export type StockMovementReason = 'InitialStock' | 'StockAdded' | 'StockRemoved' | 'Correction';

/** One entry of a product's stock history. */
export interface StockMovement {
  id: number;
  quantityChange: number;
  stockAfter: number;
  reason: StockMovementReason;
  performedBy: string;
  occurredAt: string;
}

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

/** The stock-level filters behind the summary cards (and their counts). */
export const LOW_STOCK_FILTER: ProductFilter = { kind: 'stock', min: 0, max: LOW_STOCK_THRESHOLD };
export const OUT_OF_STOCK_FILTER: ProductFilter = { kind: 'stock', min: 0, max: 0 };

export const DEFAULT_PAGE_SIZE = 20;

/** Headline numbers for the dashboard cards. */
export interface InventorySummary {
  totalProducts: number;
  lowStock: number;
  outOfStock: number;
}
