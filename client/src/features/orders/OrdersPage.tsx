import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAppDispatch, useAppSelector } from '../../app/hooks'
import { OrderStatus } from '../../api/types'
import { ProblemMessage } from '../../components/ProblemMessage'
import { StatusBadge } from '../../components/StatusBadge'
import { useNow } from '../../components/useNow'
import { CreateOrderForm } from './CreateOrderForm'
import { loadMoreOrders, loadOrders } from './ordersSlice'
import { formatRelative, formatTimestamp, statusLabel } from './presentation'
import {
  selectFilterLoaded,
  selectListError,
  selectListStatus,
  selectNextCursor,
  selectStatusCounts,
  selectVisibleOrders,
} from './selectors'

const filters = [
  OrderStatus.Created,
  OrderStatus.Shipped,
  OrderStatus.Delivered,
  OrderStatus.Cancelled,
] as const

export function OrdersPage() {
  const dispatch = useAppDispatch()
  const [filter, setFilter] = useState<OrderStatus | null>(null)

  const visible = useAppSelector((state) => selectVisibleOrders(state, filter))
  const nextCursor = useAppSelector((state) => selectNextCursor(state, filter))
  const loaded = useAppSelector((state) => selectFilterLoaded(state, filter))
  const listStatus = useAppSelector(selectListStatus)
  const listError = useAppSelector(selectListError)
  const counts = useAppSelector(selectStatusCounts)
  const now = useNow()

  // Each filter fetches its own first page, because a filtered list has its own pagination —
  // the twenty newest cancelled orders are not a subset of the twenty newest orders. What is
  // displayed still comes from the one collection, so the socket keeps a filtered view live
  // without any of this running again.
  useEffect(() => {
    if (!loaded) {
      void dispatch(loadOrders(filter))
    }
  }, [dispatch, filter, loaded])

  const total = counts && Object.values(counts).reduce((sum, count) => sum + count, 0)

  return (
    <>
      <CreateOrderForm />

      <div className="filters" role="group" aria-label="Фильтр по статусу">
        <button
          type="button"
          className="chip"
          aria-pressed={filter === null}
          onClick={() => setFilter(null)}
        >
          все {total !== null && <span className="count">{total}</span>}
        </button>

        {filters.map((status) => (
          <button
            key={status}
            type="button"
            className="chip"
            data-status={status}
            aria-pressed={filter === status}
            onClick={() => setFilter(filter === status ? null : status)}
          >
            {statusLabel(status)}{' '}
            {counts && <span className="count">{counts[status]}</span>}
          </button>
        ))}
      </div>

      {listError && <ProblemMessage problem={{ title: listError }} />}

      {!loaded && listStatus === 'loading' && <p className="muted">Загружаем…</p>}

      {loaded && visible.length === 0 && (
        <p className="muted">
          {filter ? 'Заказов с таким статусом нет.' : 'Заказов пока нет.'}
        </p>
      )}

      <ul className="orders">
        {visible.map((order) => (
          <li key={order.orderNumber} className="card order-row">
            <div className="order-main">
              <Link to={`/orders/${order.orderNumber}`} className="order-number">
                {order.orderNumber}
              </Link>
              <p className="order-description">{order.description}</p>
            </div>

            <StatusBadge status={order.status} />

            <time className="muted" dateTime={order.updatedAt} title={formatTimestamp(order.updatedAt)}>
              {formatRelative(order.updatedAt, now)}
            </time>
          </li>
        ))}
      </ul>

      {nextCursor && (
        <button
          type="button"
          className="ghost more"
          onClick={() => void dispatch(loadMoreOrders({ status: filter, cursor: nextCursor }))}
        >
          Показать ещё
        </button>
      )}
    </>
  )
}
