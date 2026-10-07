<script setup lang="ts">
import { computed, watchEffect } from 'vue'
import { useI18n } from 'vue-i18n'

const props = defineProps<{
  title: string
  lead?: string
}>()

const { t } = useI18n()
const documentTitle = computed(() => `${props.title} – ${t('app.titleSuffix')}`)

watchEffect(() => {
  document.title = documentTitle.value
})
</script>

<template>
  <div class="page-header">
    <div class="page-header__text">
      <h1 id="page-title" tabindex="-1">{{ title }}</h1>
      <p v-if="lead" class="lead">{{ lead }}</p>
      <slot name="lead" />
    </div>
    <div v-if="$slots.actions" class="page-header__actions">
      <slot name="actions" />
    </div>
  </div>
</template>
