/**
 * The four statuses an order moves through.
 *
 * Declared as a const object rather than a TypeScript `enum`: `enum` emits real JavaScript,
 * which `erasableSyntaxOnly` forbids, and a union of string literals is what actually
 * travels on the wire. The names match the server's C# enum exactly — translation to
 * Russian happens at the edge, in `statusLabel`, and nowhere else.
 */
export const OrderStatus = {
  Created: 'Created',
  Shipped: 'Shipped',
  Delivered: 'Delivered',
  Cancelled: 'Cancelled',
} as const

export type OrderStatus = (typeof OrderStatus)[keyof typeof OrderStatus]

/**
 * Longest description the server accepts.
 *
 * Mirrors `Order.DescriptionMaxLength`, from which the validator, the EF mapping and the
 * column width all derive. Nothing enforces the copy; generating these types from the OpenAPI
 * schema would.
 */
export const DESCRIPTION_MAX_LENGTH = 1000

/** An order as the list and the socket present it. */
export interface OrderSummary {
  readonly orderNumber: string
  readonly description: string
  readonly status: OrderStatus
  readonly createdAt: string
  /** Doubles as the version: a frame with an older value must be ignored. */
  readonly updatedAt: string
}

/**
 * How many orders are in each status, across the whole table.
 *
 * Server-computed: a client holds one page, so counting what it has would produce a total
 * that is wrong rather than approximate.
 */
export type OrderStatusCounts = Record<OrderStatus, number>

/** An order with the transitions currently permitted from its status. */
export interface OrderDetails extends OrderSummary {
  readonly allowedNextStatuses: readonly OrderStatus[]
}

/** One page of the keyset-paginated list. */
export interface OrderPage {
  readonly items: readonly OrderSummary[]
  /** Pass back to fetch the next page; `null` means there are none. */
  readonly nextCursor: string | null
}

/**
 * RFC 9457 problem details, as every failing endpoint returns them.
 *
 * The extensions are declared because the UI reads them: a rejected status change carries
 * what the order's status actually is and what it could legally become, which is the
 * difference between "не получилось" and telling the user why.
 *
 * The collections here are mutable where the rest of this file's are not: a problem is held
 * in Redux state, and Immer's draft type cannot accept a `readonly` array.
 */
export interface ProblemDetails {
  readonly title?: string
  readonly detail?: string
  readonly status?: number
  readonly traceId?: string
  readonly errors?: Record<string, string[]>
  readonly orderNumber?: string
  readonly currentStatus?: OrderStatus
  readonly requestedStatus?: OrderStatus
  readonly allowedNextStatuses?: OrderStatus[]
}
