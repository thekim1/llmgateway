<script setup lang="ts">
import { DialogContent, DialogDescription, DialogOverlay, DialogPortal, DialogRoot, DialogTitle } from 'reka-ui'
import { TONE_CLASSES, type Tone } from '@/utils/labels'
import AppIcon from './AppIcon.vue'

withDefaults(
  defineProps<{
    open: boolean
    title: string
    description?: string
    icon?: string
    tone?: Tone
    /** When false, Esc and scrim clicks do nothing (e.g. an unconfirmed secret). */
    dismissible?: boolean
    /** Wider dialog for lists and editors. */
    wide?: boolean
  }>(),
  { tone: 'neutral', dismissible: true },
)
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()
</script>

<template>
  <DialogRoot :open="open" @update:open="emit('update:open', $event)">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-dialog bg-scrim" />
      <DialogContent
        class="material-overlay fixed left-1/2 top-1/2 z-dialog flex max-h-[calc(100vh-32px)] max-w-[calc(100vw-32px)] -translate-x-1/2 -translate-y-1/2 flex-col gap-5 overflow-auto rounded-dialog p-7 text-fg max-md:inset-x-0 max-md:bottom-0 max-md:left-0 max-md:top-auto max-md:w-auto max-md:max-w-none max-md:translate-x-0 max-md:translate-y-0 max-md:rounded-b-none max-md:rounded-t-sheet max-md:p-6"
        :class="wide ? 'w-[760px]' : 'w-dialog'"
        @escape-key-down="(e) => !dismissible && e.preventDefault()"
        @pointer-down-outside="(e) => !dismissible && e.preventDefault()"
        @interact-outside="(e) => !dismissible && e.preventDefault()"
      >
        <div class="flex flex-col gap-3">
          <span v-if="icon" class="grid size-10 place-items-center rounded-tile" :class="TONE_CLASSES[tone]"><AppIcon :name="icon" :size="22" filled /></span>
          <DialogTitle class="text-title-3">{{ title }}</DialogTitle>
          <DialogDescription v-if="description || $slots.description" as="div" class="text-fg-2"><slot name="description">{{ description }}</slot></DialogDescription>
        </div>
        <slot />
        <div v-if="$slots.actions" class="flex flex-wrap justify-end gap-2 max-md:flex-col-reverse max-md:[&>*]:h-ctl-xl max-md:[&>*]:w-full"><slot name="actions" /></div>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
