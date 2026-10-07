<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'
import type { DataResidency } from '@/api/types'

/** Data residency shown as icon + text, e.g. "On-prem", "EU", "Extern". */
withDefaults(defineProps<{ residency: DataResidency; withLabel?: boolean }>(), { withLabel: false })

const { t } = useI18n()

const icons: Record<DataResidency, string> = {
  OnPrem: 'dns',
  Eu: 'flag',
  External: 'public',
}
</script>

<template>
  <span class="badge" :class="`badge--residency-${residency.toLowerCase()}`" :data-residency="residency">
    <AppIcon :name="icons[residency]" />
    <span>
      <span v-if="withLabel" class="visually-hidden">{{ t('residency.label') }}: </span>{{
        t(`enums.residency.${residency}`)
      }}
    </span>
  </span>
</template>
