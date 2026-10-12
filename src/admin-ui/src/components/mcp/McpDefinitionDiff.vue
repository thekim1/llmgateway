<script setup lang="ts">
import type { McpDefinitionChange } from '@/api/types'
import { MCP_CHANGE_KIND } from '@/utils/labels'
import { jsonText } from '@/utils/mcp'
import AppIcon from '../ui/AppIcon.vue'

/** The differences between a tool's approved definition and the one its server offers now, as text (never colour alone). */
defineProps<{ changes: McpDefinitionChange[] }>()
</script>

<template>
  <ol class="m-0 flex list-none flex-col gap-3 p-0" aria-label="Changes from the approved definition">
    <li v-for="change in changes" :key="change.path" class="rounded-control border border-border p-3">
      <p class="mb-2 flex flex-wrap items-center gap-2 text-small">
        <AppIcon :name="MCP_CHANGE_KIND[change.kind].icon" :size="16" />
        <span class="font-medium">{{ MCP_CHANGE_KIND[change.kind].label }}</span>
        <code class="font-mono">{{ change.path || 'the whole definition' }}</code>
      </p>
      <dl class="m-0 grid grid-cols-2 gap-3 max-md:grid-cols-1">
        <div v-if="change.kind !== 'Added'" class="min-w-0">
          <dt class="text-caption text-fg-3">Approved (what keys see)</dt>
          <dd class="m-0"><pre class="m-0 overflow-x-auto whitespace-pre-wrap break-words rounded-chip bg-sunken p-2 font-mono text-small" tabindex="0">{{ jsonText(change.before) }}</pre></dd>
        </div>
        <div v-if="change.kind !== 'Removed'" class="min-w-0">
          <dt class="text-caption text-fg-3">Offered now (needs approval)</dt>
          <dd class="m-0"><pre class="m-0 overflow-x-auto whitespace-pre-wrap break-words rounded-chip bg-sunken p-2 font-mono text-small" tabindex="0">{{ jsonText(change.after) }}</pre></dd>
        </div>
      </dl>
    </li>
  </ol>
</template>
