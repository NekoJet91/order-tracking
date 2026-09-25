import { OrderStatus, type OrderDetails, type OrderStatusCounts, type OrderSummary } from '../api/types'

/**
 * Builders for the shapes the API returns, so a test names only the field it is about —
 * usually `updatedAt`, the version the reducer arbitrates on.
 */
export function anOrder(overrides: Partial<OrderSummary> = {}): OrderSummary {
  return {
    orderNumber: 'ORD-00000001',
    description: 'Кабель ВВГнг 3x2.5, 200 м',
    status: OrderStatus.Created,
    createdAt: '2026-09-01T10:00:00.000000+00:00',
    updatedAt: '2026-09-01T10:00:00.000000+00:00',
    ...overrides,
  }
}

export function orderDetails(overrides: Partial<OrderDetails> = {}): OrderDetails {
  return {
    ...anOrder(),
    allowedNextStatuses: [OrderStatus.Shipped, OrderStatus.Cancelled],
    ...overrides,
  }
}

/** Counts that are complete over the status enum, as the server's always are. */
export function counts(overrides: Partial<OrderStatusCounts> = {}): OrderStatusCounts {
  return { Created: 0, Shipped: 0, Delivered: 0, Cancelled: 0, ...overrides }
}
