import { createSlice, type PayloadAction } from '@reduxjs/toolkit'

/**
 * Where the socket currently stands.
 *
 * `reconnecting` is kept separate from `connecting` on purpose. The first connection being
 * slow is unremarkable; having lost a connection and not yet got it back is worth telling
 * the user about, because it means the screen may be out of date.
 */
export type ConnectionStatus = 'connecting' | 'open' | 'reconnecting' | 'offline'

export interface ConnectionState {
  status: ConnectionStatus
  /** How many times reconnection has been attempted since the last successful open. */
  attempts: number
}

const initialState: ConnectionState = {
  status: 'connecting',
  attempts: 0,
}

/*
 * The time of the last frame is deliberately not in the store. The middleware's watchdog is
 * the only reader, and it keeps that value locally; putting it here would dispatch an action
 * per heartbeat for no component to consume.
 */
const connectionSlice = createSlice({
  name: 'connection',
  initialState,
  reducers: {
    connectionOpened(state) {
      state.status = 'open'
      state.attempts = 0
    },
    connectionLost(state, action: PayloadAction<number>) {
      state.status = 'reconnecting'
      state.attempts = action.payload
    },
    connectionGaveUp(state) {
      state.status = 'offline'
    },
  },
})

export const { connectionOpened, connectionLost, connectionGaveUp } = connectionSlice.actions

export const connectionReducer = connectionSlice.reducer
