import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { Provider } from 'react-redux'
import { describe, expect, it } from 'vitest'
import { DESCRIPTION_MAX_LENGTH } from '../../api/types'
import { orderDetails } from '../../test/factories'
import { server } from '../../test/server'
import { makeStore } from '../../test/store'
import { CreateOrderForm } from './CreateOrderForm'
import { selectOrder } from './selectors'

function renderForm() {
  const store = makeStore()

  render(
    <Provider store={store}>
      <CreateOrderForm />
    </Provider>,
  )

  return store
}

describe('the creation form', () => {
  it('refuses to submit nothing', async () => {
    renderForm()

    // No handler is registered, and MSW is set to fail on an unhandled request — so if the
    // button were live, this test would fail on the request rather than on the assertion.
    expect(screen.getByRole('button', { name: 'Создать' })).toBeDisabled()
  })

  it('creates an order and clears the field', async () => {
    server.use(
      http.post('/api/orders', () => HttpResponse.json(orderDetails(), { status: 201 })),
    )

    const store = renderForm()
    const input = screen.getByLabelText('Новый заказ')

    await userEvent.type(input, 'Кабель ВВГнг 3x2.5, 200 м')
    await userEvent.click(screen.getByRole('button', { name: 'Создать' }))

    await waitFor(() => expect(input).toHaveValue(''))

    // The form adds nothing to the list itself; the order is in the store because the POST
    // response went through the same reducer a socket frame would have.
    expect(selectOrder(store.getState(), 'ORD-00000001')).toBeDefined()
  })

  it('warns before sending a description the server would reject', async () => {
    renderForm()

    await userEvent.type(screen.getByLabelText('Новый заказ'), 'x'.repeat(DESCRIPTION_MAX_LENGTH + 1))

    expect(
      screen.getByText(`Описание длиннее ${DESCRIPTION_MAX_LENGTH} символов — сейчас ${DESCRIPTION_MAX_LENGTH + 1}.`),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Создать' })).toBeDisabled()
  })

  it('shows what the server said when it refuses', async () => {
    server.use(
      http.post('/api/orders', () =>
        HttpResponse.json(
          { title: 'Запрос не прошёл валидацию.', errors: { Description: ['Описание обязательно.'] } },
          { status: 400 },
        ),
      ),
    )

    renderForm()

    await userEvent.type(screen.getByLabelText('Новый заказ'), 'что-то')
    await userEvent.click(screen.getByRole('button', { name: 'Создать' }))

    expect(await screen.findByText('Описание обязательно.')).toBeInTheDocument()

    // The text stays, so the user can correct it rather than retype it.
    expect(screen.getByLabelText('Новый заказ')).toHaveValue('что-то')
  })
})
