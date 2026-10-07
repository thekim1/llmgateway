import { ref } from 'vue'
import { defineStore } from 'pinia'
import { api } from '@/api'
import type {
  CreateKeyRequest,
  CreateKeyResponse,
  KeyFilter,
  KeyRotationMode,
  RotateKeyResponse,
  UpdateKeyRequest,
  VirtualKey,
} from '@/api/types'

/**
 * Virtual keys. Secrets returned from create/rotate are passed straight back to the caller and are
 * never kept in the store (they are shown once in ShowSecretDialog and then discarded).
 */
export const useKeysStore = defineStore('keys', () => {
  const items = ref<VirtualKey[]>([])
  const current = ref<VirtualKey | null>(null)
  const loading = ref(false)
  const error = ref<unknown>(null)

  function upsert(key: VirtualKey): void {
    const index = items.value.findIndex((k) => k.id === key.id)
    if (index === -1) items.value = [key, ...items.value]
    else items.value = items.value.map((k) => (k.id === key.id ? key : k))
    if (current.value?.id === key.id) current.value = key
  }

  async function load(filter: KeyFilter = {}): Promise<void> {
    loading.value = true
    error.value = null
    try {
      items.value = await api.keys.list(filter)
    } catch (e) {
      error.value = e
    } finally {
      loading.value = false
    }
  }

  async function loadOne(id: string): Promise<VirtualKey | null> {
    loading.value = true
    error.value = null
    try {
      current.value = await api.keys.get(id)
      return current.value
    } catch (e) {
      error.value = e
      current.value = null
      return null
    } finally {
      loading.value = false
    }
  }

  async function create(request: CreateKeyRequest): Promise<CreateKeyResponse> {
    const result = await api.keys.create(request)
    upsert(result.key)
    return result
  }

  async function update(id: string, request: UpdateKeyRequest): Promise<VirtualKey> {
    const key = await api.keys.update(id, request)
    upsert(key)
    return key
  }

  async function rotate(id: string, mode: KeyRotationMode = 'RevokeImmediately'): Promise<RotateKeyResponse> {
    const result = await api.keys.rotate(id, mode)
    upsert(result.previousKey)
    upsert(result.key)
    return result
  }

  async function revoke(id: string): Promise<VirtualKey> {
    const key = await api.keys.revoke(id)
    upsert(key)
    return key
  }

  return { items, current, loading, error, load, loadOne, create, update, rotate, revoke }
})
