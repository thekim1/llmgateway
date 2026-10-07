<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useUiStore } from '@/stores/ui'
import { LOCALES, isAppLocale } from '@/i18n'

withDefaults(defineProps<{ id?: string; compact?: boolean }>(), {
  id: 'locale-picker',
  compact: false,
})

const { t } = useI18n()
const ui = useUiStore()

/** Language names are always shown in their own language (with matching lang attribute). */
const names: Record<string, string> = { sv: 'Svenska', en: 'English' }

function onChange(event: Event): void {
  const value = (event.target as HTMLSelectElement).value
  if (isAppLocale(value)) ui.setLocale(value)
}
</script>

<template>
  <div class="picker" :class="{ 'picker--compact': compact }">
    <label :for="id" class="picker__label">{{ t('locale.label') }}</label>
    <select :id="id" class="select" :value="ui.locale" @change="onChange">
      <option v-for="option in LOCALES" :key="option" :value="option" :lang="option">
        {{ names[option] }}
      </option>
    </select>
  </div>
</template>
