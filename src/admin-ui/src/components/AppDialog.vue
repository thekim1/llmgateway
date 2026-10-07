<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import AppIcon from '@/components/AppIcon.vue'

/**
 * Modal dialog built on Reka UI. Clicking outside never closes it (prevents accidental data
 * loss); Escape closes unless `persistent` is set.
 */
const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    description?: string
    persistent?: boolean
    wide?: boolean
    showClose?: boolean
  }>(),
  { description: undefined, persistent: false, wide: false, showClose: true },
)

const emit = defineEmits<{
  'update:open': [value: boolean]
  /** Emitted when the user tries to close a persistent dialog. */
  blockedClose: []
}>()

const { t } = useI18n()

function onOpenChange(value: boolean): void {
  if (!value && props.persistent) {
    emit('blockedClose')
    return
  }
  emit('update:open', value)
}

function onEscape(event: Event): void {
  if (props.persistent) {
    event.preventDefault()
    emit('blockedClose')
  }
}

function preventOutside(event: Event): void {
  event.preventDefault()
}
</script>

<template>
  <DialogRoot :open="open" @update:open="onOpenChange">
    <DialogPortal>
      <DialogOverlay class="dialog-overlay" />
      <DialogContent
        class="dialog"
        :class="{ 'dialog--wide': wide }"
        v-bind="description ? {} : { 'aria-describedby': undefined }"
        @escape-key-down="onEscape"
        @pointer-down-outside="preventOutside"
        @interact-outside="preventOutside"
      >
        <div class="dialog__header">
          <DialogTitle class="dialog__title">{{ title }}</DialogTitle>
          <DialogClose v-if="showClose && !persistent" class="btn btn--ghost btn--icon dialog__close">
            <AppIcon name="close" />
            <span class="visually-hidden">{{ t('common.closeDialog') }}</span>
          </DialogClose>
        </div>
        <DialogDescription v-if="description" class="dialog__description">{{ description }}</DialogDescription>
        <div class="dialog__body">
          <slot />
        </div>
        <div v-if="$slots.footer" class="dialog__footer">
          <slot name="footer" />
        </div>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
