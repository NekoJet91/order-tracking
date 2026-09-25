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
  /** Epoch milliseconds of the last frame of any kind, or `null` before the first. */
  lastFrameAt: number | null
}

const initialState: ConnectionState = {
  status: 'connecting',
  attempts: 0,
  lastFrameAt: null,
}

const connectionSlice = createSlice({
  name: 'connection',
  initialState,
  reducers: {
    connectionOpened(state, action: PayloadAction<number>) {
      state.status = 'open'
      state.attempts = 0
      state.lastFrameAt = action.payload
    },
    connectionLost(state, action: PayloadAction<number>) {
      state.status = 'reconnecting'
      state.attempts = action.payload
    },
    connectionGaveUp(state) {
      state.status = 'offline'
    },
    frameReceived(state, action: PayloadAction<number>) {
      state.lastFrameAt = action.payload
    },
  },
})

export const { connectionOpened, connectionLost, connectionGaveUp, frameReceived } =
  connectionSlice.actions

export const connectionReducer = connectionSlice.reducer
