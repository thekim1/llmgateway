<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'

/** Fieldset with legend, help text and inline error for radio/checkbox groups. */
const props = withDefaults(
  defineProps<{
    id: string
    legend: string
    help?: string
    error?: string
  }>(),
  { help: undefined, error: undefined },
)

const { t } = useI18n()
const helpId = computed(() => `${props.id}-help`)
const errorId = computed(() => `${props.id}-error`)
const describedBy = computed(
  () => [props.error ? errorId.value : null, props.help ? helpId.value : null].filter(Boolean).join(' ') || undefined,
)
</script>

<template>
  <fieldset :id="id" class="fieldset" :class="{ 'field--invalid': !!error }" :aria-describedby="describedBy" tabindex="-1">
    <legend class="fieldset__legend">{{ legend }}</legend>
    <p v-if="help" :id="helpId" class="field__help">{{ help }}</p>
    <p v-if="error" :id="errorId" class="field__error">
      <AppIcon name="error" />
      <span><span class="visually-hidden">{{ t('common.errorPrefix') }}: </span>{{ error }}</span>
    </p>
    <slot />
  </fieldset>
</template>
