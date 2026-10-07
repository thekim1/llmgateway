<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogOverlay,
  AlertDialogPortal,
  AlertDialogRoot,
  AlertDialogTitle,
} from 'reka-ui'
import AppIcon from '@/components/AppIcon.vue'

/**
 * Confirmation for destructive or important actions (WCAG 3.3.6). States the consequence in plain
 * language. Optionally requires typing a confirmation text (e.g. the key name) or ticking a box.
 */
const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    description: string
    confirmLabel: string
    cancelLabel?: string
    tone?: 'danger' | 'primary'
    /** User must type exactly this text to confirm. */
    confirmText?: string
    /** User must tick a checkbox with this label to confirm. */
    acknowledgeLabel?: string
    busy?: boolean
    confirmIcon?: string
  }>(),
  {
    cancelLabel: undefined,
    tone: 'danger',
    confirmText: undefined,
    acknowledgeLabel: undefined,
    busy: false,
    confirmIcon: undefined,
  },
)

const emit = defineEmits<{
  'update:open': [value: boolean]
  confirm: []
}>()

const { t } = useI18n()
const uid = `confirm-${Math.random().toString(36).slice(2, 9)}`
const typed = ref('')
const acknowledged = ref(false)
const error = ref<string | null>(null)

watch(
  () => props.open,
  (open) => {
    if (open) {
      typed.value = ''
      acknowledged.value = false
      error.value = null
    }
  },
)

const requirementMet = computed(() => {
  if (props.confirmText !== undefined && typed.value.trim() !== props.confirmText) return false
  if (props.acknowledgeLabel !== undefined && !acknowledged.value) return false
  return true
})

async function onConfirm(): Promise<void> {
  if (props.busy) return
  if (!requirementMet.value) {
    error.value =
      props.confirmText !== undefined
        ? t('confirm.typeMismatch', { text: props.confirmText })
        : t('confirm.mustAcknowledge')
    await nextTick()
    document.getElementById(props.confirmText !== undefined ? `${uid}-text` : `${uid}-ack`)?.focus()
    return
  }
  emit('confirm')
}

function onOpenChange(value: boolean): void {
  if (!value && props.busy) return
  emit('update:open', value)
}
</script>

<template>
  <AlertDialogRoot :open="open" @update:open="onOpenChange">
    <AlertDialogPortal>
      <AlertDialogOverlay class="dialog-overlay" />
      <AlertDialogContent class="dialog dialog--alert" @submit.prevent>
        <div class="dialog__header">
          <AlertDialogTitle class="dialog__title">
            <AppIcon :name="tone === 'danger' ? 'warning' : 'help'" />
            {{ title }}
          </AlertDialogTitle>
        </div>
        <AlertDialogDescription class="dialog__description">{{ description }}</AlertDialogDescription>
        <div class="dialog__body">
          <slot />
          <div v-if="confirmText !== undefined" class="field" :class="{ 'field--invalid': !!error }">
            <label :for="`${uid}-text`" class="field__label">
              {{ t('confirm.typeLabel', { text: confirmText }) }}
            </label>
            <p :id="`${uid}-help`" class="field__help">{{ t('confirm.typeHelp') }}</p>
            <p v-if="error" :id="`${uid}-error`" class="field__error">
              <AppIcon name="error" />
              <span>{{ error }}</span>
            </p>
            <input
              :id="`${uid}-text`"
              v-model="typed"
              class="input"
              type="text"
              autocomplete="off"
              spellcheck="false"
              :aria-invalid="!!error || undefined"
              :aria-describedby="error ? `${uid}-error ${uid}-help` : `${uid}-help`"
            />
          </div>
          <div v-else-if="acknowledgeLabel !== undefined" class="field" :class="{ 'field--invalid': !!error }">
            <p v-if="error" :id="`${uid}-error`" class="field__error">
              <AppIcon name="error" />
              <span>{{ error }}</span>
            </p>
            <div class="check">
              <input
                :id="`${uid}-ack`"
                v-model="acknowledged"
                type="checkbox"
                :aria-invalid="!!error || undefined"
                :aria-describedby="error ? `${uid}-error` : undefined"
              />
              <label :for="`${uid}-ack`">{{ acknowledgeLabel }}</label>
            </div>
          </div>
        </div>
        <div class="dialog__footer">
          <AlertDialogCancel class="btn btn--secondary" :disabled="busy">
            {{ cancelLabel ?? t('common.cancel') }}
          </AlertDialogCancel>
          <button
            type="button"
            class="btn"
            :class="tone === 'danger' ? 'btn--danger' : 'btn--primary'"
            :aria-busy="busy || undefined"
            data-testid="confirm-action"
            @click="onConfirm"
          >
            <AppIcon v-if="busy || confirmIcon" :name="busy ? 'hourglass_top' : (confirmIcon ?? '')" />
            {{ busy ? t('common.working') : confirmLabel }}
          </button>
        </div>
      </AlertDialogContent>
    </AlertDialogPortal>
  </AlertDialogRoot>
</template>
