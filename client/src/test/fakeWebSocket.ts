function define(value: typeof WebSocket): void {
  Object.defineProperty(globalThis, 'WebSocket', { value, configurable: true, writable: true })
}

/**
 * A `WebSocket` that does nothing until a test tells it to.
 *
 * What is under test is the state machine around the socket — when it reopens, how long it
 * waits, when it gives up — so the socket itself is what gets replaced. Everything the
 * middleware touches is on this object.
 */
export class FakeWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSING = 2
  static readonly CLOSED = 3

  /** Every instance created since the last {@link install}, in order. */
  static instances: FakeWebSocket[] = []

  readyState: number = FakeWebSocket.CONNECTING
  closeCalls: { code?: number; reason?: string }[] = []

  onopen: (() => void) | null = null
  onmessage: ((event: { data: unknown }) => void) | null = null
  onclose: (() => void) | null = null
  onerror: (() => void) | null = null

  readonly url: string

  // A field rather than a constructor parameter property, which `erasableSyntaxOnly` forbids.
  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }

  /** Pretends the handshake completed. */
  open(): void {
    this.readyState = FakeWebSocket.OPEN
    this.onopen?.()
  }

  /** Delivers a frame, serialising it the way the wire would. */
  receive(frame: unknown): void {
    this.onmessage?.({ data: JSON.stringify(frame) })
  }

  /** Pretends the far end went away, without the middleware having asked. */
  fail(): void {
    this.readyState = FakeWebSocket.CLOSED
    this.onclose?.()
  }

  close(code?: number, reason?: string): void {
    this.closeCalls.push({ code, reason })
    this.readyState = FakeWebSocket.CLOSED
    this.onclose?.()
  }

  /**
   * Replaces the global for the duration of a test. Returns the undo.
   *
   * Through `defineProperty` because jsdom installs `WebSocket` as a non-writable property.
   */
  static install(): () => void {
    const original = globalThis.WebSocket

    FakeWebSocket.instances = []
    define(FakeWebSocket as unknown as typeof WebSocket)

    return () => {
      define(original)
      FakeWebSocket.instances = []
    }
  }

  /** The most recently created socket, which is the one the middleware is holding. */
  static get last(): FakeWebSocket {
    const socket = FakeWebSocket.instances.at(-1)

    if (!socket) {
      throw new Error('No socket was opened.')
    }

    return socket
  }
}
