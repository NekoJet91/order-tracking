import { useEffect } from 'react'
import { Link, Route, Routes } from 'react-router-dom'
import { ConnectionIndicator } from '../components/ConnectionIndicator'
import { OrderDetailsPage } from '../features/orders/OrderDetailsPage'
import { OrdersPage } from '../features/orders/OrdersPage'
import { socketConnectRequested, socketDisconnectRequested } from '../realtime/socketActions'
import { useAppDispatch } from './hooks'

export function App() {
  const dispatch = useAppDispatch()

  // One socket for the whole application, opened at the shell rather than per page, so
  // navigating between the list and a detail view does not tear down the connection and
  // pay for a fresh handshake and snapshot each time.
  useEffect(() => {
    dispatch(socketConnectRequested())

    return () => {
      dispatch(socketDisconnectRequested())
    }
  }, [dispatch])

  return (
    <div className="shell">
      <header className="top">
        <Link to="/" className="brand">
          Отслеживание заказов
        </Link>
        <ConnectionIndicator />
      </header>

      <main>
        <Routes>
          <Route path="/" element={<OrdersPage />} />
          <Route path="/orders/:orderNumber" element={<OrderDetailsPage />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
      </main>
    </div>
  )
}

function NotFound() {
  return (
    <div className="card">
      <p className="muted">Такой страницы нет.</p>
      <Link to="/">← К списку заказов</Link>
    </div>
  )
}
