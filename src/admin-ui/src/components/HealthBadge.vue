<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusBadge from '@/components/StatusBadge.vue'
import type { StatusTone } from '@/components/types'
import type { HealthStatus } from '@/api/types'

const props = defineProps<{ status: HealthStatus }>()
const { t } = useI18n()

const map: Record<HealthStatus, { tone: StatusTone; icon: string }> = {
  Healthy: { tone: 'success', icon: 'check_circle' },
  Degraded: { tone: 'warning', icon: 'warning' },
  Unhealthy: { tone: 'danger', icon: 'error' },
}
const entry = computed(() => map[props.status] ?? { tone: 'neutral' as StatusTone, icon: 'help' })
</script>

<template>
  <StatusBadge :tone="entry.tone" :icon="entry.icon" :text="t(`enums.health.${status}`)" />
</template>
