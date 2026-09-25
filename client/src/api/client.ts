import type { OrderDetails, OrderPage, OrderStatus, ProblemDetails } from './types'

/**
 * An error carrying the server's problem details.
 *
 * Thrown instead of returning a result union because every caller here is a thunk, and a
 * thunk that throws already produces a rejected action with the payload attached. Inventing
 * a second error channel on top of that would mean two ways to fail.
 */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  // Fields rather than constructor parameter properties, which emit code `erasableSyntaxOnly`
  // forbids.
  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Запрос завершился со статусом ${status}.`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }
}

/** Validation messages for one request, flattened to a list. */
export function validationMessages(problem: ProblemDetails): readonly string[] {
  return Object.values(problem.errors ?? {}).flat()
}

async function request<TResponse>(path: string, init?: RequestInit): Promise<TResponse> {
  const response = await fetch(path, {
    ...init,
    headers: init?.body ? { 'Content-Type': 'application/json', ...init.headers } : init?.headers,
  })

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response))
  }

  return (await response.json()) as TResponse
}

/**
 * A failure that is not problem details is still a failure, and the UI needs something to
 * show. A gateway returning HTML, or a socket-level error with no body at all, must not
 * turn into a parse exception that hides the original status.
 */
async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    return (await response.json()) as ProblemDetails
  } catch {
    return { title: `Сервер ответил ${response.status}.`, status: response.status }
  }
}

/** Fetches one page of orders, newest first, optionally limited to one status. */
export function fetchOrders(options?: {
  cursor?: string | null
  status?: OrderStatus | null
}): Promise<OrderPage> {
  const query = new URLSearchParams()

  if (options?.status) {
    query.set('status', options.status)
  }

  if (options?.cursor) {
    query.set('cursor', options.cursor)
  }

  const suffix = query.size > 0 ? `?${query}` : ''

  return request<OrderPage>(`/api/orders${suffix}`)
}

/** Fetches one order with its permitted transitions. */
export function fetchOrder(orderNumber: string): Promise<OrderDetails> {
  return request<OrderDetails>(`/api/orders/${encodeURIComponent(orderNumber)}`)
}

/** Places a new order. */
export function createOrder(description: string): Promise<OrderDetails> {
  return request<OrderDetails>('/api/orders', {
    method: 'POST',
    body: JSON.stringify({ description }),
  })
}

/** Moves an order to a different status. */
export function changeOrderStatus(orderNumber: string, status: OrderStatus): Promise<OrderDetails> {
  return request<OrderDetails>(`/api/orders/${encodeURIComponent(orderNumber)}/status`, {
    method: 'PATCH',
    body: JSON.stringify({ status }),
  })
}
