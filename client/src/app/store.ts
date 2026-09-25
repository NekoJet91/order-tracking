import { configureStore } from '@reduxjs/toolkit'
import { connectionReducer } from '../features/connection/connectionSlice'
import { ordersReducer } from '../features/orders/ordersSlice'
import { socketMiddleware } from '../realtime/socketMiddleware'

export const store = configureStore({
  reducer: {
    orders: ordersReducer,
    connection: connectionReducer,
  },
  middleware: (getDefaultMiddleware) => getDefaultMiddleware().concat(socketMiddleware),
})

export type RootState = ReturnType<typeof store.getState>
export type AppDispatch = typeof store.dispatch
