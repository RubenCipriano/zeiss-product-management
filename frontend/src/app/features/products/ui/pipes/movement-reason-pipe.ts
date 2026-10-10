// A template pipe that turns a stock movement's reason code into words and a badge colour.
import { Pipe, PipeTransform } from '@angular/core';
import { StockMovementReason } from '../../data-access/product-models';

const REASONS: Record<StockMovementReason, { label: string; badge: string }> = {
  InitialStock: { label: 'Initial stock', badge: 'bg-slate-100 text-slate-700' },
  StockAdded: { label: 'Added', badge: 'bg-emerald-50 text-emerald-700' },
  StockRemoved: { label: 'Removed', badge: 'bg-red-50 text-red-700' },
  Correction: { label: 'Correction', badge: 'bg-amber-50 text-amber-800' },
};

/**
 * A pipe is a small function used in templates with `|` to format a value. Here: `reason | movementReason` → label
 * ("Added"), `reason | movementReason: 'badge'` → the badge's CSS classes.
 */
@Pipe({ name: 'movementReason' })
export class MovementReasonPipe implements PipeTransform {
  transform(reason: StockMovementReason, part: 'label' | 'badge' = 'label'): string {
    return REASONS[reason]?.[part] ?? reason;
  }
}
