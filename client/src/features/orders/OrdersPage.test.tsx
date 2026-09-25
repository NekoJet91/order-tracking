import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { Provider } from 'react-redux'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { OrderStatus } from '../../api/types'
import { anOrder, counts } from '../../test/factories'
import { server } from '../../test/server'
import { makeStore, type TestStore } from '../../test/store'
import { OrdersPage } from './OrdersPage'
import { orderPushed } from './ordersSlice'

function renderPage(): TestStore {
  const store = makeStore()

  render(
    <Provider store={store}>
      <MemoryRouter>
        <OrdersPage />
      </MemoryRouter>
    </Provider>,
  )

  return store
}

function chip(name: string) {
  return screen.getByRole('button', { name: new RegExp(`^${name}`) })
}

describe('the orders list', () => {
  it('shows the totals the server reported, not the size of the page it holds', async () => {
    // Twenty in hand, a hundred and thirty behind them. A client counting what it holds
    // would put 20 on the "все" chip and be confidently wrong.
    server.use(
      http.get('/api/orders', () =>
        HttpResponse.json({ items: [anOrder()], nextCursor: 'next-page' }),
      ),
    )

    const store = renderPage()

    store.dispatch(
      orderPushed({ order: anOrder(), counts: counts({ Created: 100, Cancelled: 30 }) }),
    )

    expect(await screen.findByRole('button', { name: 'все 130' })).toBeInTheDocument()
    expect(chip('отменён')).toHaveTextContent('отменён 30')
  })

  it('asks the server for a filtered page of its own', async () => {
    const requested: (string | null)[] = []

    server.use(
      http.get('/api/orders', ({ request }) => {
        requested.push(new URL(request.url).searchParams.get('status'))
        return HttpResponse.json({ items: [], nextCursor: null })
      }),
    )

    renderPage()
    await waitFor(() => expect(requested).toEqual([null]))

    await userEvent.click(chip('отменён'))

    // A separate request, because the twenty newest cancelled orders are not a subset of
    // the twenty newest orders — filtering the page in hand would show fewer than exist.
    await waitFor(() => expect(requested).toEqual([null, 'Cancelled']))
  })

  it('does not refetch a filter it has already loaded', async () => {
    let calls = 0

    server.use(
      http.get('/api/orders', () => {
        calls += 1
        return HttpResponse.json({ items: [], nextCursor: null })
      }),
    )

    renderPage()
    await waitFor(() => expect(calls).toBe(1))

    await userEvent.click(chip('отменён'))
    await waitFor(() => expect(calls).toBe(2))

    await userEvent.click(chip('все'))
    await userEvent.click(chip('отменён'))

    expect(calls).toBe(2)
  })

  it('keeps a filtered view live when the socket moves an order into it', async () => {
    server.use(http.get('/api/orders', () => HttpResponse.json({ items: [], nextCursor: null })))

    const store = renderPage()

    await userEvent.click(chip('отменён'))
    expect(await screen.findByText('Заказов с таким статусом нет.')).toBeInTheDocument()

    // No refetch anywhere: the list renders from the one collection, so an order the socket
    // moves into this status simply appears.
    store.dispatch(
      orderPushed({
        order: anOrder({ orderNumber: 'ORD-00000077', status: OrderStatus.Cancelled }),
        counts: counts({ Cancelled: 1 }),
      }),
    )

    const list = await screen.findByRole('list')
    expect(within(list).getByText('ORD-00000077')).toBeInTheDocument()
  })

  it('offers more only while the server says there is more', async () => {
    server.use(
      http.get('/api/orders', ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor')

        return cursor === null
          ? HttpResponse.json({ items: [anOrder()], nextCursor: 'page-two' })
          : HttpResponse.json({
              items: [anOrder({ orderNumber: 'ORD-00000002' })],
              nextCursor: null,
            })
      }),
    )

    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Показать ещё' }))

    expect(await screen.findByText('ORD-00000002')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).not.toBeInTheDocument()
  })

  it('says what went wrong rather than showing an empty list', async () => {
    server.use(
      http.get('/api/orders', () =>
        HttpResponse.json({ title: 'База данных недоступна.' }, { status: 503 }),
      ),
    )

    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent('База данных недоступна.')
  })
})
