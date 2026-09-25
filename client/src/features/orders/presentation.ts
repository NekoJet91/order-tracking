import { OrderStatus } from '../../api/types'

/**
 * Russian wording for each status.
 *
 * The only place the translation happens: the API deals in English enum names and the user
 * reads Russian. Wording is taken from the specification verbatim.
 */
const labels: Record<OrderStatus, string> = {
  [OrderStatus.Created]: 'создан',
  [OrderStatus.Shipped]: 'отправлен',
  [OrderStatus.Delivered]: 'доставлен',
  [OrderStatus.Cancelled]: 'отменён',
}

/** What to call a status on screen. */
export function statusLabel(status: OrderStatus): string {
  return labels[status]
}

/**
 * Wording for the button that performs a transition.
 *
 * Complete over the enum although no transition currently leads back to `Created`. The client
 * is not supposed to know the transition graph, so adding an edge server-side must not
 * require a change here.
 */
const actions: Record<OrderStatus, string> = {
  [OrderStatus.Created]: 'Вернуть в созданные',
  [OrderStatus.Shipped]: 'Отправить',
  [OrderStatus.Delivered]: 'Доставить',
  [OrderStatus.Cancelled]: 'Отменить',
}

/** What to write on the button that moves an order to this status. */
export function actionLabel(status: OrderStatus): string {
  return actions[status]
}

const formatter = new Intl.DateTimeFormat('ru-RU', {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

/**
 * Formats a server timestamp for display.
 *
 * The API sends UTC with an offset; `Intl` renders it in the viewer's zone, which is what
 * someone reading a delivery time actually wants.
 */
export function formatTimestamp(value: string): string {
  return formatter.format(new Date(value))
}

/**
 * How long ago something happened, in words.
 *
 * Deliberately coarse. A live screen updates by itself, so the useful question is "is this
 * fresh" rather than "exactly when" — and the exact time is one hover away in the title
 * attribute.
 */
export function formatRelative(value: string, now: number): string {
  const seconds = Math.round((now - new Date(value).getTime()) / 1000)

  if (seconds < 10) return 'только что'
  if (seconds < 60) return `${seconds} с назад`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} мин назад`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)} ч назад`

  return `${Math.floor(seconds / 86_400)} дн назад`
}
