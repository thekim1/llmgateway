<script setup lang="ts">
import { computed } from 'vue'
import { storeToRefs } from 'pinia'
import { useUiStore } from '@/stores/ui'
import AppIcon from './AppIcon.vue'
import { openDialogs } from './ModalDialog.vue'

const ui = useUiStore()
const { toasts } = storeToRefs(ui)
// An open modal makes everything outside it inert and covers it, so toasts move into the topmost dialog.
const target = computed(() => openDialogs[openDialogs.length - 1] ?? 'body')
</script>

<template>
  <Teleport :to="target">
    <div role="status" aria-live="polite" class="pointer-events-none fixed inset-x-0 bottom-6 z-toast flex flex-col items-center gap-2 max-md:bottom-[calc(80px+env(safe-area-inset-bottom))]">
      <!-- Hover/focus only pauses the auto-dismiss timer; the toast itself is not interactive. -->
      <!-- eslint-disable-next-line vuejs-accessibility/no-static-element-interactions -->
      <div
        v-for="toast in toasts"
        :key="toast.id"
        class="pointer-events-auto flex items-center gap-2 rounded-tile bg-fg px-4 py-3 text-on-fg shadow-2"
        @mouseenter="ui.pause(toast.id)"
        @mouseleave="ui.resume(toast.id)"
        @focusin="ui.pause(toast.id)"
        @focusout="ui.resume(toast.id)"
      >
        <AppIcon name="check_circle" filled />
        {{ toast.message }}
      </div>
    </div>
  </Teleport>
</template>
