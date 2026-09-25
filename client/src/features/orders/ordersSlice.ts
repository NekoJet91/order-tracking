import {
  createAsyncThunk,
  createEntityAdapter,
  createSlice,
  type PayloadAction,
} from '@reduxjs/toolkit'
import { ApiError, changeOrderStatus, createOrder, fetchOrder, fetchOrders } from '../../api/client'
import type {
  OrderDetails,
  OrderStatus,
  OrderStatusCounts,
  OrderSummary,
  ProblemDetails,
} from '../../api/types'

/**
 * Orders are normalised and kept sorted newest first.
 *
 * The comparer parses rather than comparing the ISO strings. Lexicographic order agrees with
 * chronological order only while every timestamp shares one offset and one fractional-second
 * width, which is an assumption about the server rather than about the format.
 */
const adapter = createEntityAdapter<OrderSummary, string>({
  selectId: (order) => order.orderNumber,
  sortComparer: (left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt),
})

export interface OrdersState {
  /** Transitions permitted from each order's current status, once its details were read. */
  allowedNext: Record<string, readonly OrderStatus[]>
  /**
   * Per-status totals over the whole table, as last reported by the server, or `null` before
   * the first frame. Never derived from the orders held here — those are one page.
   */
  counts: OrderStatusCounts | null
  /**
   * Next cursor per filter, keyed by {@link filterKey}. One cursor would be wrong the moment
   * the filter changes, because a cursor is a position within a particular ordered set.
   */
  cursors: Record<string, string | null>
  /** Which filters have had their first page fetched, so a revisit does not refetch. */
  loaded: Record<string, boolean>
  listStatus: 'idle' | 'loading' | 'ready' | 'error'
  listError: string | null
  creating: boolean
  createProblem: ProblemDetails | null
  /** Order numbers with a status change in flight, so their buttons can be disabled. */
  pendingChanges: string[]
  changeProblem: ProblemDetails | null
}

const initialState = adapter.getInitialState<OrdersState>({
  allowedNext: {},
  counts: null,
  cursors: {},
  loaded: {},
  listStatus: 'idle',
  listError: null,
  creating: false,
  createProblem: null,
  pendingChanges: [],
  changeProblem: null,
})

function problemOf(error: unknown): ProblemDetails {
  return error instanceof ApiError ? error.problem : { title: 'Сеть недоступна.' }
}

/**
 * A thunk around one API call whose rejection payload is the server's problem details.
 *
 * Every call here fails the same way, so the try/catch lives once, and typing `rejectValue`
 * here is what lets the reducers read `action.payload` without a cast.
 */
function apiThunk<Arg, Result>(type: string, call: (arg: Arg) => Promise<Result>) {
  return createAsyncThunk<Result, Arg, { rejectValue: ProblemDetails }>(
    type,
    async (arg, { rejectWithValue }) => {
      try {
        return await call(arg)
      } catch (error) {
        return rejectWithValue(problemOf(error))
      }
    },
  )
}

/**
 * The key a filter's pagination is stored under.
 *
 * `null` means no filter, and needs a name of its own because an object key cannot be null.
 */
export function filterKey(status: OrderStatus | null): string {
  return status ?? 'all'
}

export const loadOrders = apiThunk('orders/load', (status: OrderStatus | null) =>
  fetchOrders({ status }),
)

export const loadMoreOrders = apiThunk(
  'orders/loadMore',
  (args: { status: OrderStatus | null; cursor: string }) => fetchOrders(args),
)

export const loadOrder = apiThunk('orders/loadOne', fetchOrder)

export const submitOrder = apiThunk('orders/create', createOrder)

export const submitStatusChange = apiThunk(
  'orders/changeStatus',
  (args: { orderNumber: string; status: OrderStatus }) =>
    changeOrderStatus(args.orderNumber, args.status),
)

/**
 * Applies an order only when it is newer than the copy already held.
 *
 * This single guard neutralises both sources of duplicate and out-of-order frames: a delta
 * queued before the snapshot was taken, and at-least-once delivery from the broker. Without
 * it, an older frame arriving late would visibly roll a status backwards.
 */
