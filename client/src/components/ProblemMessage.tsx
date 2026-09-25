import { validationMessages } from '../api/client'
import type { ProblemDetails } from '../api/types'
import { statusLabel } from '../features/orders/presentation'

/**
 * Renders what the server said went wrong.
 *
 * The server's problem details already carry a human-readable title, the field-level
 * validation messages, and — for a rejected transition — which statuses would have been
 * accepted. Showing them beats a generic "ошибка", and it is the reason those extensions
 * exist on the API side at all.
 */
export function ProblemMessage({ problem, onDismiss }: { problem: ProblemDetails; onDismiss?: () => void }) {
  const messages = validationMessages(problem)
  const allowed = problem.allowedNextStatuses ?? []

  return (
    <div className="problem" role="alert">
      <div className="problem-body">
        <strong>{problem.title ?? 'Не удалось выполнить запрос.'}</strong>
        {problem.detail && <p>{problem.detail}</p>}

        {messages.length > 0 && (
          <ul>
            {messages.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        )}

        {allowed.length > 0 && (
          <p>Допустимые статусы: {allowed.map(statusLabel).join(', ')}.</p>
        )}
      </div>

      {onDismiss && (
        <button type="button" className="ghost" onClick={onDismiss} aria-label="Скрыть сообщение">
          ×
        </button>
      )}
    </div>
  )
}
