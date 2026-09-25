import { createAction } from '@reduxjs/toolkit'

/**
 * Commands for the socket middleware.
 *
 * Plain actions rather than an imperative handle, so that opening and closing the
 * connection is something components request the same way they request anything else, and
 * the only code holding a `WebSocket` is the middleware.
 */
export const socketConnectRequested = createAction('socket/connect')
export const socketDisconnectRequested = createAction('socket/disconnect')

/** Asks for an immediate attempt after the middleware has given up. */
export const socketRetryRequested = createAction('socket/retry')
