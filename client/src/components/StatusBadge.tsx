import type { OrderStatus } from '../api/types'
import { statusLabel } from '../features/orders/presentation'

/**
 * A status, coloured.
 *
 * The colour is carried by a `data-status` attribute rather than a class per status, so the
 * stylesheet holds the palette and adding a status is a CSS change, not a mapping table in
 * two places.
 */
export function StatusBadge({ status }: { status: OrderStatus }) {
  return (
    <span className="badge" data-status={status}>
      {statusLabel(status)}
    </span>
  )
}
