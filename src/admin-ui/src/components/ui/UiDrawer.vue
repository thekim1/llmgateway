<script setup lang="ts">
import AppIcon from './AppIcon.vue'
import ModalDialog from './ModalDialog.vue'

withDefaults(defineProps<{ open: boolean; title: string; subtitle?: string; wide?: boolean; description?: string }>(), {})
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()
</script>

<template>
  <ModalDialog
    v-slot="{ titleId, descriptionId }"
    :open="open"
    class="material-overlay inset-y-0 right-0 flex h-dvh w-full flex-col text-fg"
    :class="wide ? 'max-w-drawer-form' : 'max-w-drawer'"
    @update:open="emit('update:open', $event)"
  >
    <header class="flex items-start justify-between gap-4 border-b border-border p-6 pb-5">
      <div class="flex min-w-0 flex-col items-start gap-2">
        <slot name="badge" />
        <h2 :id="titleId" class="text-title-2 outline-none">{{ title }}</h2>
        <span v-if="subtitle" class="font-mono text-small text-fg-3">{{ subtitle }}</span>
        <p :id="descriptionId" class="sr-only">{{ description ?? title }}</p>
      </div>
      <button type="button" aria-label="Close" class="grid size-8 flex-none place-items-center rounded-control text-fg-2 hover:bg-hover" @click="emit('update:open', false)">
        <AppIcon name="close" />
      </button>
    </header>
    <div class="min-h-0 flex-1 overflow-auto px-6 py-5"><slot /></div>
    <footer v-if="$slots.footer" class="flex flex-wrap items-center gap-2 border-t border-border p-4 px-6 pb-[max(16px,env(safe-area-inset-bottom))]"><slot name="footer" /></footer>
  </ModalDialog>
</template>
