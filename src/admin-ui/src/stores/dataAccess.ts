import { ref } from 'vue'
import { defineStore } from 'pinia'
import { api } from '@/api'
import type { CreateDataApiKeyResponse, DataAccessSettings, DataApiKey, DataApiKeyRequest } from '@/api/types'

/**
 * Data API keys (gateway-admin only). The secret from create is passed straight back to the caller and never kept
 * in the store; it is shown once in SecretDialog and then discarded.
 */
export const useDataAccessStore = defineStore('dataAccess', () => {
  const items = ref<DataApiKey[]>([])
  const settings = ref<DataAccessSettings | null>(null)
  const loading = ref(false)
  const error = ref<unknown>(null)

  function upsert(key: DataApiKey): void {
    const index = items.value.findIndex((k) => k.id === key.id)
    if (index === -1) items.value = [...items.value, key].sort((a, b) => a.name.localeCompare(b.name))
    else items.value = items.value.map((k) => (k.id === key.id ? key : k))
  }

  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const [keys, current] = await Promise.all([api.dataAccess.list(), api.dataAccess.settings()])
      items.value = keys
      settings.value = current
    } catch (e) {
      error.value = e
    } finally {
      loading.value = false
    }
  }

  async function create(request: DataApiKeyRequest): Promise<CreateDataApiKeyResponse> {
    const result = await api.dataAccess.create(request)
    upsert(result.key)
    return result
  }

  async function update(id: string, request: DataApiKeyRequest): Promise<DataApiKey> {
    const key = await api.dataAccess.update(id, request)
    upsert(key)
    return key
  }

  async function revoke(id: string): Promise<DataApiKey> {
    const key = await api.dataAccess.revoke(id)
    upsert(key)
    return key
  }

  async function remove(id: string): Promise<void> {
    await api.dataAccess.remove(id)
    items.value = items.value.filter((k) => k.id !== id)
  }

  return { items, settings, loading, error, load, create, update, revoke, remove }
})
