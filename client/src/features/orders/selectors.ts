import { createSelector } from '@reduxjs/toolkit'
import type { OrderStatus } from '../../api/types'
import type { RootState } from '../../app/store'
import { filterKey, ordersAdapter } from './ordersSlice'

const adapterSelectors = ordersAdapter.getSelectors<RootState>((state) => state.orders)

/** Every order held, newest first. */
export const selectOrders = adapterSelectors.selectAll

/** One order, or `undefined` if it has not been loaded or pushed yet. */
export const selectOrder = adapterSelectors.selectById

export const selectListStatus = (state: RootState) => state.orders.listStatus
export const selectListError = (state: RootState) => state.orders.listError

/** The cursor for the next page of a given filter, or `null` when there is none. */
export const selectNextCursor = (state: RootState, status: OrderStatus | null) =>
  state.orders.cursors[filterKey(status)] ?? null

/** Whether a filter's first page has been fetched. */
export const selectFilterLoaded = (state: RootState, status: OrderStatus | null) =>
  state.orders.loaded[filterKey(status)] === true

/**
 * The orders to show for a filter.
 *
 * Derived from the one collection rather than from the page that was fetched, which is what
 * keeps a filtered view live: an order the socket moves into this status appears, and one it
 * moves out of disappears, with no refetch.
 */
export const selectVisibleOrders = createSelector(
  [selectOrders, (_: RootState, status: OrderStatus | null) => status],
  (orders, status) => (status ? orders.filter((order) => order.status === status) : orders),
)
export const selectCreating = (state: RootState) => state.orders.creating
export const selectCreateProblem = (state: RootState) => state.orders.createProblem
export const selectChangeProblem = (state: RootState) => state.orders.changeProblem
export const selectConnection = (state: RootState) => state.connection

/** Transitions the server said were permitted, or an empty list if they are not known. */
export const selectAllowedNext = createSelector(
  [(state: RootState) => state.orders.allowedNext, (_: RootState, orderNumber: string) => orderNumber],
  (allowedNext, orderNumber) => allowedNext[orderNumber] ?? [],
)

/** Whether a status change for this order is waiting on the server. */
export const selectChangePending = (state: RootState, orderNumber: string) =>
  state.orders.pendingChanges.includes(orderNumber)

/**
 * How many orders sit in each status, or `null` until the server has said.
 *
 * Read, not computed: the store holds one page, not the table.
 */
export const selectStatusCounts = (state: RootState) => state.orders.counts
