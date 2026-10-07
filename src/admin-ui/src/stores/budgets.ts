import { ref } from 'vue'
import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Budget, BudgetAlert, BudgetFilter, BudgetRequest, ExchangeRate } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

export const useBudgetsStore = defineStore('budgets', () => {
  const { data: items, loading, error, loaded, run } = useLoadable<Budget[]>([])
  const alerts = useLoadable<BudgetAlert[]>([])
  const exchangeRate = ref<ExchangeRate | null>(null)
  const exchangeRateError = ref<unknown>(null)

  const load = (filter: BudgetFilter = {}) => run(() => api.budgets.list(filter))

  async function create(request: BudgetRequest): Promise<Budget> {
    const created = await api.budgets.create(request)
    items.value = replaceById(items.value, created)
    return created
  }

  async function update(id: string, request: BudgetRequest): Promise<Budget> {
    const updated = await api.budgets.update(id, request)
    items.value = replaceById(items.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await api.budgets.remove(id)
    items.value = removeById(items.value, id)
  }

  const loadAlerts = (acknowledged?: boolean) => alerts.run(() => api.alerts.list(acknowledged))

  async function acknowledge(id: string): Promise<void> {
    await api.alerts.acknowledge(id)
    alerts.data.value = alerts.data.value.map((a) => (a.id === id ? { ...a, acknowledged: true } : a))
  }

  async function loadExchangeRate(): Promise<ExchangeRate | null> {
    exchangeRateError.value = null
    try {
      exchangeRate.value = await api.settings.exchangeRate()
    } catch (e) {
      exchangeRateError.value = e
    }
    return exchangeRate.value
  }

  async function updateExchangeRate(sekPerUnit: number): Promise<ExchangeRate> {
    exchangeRate.value = await api.settings.updateExchangeRate(sekPerUnit)
    return exchangeRate.value
  }

  return {
    items,
    loading,
    error,
    loaded,
    load,
    create,
    update,
    remove,
    alerts: alerts.data,
    alertsLoading: alerts.loading,
    alertsError: alerts.error,
    loadAlerts,
    acknowledge,
    exchangeRate,
    exchangeRateError,
    loadExchangeRate,
    updateExchangeRate,
  }
})
