import { Pipe, PipeTransform } from '@angular/core';
import { StockMovementReason } from '../../data-access/product-models';

const REASONS: Record<StockMovementReason, { label: string; badge: string }> = {
  InitialStock: { label: 'Initial stock', badge: 'bg-slate-100 text-slate-700' },
  StockAdded: { label: 'Added', badge: 'bg-emerald-50 text-emerald-700' },
  StockRemoved: { label: 'Removed', badge: 'bg-red-50 text-red-700' },
  Correction: { label: 'Correction', badge: 'bg-amber-50 text-amber-800' },
};

/** A stock movement's reason for people: `reason | movementReason` → label, `reason | movementReason: 'badge'` → classes. */
@Pipe({ name: 'movementReason' })
export class MovementReasonPipe implements PipeTransform {
  transform(reason: StockMovementReason, part: 'label' | 'badge' = 'label'): string {
    return REASONS[reason]?.[part] ?? reason;
  }
}
