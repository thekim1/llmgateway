<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useUiStore } from '@/stores/ui'
import { THEME_PREFERENCES, isThemePreference } from '@/theme/themes'

const props = withDefaults(defineProps<{ id?: string; compact?: boolean }>(), {
  id: 'theme-picker',
  compact: false,
})

const { t } = useI18n()
const ui = useUiStore()

const helpId = computed(() => `${props.id}-help`)

function onChange(event: Event): void {
  const value = (event.target as HTMLSelectElement).value
  if (isThemePreference(value)) ui.setTheme(value)
}
</script>

<template>
  <div class="picker" :class="{ 'picker--compact': compact }">
    <label :for="id" class="picker__label">{{ t('theme.label') }}</label>
    <p v-if="!compact" :id="helpId" class="help">{{ t('theme.help') }}</p>
    <select
      :id="id"
      class="select"
      :value="ui.themePreference"
      :aria-describedby="compact ? undefined : helpId"
      @change="onChange"
    >
      <option v-for="option in THEME_PREFERENCES" :key="option" :value="option">
        {{ t(`theme.options.${option}`) }}
      </option>
    </select>
  </div>
</template>
