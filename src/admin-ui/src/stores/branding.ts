import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api } from '@/api'
import type { Branding } from '@/api/types'

/**
 * The organisation name and logo the console shows (set by gateway admins under Settings). Loaded once, before sign-in,
 * because the sign-in page shows them too. Until it has loaded, or if it cannot load, the console shows no name and the
 * built-in icon rather than another organisation's.
 */
export const useBrandingStore = defineStore('branding', () => {
  const branding = ref<Branding | null>(null)
  let loading: Promise<void> | null = null

  const organisationName = computed(() => branding.value?.organisationName ?? '')
  const logoUrl = computed(() => branding.value?.logoUrl ?? null)

  function ensureLoaded(): Promise<void> {
    loading ??= api.bff
      .branding()
      .then((value) => {
        branding.value = value
      })
      .catch(() => {
        // Not worth an error on screen: the console works without it. A later call tries again.
        loading = null
      })
    return loading
  }

  /** After a gateway admin saved a change: show it at once. */
  function set(value: Branding): void {
    branding.value = value
  }

  return { branding, organisationName, logoUrl, ensureLoaded, set }
})
