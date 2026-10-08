<script setup lang="ts">
import { DialogContent, DialogDescription, DialogOverlay, DialogPortal, DialogRoot, DialogTitle } from 'reka-ui'
import AppIcon from './AppIcon.vue'

withDefaults(defineProps<{ open: boolean; title: string; subtitle?: string; wide?: boolean; description?: string }>(), {})
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()
</script>

<template>
  <DialogRoot :open="open" @update:open="emit('update:open', $event)">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-drawer bg-scrim" />
      <DialogContent
        class="material-overlay fixed inset-y-0 right-0 z-drawer flex w-full flex-col text-fg"
        :class="wide ? 'max-w-drawer-form' : 'max-w-drawer'"
      >
        <header class="flex items-start justify-between gap-4 border-b border-border p-6 pb-5">
          <div class="flex min-w-0 flex-col items-start gap-2">
            <slot name="badge" />
            <DialogTitle class="text-title-2 outline-none">{{ title }}</DialogTitle>
            <span v-if="subtitle" class="font-mono text-small text-fg-3">{{ subtitle }}</span>
            <DialogDescription class="sr-only">{{ description ?? title }}</DialogDescription>
          </div>
          <button type="button" aria-label="Close" class="grid size-8 flex-none place-items-center rounded-control text-fg-2 hover:bg-hover" @click="emit('update:open', false)">
            <AppIcon name="close" />
          </button>
        </header>
        <div class="min-h-0 flex-1 overflow-auto px-6 py-5"><slot /></div>
        <footer v-if="$slots.footer" class="flex flex-wrap items-center gap-2 border-t border-border p-4 px-6"><slot name="footer" /></footer>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
