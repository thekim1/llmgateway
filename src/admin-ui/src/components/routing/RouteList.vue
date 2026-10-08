<script setup lang="ts">
import type { OpsProviderHealth, Route } from '@/api/types'
import { KIND_LABEL, plural } from '@/utils/labels'
import AppIcon from '../ui/AppIcon.vue'
import { routeHasUnhealthyTarget } from './tiers'

defineProps<{ routes: Route[]; selectedId: string | null; health: Map<string, OpsProviderHealth> }>()
defineEmits<{ (e: 'select', id: string): void }>()
</script>

<template>
  <nav aria-label="Routes" class="flex flex-col gap-1">
    <button
      v-for="r in routes"
      :key="r.id"
      type="button"
      :aria-current="r.id === selectedId ? 'true' : undefined"
      class="flex min-h-ctl-lg items-center justify-between gap-3 rounded-control px-3 py-2 text-left"
      :class="r.id === selectedId ? 'bg-surface text-fg shadow-1' : 'text-fg-2 hover:bg-hover'"
      @click="$emit('select', r.id)"
    >
      <span class="flex min-w-0 flex-col">
        <span class="break-all font-mono font-medium text-fg">{{ r.name }}</span>
        <span class="text-caption text-fg-3">{{ KIND_LABEL[r.kind] }} · {{ plural(r.targets.length, 'target') }}<template v-if="!r.isEnabled"> · Disabled</template></span>
      </span>
      <span v-if="routeHasUnhealthyTarget(r, health)" class="flex-none text-warn" role="img" aria-label="Has an unavailable target"><AppIcon name="warning" filled /></span>
    </button>
  </nav>
</template>
