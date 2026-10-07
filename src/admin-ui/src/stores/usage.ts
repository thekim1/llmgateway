import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Paged, UsageRequest, UsageRequestsQuery, UsageSummary, UsageSummaryQuery } from '@/api/types'
import { useLoadable } from './loadable'

export const useUsageStore = defineStore('usage', () => {
  const summary = useLoadable<UsageSummary | null>(null)
  const requests = useLoadable<Paged<UsageRequest>>({ items: [], total: 0 })

  const loadSummary = (query: UsageSummaryQuery) => summary.run(() => api.usage.summary(query))
  const loadRequests = (query: UsageRequestsQuery) => requests.run(() => api.usage.requests(query))
  const lookup = (requestId: string) => api.usage.request(requestId)
  const exportCsvUrl = (from?: string, to?: string) => api.usage.exportCsvUrl(from, to)

  return {
    summary: summary.data,
    summaryLoading: summary.loading,
    summaryError: summary.error,
    requests: requests.data,
    requestsLoading: requests.loading,
    requestsError: requests.error,
    loadSummary,
    loadRequests,
    lookup,
    exportCsvUrl,
  }
})
