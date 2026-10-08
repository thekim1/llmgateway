<script setup lang="ts">
import type { OpsProviderHealth } from '@/api/types'
import { targetStatus, type Tier } from './tiers'
import AppIcon from '../ui/AppIcon.vue'
import ResidencyLabel from '../ui/ResidencyLabel.vue'
import UiBadge from '../ui/UiBadge.vue'

defineProps<{ tiers: Tier[]; health: Map<string, OpsProviderHealth> }>()
</script>

<template>
  <ol class="flex flex-wrap items-stretch gap-3 max-md:flex-col">
    <li v-for="tier in tiers" :key="tier.priority" class="flex min-w-0 items-stretch gap-3 max-md:flex-col">
      <div v-if="tier.position > 1" class="flex flex-none flex-col items-center justify-center gap-1 text-small text-fg-3 max-md:flex-row">
        <AppIcon name="arrow_forward" class="max-md:hidden" />
        <AppIcon name="arrow_downward" class="md:hidden" />
        on failure
      </div>
      <div class="flex min-w-[220px] flex-1 flex-col gap-2">
        <div class="flex items-baseline gap-2">
          <h4 class="font-medium">Priority {{ tier.position }}</h4>
          <span class="text-small text-fg-3">{{ tier.position === 1 ? 'Tried first' : 'Fallback' }}</span>
        </div>
        <div v-for="item in tier.targets" :key="item.target.modelId" class="flex flex-col gap-3 rounded-tile bg-surface-2 p-4">
          <div class="flex items-start justify-between gap-3">
            <div class="flex min-w-0 flex-col">
              <span class="break-all font-mono font-medium">{{ item.target.modelName }}</span>
              <span class="text-small text-fg-2">{{ item.target.providerName }} · <ResidencyLabel :residency="item.target.residency" /></span>
            </div>
            <UiBadge v-if="targetStatus(health.get(item.target.providerName))" :tone="targetStatus(health.get(item.target.providerName))!.tone" :label="targetStatus(health.get(item.target.providerName))!.label" />
          </div>
          <div class="flex items-center gap-3">
            <div class="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-sunken" aria-hidden="true">
              <div class="h-full rounded-full bg-chart" :style="{ width: `${item.share}%` }" />
            </div>
            <span class="tabular text-small text-fg-2">{{ item.share }}% of tier</span>
          </div>
        </div>
      </div>
    </li>
  </ol>
</template>
