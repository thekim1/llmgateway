<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'

const route = useRoute()
const router = useRouter()
const { t } = useI18n()

interface Crumb {
  label: string
  to: string | null
}

const crumbs = computed<Crumb[]>(() => {
  const chain: Crumb[] = []
  const current = route.meta.titleKey
  if (!current || route.name === 'overview') return chain
  chain.push({ label: t(current), to: null })
  let parentName = route.meta.parent
  let guard = 0
  while (parentName && guard++ < 10) {
    const parent = router.getRoutes().find((r) => r.name === parentName)
    if (!parent) break
    chain.unshift({ label: t(parent.meta.titleKey ?? ''), to: router.resolve({ name: parentName }).href })
    parentName = parent.meta.parent
  }
  return chain
})
</script>

<template>
  <nav v-if="crumbs.length > 1" class="breadcrumbs" :aria-label="t('breadcrumbs.label')">
    <ol>
      <li v-for="(crumb, index) in crumbs" :key="index">
        <RouterLink v-if="crumb.to" :to="crumb.to">{{ crumb.label }}</RouterLink>
        <span v-else aria-current="page">{{ crumb.label }}</span>
      </li>
    </ol>
  </nav>
</template>
