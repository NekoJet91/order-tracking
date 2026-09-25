import { configureStore } from '@reduxjs/toolkit'
import { connectionReducer } from '../features/connection/connectionSlice'
import { ordersReducer } from '../features/orders/ordersSlice'

/**
 * A store shaped like the application's, minus the socket middleware.
 *
 * The real store is a module-level singleton and would leak state between tests. The socket
 * tests build their own store with the middleware attached.
 */
export function makeStore() {
  return configureStore({
    reducer: {
      orders: ordersReducer,
      connection: connectionReducer,
    },
  })
}

export type TestStore = ReturnType<typeof makeStore>
