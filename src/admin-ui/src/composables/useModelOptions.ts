import { computed, onMounted } from 'vue'
import type { DataResidency } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useCatalogStore } from '@/stores/catalog'
import { useModelsStore } from '@/stores/models'
import { useRoutesStore } from '@/stores/routes'

export interface ModelOption {
  name: string
  /** "route" = model alias with fallback; "model" = a single deployment. */
  source: 'route' | 'model'
  residencies: DataResidency[]
}

/**
 * Names that can be put in a key's "allowed models" list: route aliases + model names.
 * gateway-admin reads /api/routes and /api/models; other roles fall back to the catalogue,
 * which any signed-in user may read.
 */
export function useModelOptions() {
  const auth = useAuthStore()
  const routes = useRoutesStore()
  const models = useModelsStore()
  const catalog = useCatalogStore()

  const loading = computed(() => (auth.isGatewayAdmin ? routes.loading || models.loading : catalog.loading))
  const error = computed(() => (auth.isGatewayAdmin ? (routes.error ?? models.error) : catalog.error))

  const options = computed<ModelOption[]>(() => {
    if (auth.isGatewayAdmin) {
      return [
        ...routes.items.map((r) => ({
          name: r.name,
          source: 'route' as const,
          residencies: [...new Set(r.targets.map((t) => t.residency))],
        })),
        ...models.items.map((m) => ({ name: m.name, source: 'model' as const, residencies: [m.residency] })),
      ]
    }
    const data = catalog.catalog
    if (!data) return []
    return [
      ...data.routes.map((r) => ({ name: r.name, source: 'route' as const, residencies: r.residencies })),
      ...data.models.map((m) => ({ name: m.name, source: 'model' as const, residencies: [m.residency] })),
    ]
  })

  async function load(): Promise<void> {
    if (auth.isGatewayAdmin) {
      await Promise.all([routes.loaded ? null : routes.load(), models.loaded ? null : models.load()])
    } else {
      await catalog.load()
    }
  }

  onMounted(() => {
    void load()
  })

  return { options, loading, error, load }
}
