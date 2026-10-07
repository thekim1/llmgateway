<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'

/** Copies text to the clipboard and announces the result in a local live region. */
const props = withDefaults(defineProps<{ text: string; label?: string; describedBy?: string }>(), {
  label: undefined,
  describedBy: undefined,
})

const { t } = useI18n()
const state = ref<'idle' | 'copied' | 'failed'>('idle')
const announcement = ref('')

async function copy(): Promise<void> {
  try {
    if (!navigator.clipboard?.writeText) throw new Error('Clipboard API saknas')
    await navigator.clipboard.writeText(props.text)
    state.value = 'copied'
    announcement.value = t('common.copied')
  } catch {
    state.value = 'failed'
    announcement.value = t('common.copyFailed')
  }
}
</script>

<template>
  <span class="copy-button">
    <button type="button" class="btn btn--secondary" :aria-describedby="describedBy" @click="copy">
      <AppIcon :name="state === 'copied' ? 'check' : 'content_copy'" />
      {{ state === 'copied' ? t('common.copied') : (label ?? t('common.copy')) }}
    </button>
    <span role="status" class="copy-button__status" :class="{ 'visually-hidden': state !== 'failed' }">{{
      announcement
    }}</span>
  </span>
</template>
