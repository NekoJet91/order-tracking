import { useAppDispatch, useAppSelector } from '../app/hooks'
import { selectConnection } from '../features/orders/selectors'
import { socketRetryRequested } from '../realtime/socketActions'

const wording = {
  connecting: 'Подключение…',
  open: 'Обновления в реальном времени',
  reconnecting: 'Переподключение…',
  offline: 'Нет соединения',
} as const

/**
 * Shows whether the page is still live.
 *
 * On a screen that updates by itself, a lost connection looks exactly like nothing having
 * happened — which is the one failure a user cannot detect. Saying so is the whole point of
 * the heartbeat travelling this far up.
 */
export function ConnectionIndicator() {
  const dispatch = useAppDispatch()
  const { status, attempts } = useAppSelector(selectConnection)

  return (
    <div className="connection" data-status={status}>
      <span className="dot" aria-hidden="true" />
      <span>{wording[status]}</span>

      {status === 'reconnecting' && attempts > 1 && <span className="muted">попытка {attempts}</span>}

      {status === 'offline' && (
        <button type="button" className="ghost" onClick={() => dispatch(socketRetryRequested())}>
          Повторить
        </button>
      )}
    </div>
  )
}
