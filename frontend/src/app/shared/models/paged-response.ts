/** One page of a collection, as returned by every list endpoint of the API. */
export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export function emptyPage<T>(pageSize: number): PagedResponse<T> {
  return { items: [], page: 1, pageSize, totalCount: 0, totalPages: 0 };
}
