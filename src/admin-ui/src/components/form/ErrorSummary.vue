<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'

/** Error summary shown at the top of a form after a failed submit. Receives focus. */
withDefaults(
  defineProps<{
    id: string
    items: { href: string | null; message: string }[]
    level?: 2 | 3
  }>(),
  { level: 2 },
)

const { t } = useI18n()

function focusTarget(href: string): void {
  const target = document.getElementById(href.slice(1))
  if (!target) return
  target.scrollIntoView({ block: 'center' })
  target.focus()
}
</script>

<template>
  <div v-if="items.length" :id="id" class="error-summary" tabindex="-1" role="alert" :aria-labelledby="`${id}-title`">
    <component :is="`h${level}`" :id="`${id}-title`" class="error-summary__title">
      <AppIcon name="error" />
      {{ t('errors.summaryTitle', items.length) }}
    </component>
    <ul>
      <li v-for="(item, index) in items" :key="index">
        <a v-if="item.href" :href="item.href" @click.prevent="focusTarget(item.href)">{{ item.message }}</a>
        <span v-else>{{ item.message }}</span>
      </li>
    </ul>
  </div>
</template>
