import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { Provider } from 'react-redux'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { OrderStatus } from '../../api/types'
import { anOrder, counts, orderDetails } from '../../test/factories'
import { server } from '../../test/server'
import { makeStore, type TestStore } from '../../test/store'
import { OrderDetailsPage } from './OrderDetailsPage'
import { orderPushed } from './ordersSlice'

function renderPage(): TestStore {
  const store = makeStore()

  render(
    <Provider store={store}>
      <MemoryRouter initialEntries={['/orders/ORD-00000001']}>
        <Routes>
          <Route path="/orders/:orderNumber" element={<OrderDetailsPage />} />
        </Routes>
      </MemoryRouter>
    </Provider>,
  )

  return store
}

describe('the details page', () => {
  it('offers exactly the transitions the server permits', async () => {
    server.use(
      http.get('/api/orders/ORD-00000001', () =>
        HttpResponse.json(orderDetails({ allowedNextStatuses: [OrderStatus.Shipped] })),
      ),
    )

    renderPage()

    // One button, not two. The transition rules live in the domain model and are published
    // per order; reimplementing them in TypeScript would mean two places to keep in step.
    expect(await screen.findByRole('button', { name: 'Отправить' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Отменить' })).not.toBeInTheDocument()
  })

  it('re-reads the transitions after the socket advances the status', async () => {
    let served = 0

    server.use(
      http.get('/api/orders/ORD-00000001', () => {
        served += 1
        return HttpResponse.json(
          served === 1
            ? orderDetails({ allowedNextStatuses: [OrderStatus.Shipped, OrderStatus.Cancelled] })
            : orderDetails({
                status: OrderStatus.Shipped,
                updatedAt: '2026-09-01T10:05:00.000000+00:00',
                allowedNextStatuses: [OrderStatus.Delivered],
              }),
        )
      }),
    )

    const store = renderPage()
    await screen.findByRole('button', { name: 'Отправить' })

    // A frame carries the new status but not what may follow it, so the reducer drops the
    // stale list and the page fetches the real one rather than showing a button the server
    // is about to reject.
    store.dispatch(
      orderPushed({
        order: anOrder({
          status: OrderStatus.Shipped,
          updatedAt: '2026-09-01T10:05:00.000000+00:00',
        }),
        counts: counts({ Shipped: 1 }),
      }),
    )

    expect(await screen.findByRole('button', { name: 'Доставить' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Отправить' })).not.toBeInTheDocument()
  })

  it('explains a rejected transition with what would have been accepted', async () => {
    server.use(
      http.get('/api/orders/ORD-00000001', () =>
        HttpResponse.json(orderDetails({ allowedNextStatuses: [OrderStatus.Shipped] })),
      ),
      http.patch('/api/orders/ORD-00000001/status', () =>
        HttpResponse.json(
          {
            title: 'Переход между статусами недопустим.',
            currentStatus: OrderStatus.Delivered,
            allowedNextStatuses: [],
          },
          { status: 409 },
        ),
      ),
    )

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Отправить' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Переход между статусами недопустим.',
    )
  })

  it('does not call an order finished before its transitions have been read', async () => {
    let release: () => void = () => {}
    const held = new Promise<void>((resolve) => {
      release = resolve
    })

    server.use(
      http.get('/api/orders/ORD-00000001', async () => {
        await held
        return HttpResponse.json(orderDetails({ allowedNextStatuses: [OrderStatus.Shipped] }))
      }),
    )

    // The order is already in the store from the list, so the page renders it at once —
    // but nothing is known yet about what it may become.
    const store = renderPage()
    act(() => {
      store.dispatch(orderPushed({ order: anOrder(), counts: counts({ Created: 1 }) }))
    })

    expect(await screen.findByRole('heading', { name: 'ORD-00000001' })).toBeInTheDocument()
    expect(screen.queryByText(/в конечном статусе/)).not.toBeInTheDocument()

    release()

    expect(await screen.findByRole('button', { name: 'Отправить' })).toBeInTheDocument()
  })

  it('says a finished order is finished instead of showing no buttons', async () => {
    server.use(
      http.get('/api/orders/ORD-00000001', () =>
        HttpResponse.json(orderDetails({ status: OrderStatus.Delivered, allowedNextStatuses: [] })),
      ),
    )

    renderPage()

    await waitFor(() =>
      expect(screen.getByText('Заказ в конечном статусе, менять его больше нельзя.')).toBeInTheDocument(),
    )
  })
})
