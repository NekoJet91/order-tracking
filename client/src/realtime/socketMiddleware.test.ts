import { configureStore } from '@reduxjs/toolkit'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrderStatus } from '../api/types'
import { connectionReducer } from '../features/connection/connectionSlice'
import { ordersReducer } from '../features/orders/ordersSlice'
import { selectOrder, selectStatusCounts } from '../features/orders/selectors'
import { anOrder, counts } from '../test/factories'
import { FakeWebSocket } from '../test/fakeWebSocket'
import { socketConnectRequested, socketDisconnectRequested } from './socketActions'
import { socketMiddleware } from './socketMiddleware'

/**
 * Everything the middleware does between "the connection died" and "the user noticed".
 *
 * Time is faked because the behaviour under test is entirely about delays: how long before
 * the next attempt, how long a silent connection is tolerated, when to stop trying. Waiting
 * those out for real would make the file take a minute and still be timing-dependent.
 */
describe('the socket middleware', () => {
  let uninstall: () => void

  function makeStore() {
    return configureStore({
      reducer: { orders: ordersReducer, connection: connectionReducer },
      middleware: (getDefault) => getDefault().concat(socketMiddleware),
    })
  }

  beforeEach(() => {
    uninstall = FakeWebSocket.install()
    vi.useFakeTimers()

    // The backoff lands at a random point in the upper half of its window. Pinning the
    // source makes the delay a number a test can assert on without knowing the formula.
    vi.spyOn(Math, 'random').mockReturnValue(0)
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
    uninstall()
  })

  it('opens a socket when asked and reports it open', () => {
    const store = makeStore()

    store.dispatch(socketConnectRequested())
    FakeWebSocket.last.open()

    expect(FakeWebSocket.last.url).toContain('/ws/orders')
    expect(store.getState().connection.status).toBe('open')
  })

  it('applies a snapshot and a delta to the store', () => {
    const store = makeStore()

    store.dispatch(socketConnectRequested())
    FakeWebSocket.last.open()

    FakeWebSocket.last.receive({ type: 'snapshot', orders: [anOrder()], counts: counts({ Created: 4 }) })
    FakeWebSocket.last.receive({
      type: 'order-changed',
      order: anOrder({ status: OrderStatus.Shipped, updatedAt: '2026-09-01T10:05:00.000000+00:00' }),
      counts: counts({ Shipped: 4 }),
    })

    expect(selectOrder(store.getState(), 'ORD-00000001')?.status).toBe(OrderStatus.Shipped)
    expect(selectStatusCounts(store.getState())?.Shipped).toBe(4)
  })

  it('survives a frame it cannot parse', () => {
    vi.spyOn(console, 'warn').mockImplementation(() => {})

    const store = makeStore()

    store.dispatch(socketConnectRequested())
    FakeWebSocket.last.open()
    FakeWebSocket.last.onmessage?.({ data: 'not json' })

    // Still one socket, still open. Dropping the connection over one bad frame would lose
    // every good frame after it.
    expect(FakeWebSocket.instances).toHaveLength(1)
    expect(store.getState().connection.status).toBe('open')
  })

  it('reconnects after a drop, with a delay that grows', () => {
    const store = makeStore()

    store.dispatch(socketConnectRequested())
    FakeWebSocket.last.open()
    FakeWebSocket.last.fail()

    expect(store.getState().connection.status).toBe('reconnecting')
    expect(store.getState().connection.attempts).toBe(1)

    // Math.random pinned to 0 puts the first delay at the bottom of its window: half of the
    // 500 ms ceiling. One millisecond short must not be enough.
    vi.advanceTimersByTime(249)
    expect(FakeWebSocket.instances).toHaveLength(1)

    vi.advanceTimersByTime(1)
    expect(FakeWebSocket.instances).toHaveLength(2)

    // Second failure, second doubling: the 1000 ms ceiling, so 500 ms at the bottom.
    FakeWebSocket.last.fail()
    vi.advanceTimersByTime(499)
    expect(FakeWebSocket.instances).toHaveLength(2)

    vi.advanceTimersByTime(1)
    expect(FakeWebSocket.instances).toHaveLength(3)
  })

  it('forgets the failures once a connection succeeds', () => {
    const store = makeStore()

    store.dispatch(socketConnectRequested())
    FakeWebSocket.last.fail()
    vi.advanceTimersByTime(1_000)

    FakeWebSocket.last.open()

    expect(store.getState().connection.attempts).toBe(0)
    expect(store.getState().connection.status).toBe('open')
  })

  it('stops trying, and says so, rather than retrying for ever', () => {
    const store = makeStore()

    store.dispatch(socketConnectRequested())

    // Eight attempts are allowed; the ninth is where it gives up. Each iteration fails the
    // current socket and then runs out the whole backoff window.
    for (let attempt = 0; attempt < 9; attempt += 1) {
      FakeWebSocket.last.fail()
      vi.advanceTimersByTime(20_000)
    }

    expect(store.getState().connection.status).toBe('offline')
  })

  it('closes a connection that has gone silent', () => {
    const store = makeStore()

    store.dispatch(socketConnectRequested())
    FakeWebSocket.last.open()

    const first = FakeWebSocket.last

    // A socket whose far end vanished stays OPEN for ever and reports nothing. Sixty seconds
    // without a frame — three missed heartbeats — is what tells the two apart.
    vi.advanceTimersByTime(59_000)
    expect(first.closeCalls).toHaveLength(0)

    vi.advanceTimersByTime(6_000)
    expect(first.closeCalls[0]?.reason).toBe('silent')
    expect(store.getState().connection.status).toBe('reconnecting')
  })

  it('keeps a quiet but living connection open', () => {
    makeStore().dispatch(socketConnectRequested())
    FakeWebSocket.last.open()

    // A heartbeat every twenty seconds, and nothing else happening. This is the normal state
    // of an idle screen and must not be mistaken for a dead connection.
    for (let beat = 0; beat < 10; beat += 1) {
      vi.advanceTimersByTime(20_000)
      FakeWebSocket.last.receive({ type: 'heartbeat', at: new Date().toISOString() })
    }

    expect(FakeWebSocket.instances).toHaveLength(1)
    expect(FakeWebSocket.last.closeCalls).toHaveLength(0)
  })

  it('does not reconnect after being told to disconnect', () => {
    const store = makeStore()

    store.dispatch(socketConnectRequested())
    FakeWebSocket.last.open()
    store.dispatch(socketDisconnectRequested())

    expect(FakeWebSocket.last.closeCalls[0]?.code).toBe(1000)

    vi.advanceTimersByTime(60_000)

    expect(FakeWebSocket.instances).toHaveLength(1)
  })
})
