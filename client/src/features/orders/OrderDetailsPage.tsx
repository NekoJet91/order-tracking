import { useEffect } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAppDispatch, useAppSelector } from '../../app/hooks'
import { ProblemMessage } from '../../components/ProblemMessage'
import { StatusBadge } from '../../components/StatusBadge'
import { changeProblemCleared, loadOrder, submitStatusChange } from './ordersSlice'
import { actionLabel, formatTimestamp } from './presentation'
import {
  selectAllowedNext,
  selectChangePending,
  selectChangeProblem,
  selectOrder,
} from './selectors'

export function OrderDetailsPage() {
  const dispatch = useAppDispatch()
  const { orderNumber = '' } = useParams<{ orderNumber: string }>()

  const order = useAppSelector((state) => selectOrder(state, orderNumber))
  const allowedNext = useAppSelector((state) => selectAllowedNext(state, orderNumber))
  const pending = useAppSelector((state) => selectChangePending(state, orderNumber))
  const problem = useAppSelector(selectChangeProblem)

  // Re-reads whenever the permitted transitions are unknown, which is both the first visit
  // and the moment after a socket frame advanced the status — the frame carries the new
  // status but not what may follow it, so the reducer drops the list and this puts it back.
  const missingTransitions = allowedNext === undefined

  useEffect(() => {
    if (orderNumber && missingTransitions) {
      void dispatch(loadOrder(orderNumber))
    }
  }, [dispatch, orderNumber, missingTransitions])

  if (!order) {
    return (
      <div className="card">
        <p className="muted">Загружаем заказ {orderNumber}…</p>
        <Link to="/">← К списку</Link>
      </div>
    )
  }

  return (
    <>
      <Link to="/" className="back">
        ← К списку
      </Link>

      <article className="card details">
        <header>
          <h2>{order.orderNumber}</h2>
          <StatusBadge status={order.status} />
        </header>

        <p className="order-description">{order.description}</p>

        <dl>
          <div>
            <dt>Создан</dt>
            <dd>{formatTimestamp(order.createdAt)}</dd>
          </div>
          <div>
            <dt>Обновлён</dt>
            <dd>{formatTimestamp(order.updatedAt)}</dd>
          </div>
        </dl>

        {/* Nothing is said about the transitions until the server has answered: the order
            itself may already be in the store from the list, but whether it can still move
            is only known once its details have been read. */}
        {allowedNext && allowedNext.length > 0 && (
          <div className="actions">
            {allowedNext.map((status) => (
              <button
                key={status}
                type="button"
                data-status={status}
                disabled={pending}
                onClick={() => void dispatch(submitStatusChange({ orderNumber, status }))}
              >
                {actionLabel(status)}
              </button>
            ))}
          </div>
        )}

        {allowedNext && allowedNext.length === 0 && (
          <p className="muted">Заказ в конечном статусе, менять его больше нельзя.</p>
        )}

        {problem && (
          <ProblemMessage problem={problem} onDismiss={() => dispatch(changeProblemCleared())} />
        )}
      </article>
    </>
  )
}
