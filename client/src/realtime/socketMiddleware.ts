import type { Dispatch, Middleware, UnknownAction } from '@reduxjs/toolkit'
import type { OrderStatusCounts, OrderSummary } from '../api/types'
import {
  connectionGaveUp,
  connectionLost,
  connectionOpened,
} from '../features/connection/connectionSlice'
import { orderPushed, snapshotReceived } from '../features/orders/ordersSlice'
import { socketConnectRequested, socketDisconnectRequested, socketRetryRequested } from './socketActions'

/** Mirrors the server's `OrderSocketMessages`. Discriminated by `type`, as the wire is. */
type ServerFrame =
  | { type: 'snapshot'; orders: OrderSummary[]; counts: OrderStatusCounts }
  | { type: 'order-changed'; order: OrderSummary; counts: OrderStatusCounts }
  | { type: 'heartbeat'; at: string }

const BASE_DELAY_MS = 500
const MAX_DELAY_MS = 15_000
const MAX_ATTEMPTS = 8

/**
 * How long a silent socket is tolerated.
 *
 * The server heartbeats every twenty seconds, so three missed beats is a connection that
 * has stopped delivering. Anything tighter turns a slow network into a reconnect loop.
 */
const SILENCE_LIMIT_MS = 60_000
const WATCHDOG_INTERVAL_MS = 5_000

function socketUrl(): string {
  const url = new URL('/ws/orders', window.location.href)

  // Derived from the page rather than configured, so the dev proxy, a container behind
  // nginx, and a TLS deployment all work without a build-time switch.
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'

  return url.toString()
}

/**
 * Exponential backoff with jitter.
 */
function backoffDelay(attempt: number): number {
  const ceiling = Math.min(BASE_DELAY_MS * 2 ** (attempt - 1), MAX_DELAY_MS)

  return ceiling / 2 + Math.random() * (ceiling / 2)
}

/**
 * Owns the one `WebSocket` and everything that keeps it alive.
 *
 * Written by hand rather than taken from a library because the behaviour worth showing is
 * here: a snapshot on every open, reconnection that backs off, and a watchdog that notices
 * the difference between a quiet connection and a dead one.
 */
export const socketMiddleware: Middleware<object, unknown, Dispatch<UnknownAction>> = (api) => {
  let socket: WebSocket | null = null
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null
  let watchdog: ReturnType<typeof setInterval> | null = null
  let lastFrameAt = 0
  let attempts = 0
  let wanted = false

  function handleFrame(data: string): void {
    // Any frame, heartbeat included, is proof of life for the watchdog below.
    lastFrameAt = Date.now()

    let frame: ServerFrame

    try {
      frame = JSON.parse(data) as ServerFrame
    } catch {
      // A frame we cannot parse is a bug on one side or the other, but dropping the
      // connection over it would lose every frame after it as well.
      console.warn('Получен кадр, который не удалось разобрать.', data)
      return
    }

    switch (frame.type) {
      case 'snapshot':
        api.dispatch(snapshotReceived({ orders: frame.orders, counts: frame.counts }))
        break
      case 'order-changed':
        api.dispatch(orderPushed({ order: frame.order, counts: frame.counts }))
        break
      case 'heartbeat':
        // Counted above and otherwise ignored: proof of life is the whole payload.
        break
      default:
        // A frame type added by a newer server.
        break
    }
  }

  /**
   * Notices a connection that has stopped delivering.
   *
   * A connection whose far end vanished — a laptop lid, a dropped VPN, an expired NAT
   * mapping — stays `readyState === OPEN` indefinitely, and silence is also what an idle
   * connection looks like. The missing heartbeat is the only thing that tells them apart.
   * This covers waking from sleep too, where the first tick after resume sees a stale
   * timestamp.
   */
  function checkSilence(): void {
    if (socket?.readyState !== WebSocket.OPEN) {
      return
    }

    if (Date.now() - lastFrameAt > SILENCE_LIMIT_MS) {
      console.warn('Соединение молчит дольше допустимого, переподключаемся.')

      // close() triggers onclose, which schedules the reconnect — so there is one path into
      // reconnection however the connection died.
      socket.close(4000, 'silent')
    }
  }

  function scheduleReconnect(): void {
    if (!wanted || reconnectTimer !== null) {
      return
    }

    attempts += 1

    if (attempts > MAX_ATTEMPTS) {
      api.dispatch(connectionGaveUp())
      return
    }

    api.dispatch(connectionLost(attempts))

    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      open()
    }, backoffDelay(attempts))
  }

  function open(): void {
    if (!wanted || socket !== null) {
      return
    }

    const next = new WebSocket(socketUrl())
    socket = next

    next.onopen = () => {
      attempts = 0
      lastFrameAt = Date.now()
      api.dispatch(connectionOpened())
    }

    next.onmessage = (event) => {
      if (typeof event.data === 'string') {
        handleFrame(event.data)
      }
    }

    // onerror is not handled separately: the browser always follows it with onclose, and
    // reacting to both would double the attempt count for a single failure.
    next.onclose = () => {
      if (socket === next) {
        socket = null
        scheduleReconnect()
      }
    }
  }

  function start(): void {
    wanted = true
    attempts = 0
    open()

    watchdog ??= setInterval(checkSilence, WATCHDOG_INTERVAL_MS)

    window.addEventListener('online', onOnline)
  }

  function cancelReconnect(): void {
    if (reconnectTimer !== null) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
  }

  function stop(): void {
    wanted = false
    cancelReconnect()

    if (watchdog !== null) {
      clearInterval(watchdog)
      watchdog = null
    }

    window.removeEventListener('online', onOnline)

    // Detached before closing so onclose cannot schedule a reconnect for a socket that was
    // closed on purpose.
    const closing = socket
    socket = null
    closing?.close(1000, 'client closing')
  }

  /**
   * The browser saying the network is back is better information than a timer, so a pending
   * backoff is cut short rather than waited out.
   */
  function onOnline(): void {
    if (!wanted || socket !== null) {
      return
    }

    cancelReconnect()
    open()
  }

  return (next) => (action) => {
    if (socketConnectRequested.match(action)) {
      start()
    } else if (socketDisconnectRequested.match(action)) {
      stop()
    } else if (socketRetryRequested.match(action)) {
      attempts = 0
      api.dispatch(connectionLost(0))
      onOnline()
    }

    return next(action)
  }
}
