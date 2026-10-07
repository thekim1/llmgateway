<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'

const props = defineProps<{ page: number; pageSize: number; total: number; label: string }>()
const emit = defineEmits<{ change: [page: number] }>()
const { t } = useI18n()

const pages = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))
</script>

<template>
  <nav class="pagination" :aria-label="label">
    <button type="button" class="btn btn--secondary" :disabled="page <= 1" @click="emit('change', page - 1)">
      <AppIcon name="chevron_left" />
      {{ t('common.previousPage') }}
    </button>
    <p class="pagination__status">
      {{ t('common.pageOf', { page, pages }) }} · {{ t('common.totalRows', { n: total }) }}
    </p>
    <button type="button" class="btn btn--secondary" :disabled="page >= pages" @click="emit('change', page + 1)">
      {{ t('common.nextPage') }}
      <AppIcon name="chevron_right" />
    </button>
  </nav>
</template>
