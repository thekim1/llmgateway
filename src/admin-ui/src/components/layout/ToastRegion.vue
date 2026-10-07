<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'
import { useUiStore, type ToastKind } from '@/stores/ui'

const { t } = useI18n()
const ui = useUiStore()

const icons: Record<ToastKind, string> = {
  success: 'check_circle',
  info: 'info',
  warning: 'warning',
  error: 'error',
}
</script>

<template>
  <div class="toasts">
    <!-- Always rendered so screen readers pick up additions. Toasts never disappear on a timer. -->
    <div aria-live="polite" aria-relevant="additions text" class="toasts__live">
      <ul v-if="ui.toasts.length" class="toasts__list">
        <li v-for="toast in ui.toasts" :key="toast.id" class="toast" :class="`toast--${toast.kind}`">
          <AppIcon :name="icons[toast.kind]" />
          <p class="toast__message">
            <span class="visually-hidden">{{ t(`toasts.kind.${toast.kind}`) }}: </span>{{ toast.message }}
          </p>
          <button type="button" class="btn btn--ghost btn--icon" @click="ui.dismiss(toast.id)">
            <AppIcon name="close" />
            <span class="visually-hidden">{{ t('toasts.dismiss') }}</span>
          </button>
        </li>
      </ul>
    </div>
    <button v-if="ui.toasts.length > 1" type="button" class="btn btn--secondary toasts__clear" @click="ui.dismissAll()">
      {{ t('toasts.dismissAll') }}
    </button>
  </div>
</template>
