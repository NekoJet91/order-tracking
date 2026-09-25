import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '../test/server'
import { ApiError, fetchOrders, validationMessages } from './client'
import { OrderStatus } from './types'

describe('fetchOrders', () => {
  it('sends no query at all when nothing is filtered or paged', async () => {
    let seen: string | undefined

    server.use(
      http.get('/api/orders', ({ request }) => {
        seen = new URL(request.url).search
        return HttpResponse.json({ items: [], nextCursor: null })
      }),
    )

    await fetchOrders()

    expect(seen).toBe('')
  })

  it('passes the status and the cursor through', async () => {
    let seen: URLSearchParams | undefined

    server.use(
      http.get('/api/orders', ({ request }) => {
        seen = new URL(request.url).searchParams
        return HttpResponse.json({ items: [], nextCursor: null })
      }),
    )

    await fetchOrders({ status: OrderStatus.Cancelled, cursor: 'abc' })

    expect(seen?.get('status')).toBe('Cancelled')
    expect(seen?.get('cursor')).toBe('abc')
  })
})

describe('failures', () => {
  it('surfaces the servers problem details', async () => {
    server.use(
      http.get('/api/orders', () =>
        HttpResponse.json(
          { title: 'Неверный курсор.', status: 400, errors: { Cursor: ['Не удалось разобрать.'] } },
          { status: 400 },
        ),
      ),
    )

    const error = await fetchOrders().catch((caught: unknown) => caught)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(400)
    expect(validationMessages((error as ApiError).problem)).toEqual(['Не удалось разобрать.'])
  })

  it('still produces something showable when the body is not problem details', async () => {
    // What a proxy or a gateway returns when it fails on the way to the API. Letting the
    // JSON parse error escape would replace a 502 with a message about unexpected tokens.
    server.use(
      http.get('/api/orders', () =>
        HttpResponse.text('<html>Bad Gateway</html>', { status: 502 }),
      ),
    )

    const error = (await fetchOrders().catch((caught: unknown) => caught)) as ApiError

    expect(error.status).toBe(502)
    expect(error.problem.title).toContain('502')
  })
})
