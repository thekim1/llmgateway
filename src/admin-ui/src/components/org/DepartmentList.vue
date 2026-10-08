<script setup lang="ts">
import type { Department } from '@/api/types'
import { plural } from '@/utils/labels'

defineProps<{ departments: Department[]; selectedId: string | null }>()
defineEmits<{ (e: 'select', id: string): void }>()
</script>

<template>
  <nav aria-label="Departments" class="flex flex-col gap-1">
    <button
      v-for="d in departments"
      :key="d.id"
      type="button"
      :aria-current="d.id === selectedId ? 'true' : undefined"
      class="flex min-h-ctl-lg items-center justify-between gap-3 rounded-control px-3 py-2 text-left"
      :class="d.id === selectedId ? 'bg-surface text-fg shadow-1' : 'text-fg-2 hover:bg-hover'"
      @click="$emit('select', d.id)"
    >
      <span class="flex min-w-0 flex-col" lang="sv">
        <span class="truncate font-medium text-fg">{{ d.name }}</span>
        <span class="font-mono text-caption text-fg-3" lang="en">{{ d.costCenterCode }}<template v-if="!d.isActive"> · Inactive</template></span>
      </span>
      <span class="flex-none text-small text-fg-3">{{ plural(d.teamCount, 'team') }}</span>
    </button>
  </nav>
</template>
