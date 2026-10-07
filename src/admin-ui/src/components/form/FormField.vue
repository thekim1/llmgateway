<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'

/**
 * Visible label + help text + inline error for one form control. The control is rendered in the
 * default slot and receives `id`, `describedBy` and `invalid` to bind.
 */
const props = withDefaults(
  defineProps<{
    id: string
    label: string
    help?: string
    error?: string
    required?: boolean
  }>(),
  { help: undefined, error: undefined, required: false },
)

const { t } = useI18n()
const helpId = computed(() => `${props.id}-help`)
const errorId = computed(() => `${props.id}-error`)
const describedBy = computed(
  () => [props.error ? errorId.value : null, props.help ? helpId.value : null].filter(Boolean).join(' ') || undefined,
)
</script>

<template>
  <div class="field" :class="{ 'field--invalid': !!error }">
    <label :for="id" class="field__label">
      {{ label }}
      <span v-if="!required" class="field__optional">({{ t('common.optional') }})</span>
    </label>
    <p v-if="help" :id="helpId" class="field__help">{{ help }}</p>
    <p v-if="error" :id="errorId" class="field__error">
      <AppIcon name="error" />
      <span><span class="visually-hidden">{{ t('common.errorPrefix') }}: </span>{{ error }}</span>
    </p>
    <slot :id="id" :described-by="describedBy" :invalid="!!error" />
  </div>
</template>