function upsertIfNewer(
  state: typeof initialState,
  incoming: OrderSummary,
  allowedNext?: readonly OrderStatus[],
): void {
  const existing = state.entities[incoming.orderNumber]
  const age = existing ? Date.parse(incoming.updatedAt) - Date.parse(existing.updatedAt) : 1

  if (age < 0) {
    return
  }

  adapter.upsertOne(state, incoming)

  if (allowedNext) {
    state.allowedNext[incoming.orderNumber] = [...allowedNext]
  } else if (age > 0) {
    // The socket does not carry the permitted transitions, and keeping a list from an
    // earlier status would offer the user a button the server is about to reject. Dropping
    // it makes the detail page re-read them, which is the only source that can be trusted.
    // A frame at the same version changes nothing, so the list it came with still holds.
    delete state.allowedNext[incoming.orderNumber]
  }
}

const ordersSlice = createSlice({
  name: 'orders',
  initialState,
  reducers: {
    /** A fresh snapshot from a newly opened socket. */
    snapshotReceived(
      state,
      action: PayloadAction<{ orders: readonly OrderSummary[]; counts: OrderStatusCounts }>,
    ) {
      for (const order of action.payload.orders) {
        upsertIfNewer(state, order)
      }

      state.counts = action.payload.counts
    },
    /**
     * One order changed, or was created, somewhere else.
     *
     * The totals are taken as given even when the order itself is discarded as stale. They
     * describe the whole table at the moment the server sent them, so a later frame's copy
     * is the better answer regardless of what happened to this particular order.
     */
    orderPushed(state, action: PayloadAction<{ order: OrderSummary; counts: OrderStatusCounts }>) {
      upsertIfNewer(state, action.payload.order)
      state.counts = action.payload.counts
    },
    createProblemCleared(state) {
      state.createProblem = null
    },
    changeProblemCleared(state) {
      state.changeProblem = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadOrders.pending, (state) => {
        state.listStatus = 'loading'
        state.listError = null
      })
      .addCase(loadOrders.fulfilled, (state, action) => {
        const key = filterKey(action.meta.arg)

        state.listStatus = 'ready'
        state.cursors[key] = action.payload.nextCursor
        state.loaded[key] = true

        // Merged, not replaced: the store is one collection that every filter contributes to
        // and the socket writes into. Replacing it would throw away both.
        for (const order of action.payload.items) {
          upsertIfNewer(state, order)
        }
      })
      .addCase(loadOrders.rejected, (state, action) => {
        state.listStatus = 'error'
        state.listError = action.payload?.title ?? 'Не удалось загрузить заказы.'
      })
      .addCase(loadMoreOrders.fulfilled, (state, action) => {
        state.cursors[filterKey(action.meta.arg.status)] = action.payload.nextCursor

        for (const order of action.payload.items) {
          upsertIfNewer(state, order)
        }
      })
      .addCase(loadOrder.fulfilled, (state, action) => {
        applyDetails(state, action.payload)
      })
      .addCase(submitOrder.pending, (state) => {
        state.creating = true
        state.createProblem = null
      })
      .addCase(submitOrder.fulfilled, (state, action) => {
        state.creating = false
        applyDetails(state, action.payload)
      })
      .addCase(submitOrder.rejected, (state, action) => {
        state.creating = false
        state.createProblem = action.payload ?? null
      })
      .addCase(submitStatusChange.pending, (state, action) => {
        state.pendingChanges.push(action.meta.arg.orderNumber)
        state.changeProblem = null
      })
      .addCase(submitStatusChange.fulfilled, (state, action) => {
        state.pendingChanges = state.pendingChanges.filter(
          (number) => number !== action.meta.arg.orderNumber,
        )
        applyDetails(state, action.payload)
      })
      .addCase(submitStatusChange.rejected, (state, action) => {
        state.pendingChanges = state.pendingChanges.filter(
          (number) => number !== action.meta.arg.orderNumber,
        )
        state.changeProblem = action.payload ?? null
      })
  },
})

function applyDetails(state: typeof initialState, details: OrderDetails): void {
  const { allowedNextStatuses, ...summary } = details

  upsertIfNewer(state, summary, allowedNextStatuses)
}

export const { snapshotReceived, orderPushed, createProblemCleared, changeProblemCleared } =
  ordersSlice.actions

export const ordersReducer = ordersSlice.reducer
export const ordersAdapter = adapter
