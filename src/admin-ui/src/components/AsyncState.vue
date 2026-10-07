<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'
import { isApiError } from '@/api/client'
import { useErrorMessage } from '@/composables/useForm'

/**
 * Wraps content that is loaded from the API: shows loading (aria-busy), a friendly
 * "Du saknar behörighet" message on 403 and a retry option on other errors.
 */
const props = withDefaults(
  defineProps<{
    loading: boolean
    error?: unknown
    /** Keep showing the slot while reloading (e.g. manual refresh). */
    keepContent?: boolean
  }>(),
  { error: undefined, keepContent: false },
)

const emit = defineEmits<{ retry: [] }>()
const { t } = useI18n()
const errorMessage = useErrorMessage()

const forbidden = computed(() => isApiError(props.error) && props.error.status === 403)
const message = computed(() => (props.error ? errorMessage(props.error) : ''))
</script>

<template>
  <div class="async-state" :aria-busy="loading ? 'true' : 'false'">
    <p v-if="loading" class="loading">
      <AppIcon name="hourglass_top" />
      {{ t('common.loading') }}
    </p>
    <div v-if="!loading && forbidden" class="notice notice--warning">
      <AppIcon name="lock" />
      <div>
        <p class="notice__title">{{ t('errors.forbiddenTitle') }}</p>
        <p>{{ t('errors.forbiddenBody') }}</p>
      </div>
    </div>
    <div v-else-if="!loading && error" class="notice notice--danger">
      <AppIcon name="error" />
      <div>
        <p class="notice__title">{{ t('errors.loadFailed') }}</p>
        <p>{{ message }}</p>
        <button type="button" class="btn btn--secondary" @click="emit('retry')">
          <AppIcon name="refresh" />
          {{ t('common.retry') }}
        </button>
      </div>
    </div>
    <slot v-else-if="!loading || keepContent" />
  </div>
</template>
