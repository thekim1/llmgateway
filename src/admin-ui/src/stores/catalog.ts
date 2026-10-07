import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Catalog } from '@/api/types'
import { useLoadable } from './loadable'

export const useCatalogStore = defineStore('catalog', () => {
  const { data: catalog, loading, error, loaded, run } = useLoadable<Catalog | null>(null)

  /** The catalogue rarely changes – load once unless forced. */
  async function load(force = false): Promise<Catalog | null> {
    if (loaded.value && !force) return catalog.value
    return run(() => api.catalog.get())
  }

  return { catalog, loading, error, loaded, load }
})
