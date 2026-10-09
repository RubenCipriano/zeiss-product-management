import { PagedResponse } from '../app/shared/models/paged-response';
import { Product } from '../app/features/products/data-access/product.models';

/** Builds a valid product; tests override only what matters to them. */
export function aProduct(overrides: Partial<Product> = {}): Product {
  return {
    id: 100001,
    name: 'Progressive Lens',
    description: 'Premium progressive lens',
    price: 249.9,
    stockQuantity: 12,
    categoryId: 1,
    categoryName: 'Spectacle Lenses',
    createdAt: '2026-10-01T10:00:00Z',
    updatedAt: '2026-10-01T10:00:00Z',
    version: 7,
    ...overrides,
  };
}

export function aPage(
  items: Product[],
  overrides: Partial<PagedResponse<Product>> = {},
): PagedResponse<Product> {
  return {
    items,
    page: 1,
    pageSize: 20,
    totalCount: items.length,
    totalPages: items.length === 0 ? 0 : 1,
    ...overrides,
  };
}
