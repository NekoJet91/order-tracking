import { describe, expect, it } from 'vitest'
import { OrderStatus } from '../../api/types'
import { actionLabel, formatRelative, statusLabel } from './presentation'

describe('wording', () => {
  // The `Record<OrderStatus, string>` type already rejects a missing key. What this pins is
  // the wording itself, which the specification fixes and a type cannot.
  it.each([
    [OrderStatus.Created, 'создан'],
    [OrderStatus.Shipped, 'отправлен'],
    [OrderStatus.Delivered, 'доставлен'],
    [OrderStatus.Cancelled, 'отменён'],
  ])('calls %s "%s"', (status, expected) => {
    expect(statusLabel(status)).toBe(expected)
  })

  it('has a button caption for every status, including the one nothing reaches today', () => {
    // Created has no incoming transition on the server, so its caption never renders. The
    // map stays complete on purpose: the client is not supposed to know the transition
    // graph, so adding an edge server-side must not require a frontend change.
    expect(Object.values(OrderStatus).map(actionLabel).every(Boolean)).toBe(true)
  })
})

describe('relative time', () => {
  const at = Date.parse('2026-09-01T10:00:00.000Z')

  it.each([
    [3, 'только что'],
    [42, '42 с назад'],
    [90, '1 мин назад'],
    [7_200, '2 ч назад'],
    [172_800, '2 дн назад'],
  ])('renders %i seconds ago as "%s"', (seconds, expected) => {
    expect(formatRelative(new Date(at).toISOString(), at + seconds * 1000)).toBe(expected)
  })
})
