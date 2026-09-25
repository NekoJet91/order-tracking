import { describe, expect, it } from 'vitest'
import { OrderStatus } from '../../api/types'
import { anOrder, counts, orderDetails } from '../../test/factories'
import { makeStore } from '../../test/store'
import { loadOrders, orderPushed, snapshotReceived, submitOrder } from './ordersSlice'
import { selectAllowedNext, selectNextCursor, selectOrder, selectStatusCounts } from './selectors'

/**
 * The version guard, which is the one thing in the client that has to be right.
 *
 * Four independent routes deliver an order — a REST page, the socket snapshot, a socket
 * delta, and the reply to the user's own POST or PATCH — and all four go through
 * `upsertIfNewer`. If it is wrong, a late frame rolls a status backwards on screen and
 * nothing anywhere else can catch it.
 */
describe('the version guard', () => {
  const early = '2026-09-01T10:00:00.000000+00:00'
  const late = '2026-09-01T10:05:00.000000+00:00'

  it('accepts a newer copy of an order it already holds', () => {
    const store = makeStore()

    store.dispatch(snapshotReceived({ orders: [anOrder({ updatedAt: early })], counts: counts() }))
    store.dispatch(
      orderPushed({
        order: anOrder({ status: OrderStatus.Shipped, updatedAt: late }),
        counts: counts({ Shipped: 1 }),
      }),
    )

    expect(selectOrder(store.getState(), 'ORD-00000001')?.status).toBe(OrderStatus.Shipped)
  })

  it('ignores a frame that is older than what it holds', () => {
    const store = makeStore()

    store.dispatch(
      snapshotReceived({
        orders: [anOrder({ status: OrderStatus.Shipped, updatedAt: late })],
        counts: counts({ Shipped: 1 }),
      }),
    )

    // A delta queued before the snapshot was taken, arriving after it. At-least-once
    // delivery makes this ordinary rather than exotic.
    store.dispatch(
      orderPushed({
        order: anOrder({ status: OrderStatus.Created, updatedAt: early }),
        counts: counts({ Shipped: 1 }),
      }),
    )

    expect(selectOrder(store.getState(), 'ORD-00000001')?.status).toBe(OrderStatus.Shipped)
  })

  it('adds an order it has never seen', () => {
    const store = makeStore()

    store.dispatch(orderPushed({ order: anOrder({ orderNumber: 'ORD-00000042' }), counts: counts() }))

    expect(selectOrder(store.getState(), 'ORD-00000042')).toBeDefined()
  })

  it('does not duplicate an order delivered twice', () => {
    const store = makeStore()
    const order = anOrder()

    store.dispatch(snapshotReceived({ orders: [order], counts: counts() }))
    store.dispatch(orderPushed({ order, counts: counts() }))

    expect(store.getState().orders.ids).toEqual(['ORD-00000001'])
  })

  it('drops the permitted transitions when the status moves on', () => {
    const store = makeStore()

    // Details carry the transitions; a socket frame does not. Keeping the old list would
    // offer a button the server is about to reject.
    store.dispatch({
      type: submitOrder.fulfilled.type,
      payload: orderDetails({ updatedAt: early }),
      meta: { arg: '', requestId: '', requestStatus: 'fulfilled' },
    })
    expect(selectAllowedNext(store.getState(), 'ORD-00000001')).toEqual([
      OrderStatus.Shipped,
      OrderStatus.Cancelled,
    ])

    store.dispatch(
      orderPushed({
        order: anOrder({ status: OrderStatus.Shipped, updatedAt: late }),
        counts: counts({ Shipped: 1 }),
      }),
    )

    expect(selectAllowedNext(store.getState(), 'ORD-00000001')).toEqual([])
  })

  it('keeps the permitted transitions when a frame repeats the same version', () => {
    const store = makeStore()

    store.dispatch({
      type: submitOrder.fulfilled.type,
      payload: orderDetails({ updatedAt: early }),
      meta: { arg: '', requestId: '', requestStatus: 'fulfilled' },
    })

    // The socket echo of the very change the POST just returned. It is not newer, so it
    // says nothing new about the transitions either — and discarding them here would blank
    // the detail page's buttons for the length of a round trip.
    store.dispatch(orderPushed({ order: anOrder({ updatedAt: early }), counts: counts() }))

    expect(selectAllowedNext(store.getState(), 'ORD-00000001')).toHaveLength(2)
  })
})

describe('status totals', () => {
  it('are taken from the server, not counted locally', () => {
    const store = makeStore()

    // One order in the store, a hundred behind it. Counting what is held would report 1.
    store.dispatch(
      snapshotReceived({ orders: [anOrder()], counts: counts({ Created: 100, Delivered: 7 }) }),
    )

    expect(selectStatusCounts(store.getState())).toEqual(counts({ Created: 100, Delivered: 7 }))
  })

  it('are still applied when the order in the same frame is stale', () => {
    const store = makeStore()
    const late = '2026-09-01T10:05:00.000000+00:00'

    store.dispatch(snapshotReceived({ orders: [anOrder({ updatedAt: late })], counts: counts() }))
    store.dispatch(
      orderPushed({
        order: anOrder({ updatedAt: '2026-09-01T10:00:00.000000+00:00' }),
        counts: counts({ Shipped: 3 }),
      }),
    )

    // The totals describe the whole table at the moment the server sent them, which is later
    // information regardless of what happened to this one order.
    expect(selectStatusCounts(store.getState())?.Shipped).toBe(3)
  })
})

describe('pagination cursors', () => {
  function pageLoaded(status: OrderStatus | null, nextCursor: string | null) {
    return {
      type: loadOrders.fulfilled.type,
      payload: { items: [], nextCursor },
      meta: { arg: status, requestId: '', requestStatus: 'fulfilled' },
    }
  }

  it('are kept per filter, because a cursor is a position in one ordered set', () => {
    const store = makeStore()

    store.dispatch(pageLoaded(null, 'cursor-for-all'))
    store.dispatch(pageLoaded(OrderStatus.Cancelled, 'cursor-for-cancelled'))

    expect(selectNextCursor(store.getState(), null)).toBe('cursor-for-all')
    expect(selectNextCursor(store.getState(), OrderStatus.Cancelled)).toBe('cursor-for-cancelled')
  })

  it('report no cursor for a filter that has never been fetched', () => {
    const store = makeStore()

    expect(selectNextCursor(store.getState(), OrderStatus.Delivered)).toBeNull()
  })
})
