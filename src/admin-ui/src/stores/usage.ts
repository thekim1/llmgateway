import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Paged, PiiIncidentReport, PiiIncidentsQuery, UsageMetrics, UsageMetricsQuery, UsageRequest, UsageRequestsQuery, UsageSummary, UsageSummaryQuery } from '@/api/types'
import { useLoadable } from './loadable'

export const useUsageStore = defineStore('usage', () => {
  const summary = useLoadable<UsageSummary | null>(null)
  const requests = useLoadable<Paged<UsageRequest>>({ items: [], total: 0 })
  const piiIncidents = useLoadable<PiiIncidentReport | null>(null)
  const metrics = useLoadable<UsageMetrics | null>(null)

  const loadSummary = (query: UsageSummaryQuery) => summary.run(() => api.usage.summary(query))
  const loadRequests = (query: UsageRequestsQuery) => requests.run(() => api.usage.requests(query))
  // Filters change the dashboard at once, so an earlier, slower answer must not overwrite a later one.
  let metricsCall = 0
  const loadMetrics = (query: UsageMetricsQuery) => {
    const call = ++metricsCall
    return metrics.run(async () => {
      const result = await api.usage.metrics(query)
      return call === metricsCall ? result : metrics.data.value
    })
  }
  const loadPiiIncidents = (query: PiiIncidentsQuery) => piiIncidents.run(() => api.usage.piiIncidents(query))
  const lookup = (requestId: string) => api.usage.request(requestId)
  const exportCsvUrl = (from?: string, to?: string) => api.usage.exportCsvUrl(from, to)

  return {
    summary: summary.data,
    summaryLoading: summary.loading,
    summaryError: summary.error,
    requests: requests.data,
    requestsLoading: requests.loading,
    requestsError: requests.error,
    piiIncidents: piiIncidents.data,
    piiIncidentsLoading: piiIncidents.loading,
    piiIncidentsError: piiIncidents.error,
    metrics: metrics.data,
    metricsLoading: metrics.loading,
    metricsError: metrics.error,
    loadSummary,
    loadMetrics,
    loadPiiIncidents,
    loadRequests,
    lookup,
    exportCsvUrl,
  }
})
