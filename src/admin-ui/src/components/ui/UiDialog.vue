<script setup lang="ts">
import { TONE_CLASSES, type Tone } from '@/utils/labels'
import AppIcon from './AppIcon.vue'
import ModalDialog from './ModalDialog.vue'

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
  <ModalDialog
    v-slot="{ titleId, descriptionId }"
    :open="open"
    :dismissible="dismissible"
    class="material-overlay inset-0 m-auto flex h-fit max-h-[calc(100dvh-32px)] max-w-[calc(100vw-32px)] flex-col gap-5 overflow-auto rounded-dialog p-7 text-fg max-md:inset-x-0 max-md:bottom-0 max-md:top-auto max-md:m-0 max-md:w-auto max-md:max-w-none max-md:rounded-b-none max-md:rounded-t-sheet max-md:p-6 max-md:pb-[max(24px,env(safe-area-inset-bottom))]"
    :class="wide ? 'w-[760px]' : 'w-dialog'"
    @update:open="emit('update:open', $event)"
  >
    <div class="flex flex-col gap-3">
      <span v-if="icon" class="grid size-10 place-items-center rounded-tile" :class="TONE_CLASSES[tone]"><AppIcon :name="icon" :size="22" filled /></span>
      <h2 :id="titleId" class="text-title-3">{{ title }}</h2>
      <div v-if="description || $slots.description" :id="descriptionId" class="text-fg-2"><slot name="description">{{ description }}</slot></div>
    </div>
    <slot />
    <div v-if="$slots.actions" class="flex flex-wrap justify-end gap-2 max-md:flex-col-reverse max-md:[&>*]:h-ctl-xl max-md:[&>*]:w-full"><slot name="actions" /></div>
  </ModalDialog>
</template>
