import { useEffect, useState } from 'react'

/**
 * The current time, re-read on an interval.
 *
 * Relative timestamps ("2 мин назад") are the one thing on the page that goes stale without
 * anything happening. A single ticking value shared by the list is far cheaper than a timer
 * per row, and coarse enough that the interval can stay long.
 */
export function useNow(intervalMs = 15_000): number {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs)

    return () => clearInterval(timer)
  }, [intervalMs])

  return now
}
