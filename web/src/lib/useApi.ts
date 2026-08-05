import { useEffect, useState } from 'react'

// Minimal fetch-on-mount hook with loading/error states — enough for the dashboards
// without pulling in a data-fetching library.
export function useApi<T>(fetcher: () => Promise<T>, deps: unknown[] = []): {
  data: T | null; error: string | null; loading: boolean;
} {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let live = true
    setLoading(true); setError(null)
    fetcher()
      .then(d => { if (live) setData(d) })
      .catch(e => { if (live) setError(String(e.message ?? e)) })
      .finally(() => { if (live) setLoading(false) })
    return () => { live = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)

  return { data, error, loading }
}
