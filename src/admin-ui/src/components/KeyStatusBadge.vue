<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusBadge from '@/components/StatusBadge.vue'
import type { StatusTone } from '@/components/types'
import type { KeyStatus } from '@/api/types'

const props = defineProps<{ status: KeyStatus }>()
const { t } = useI18n()

const map: Record<KeyStatus, { tone: StatusTone; icon: string }> = {
  Active: { tone: 'success', icon: 'check_circle' },
  InGracePeriod: { tone: 'warning', icon: 'schedule' },
  Expired: { tone: 'neutral', icon: 'event_busy' },
  Revoked: { tone: 'danger', icon: 'block' },
  Disabled: { tone: 'neutral', icon: 'pause_circle' },
}
const entry = computed(() => map[props.status] ?? { tone: 'neutral' as StatusTone, icon: 'help' })
</script>

<template>
  <StatusBadge :tone="entry.tone" :icon="entry.icon" :text="t(`enums.keyStatus.${status}`)" />
</template>
