<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppDialog from '@/components/AppDialog.vue'
import AppIcon from '@/components/AppIcon.vue'
import FieldGroup from '@/components/form/FieldGroup.vue'
import type { KeyRotationMode } from '@/api/types'

const props = withDefaults(
  defineProps<{
    open: boolean
    keyName: string
    busy?: boolean
  }>(),
  { busy: false },
)

const emit = defineEmits<{
  'update:open': [value: boolean]
  confirm: [mode: KeyRotationMode]
}>()

const { t } = useI18n()
const mode = ref<KeyRotationMode>('RevokeImmediately')

watch(
  () => props.open,
  (open) => {
    if (open) mode.value = 'RevokeImmediately'
  },
)

const options: { value: KeyRotationMode; icon: string }[] = [
  { value: 'RevokeImmediately', icon: 'block' },
  { value: 'Grace24Hours', icon: 'schedule' },
]
</script>

<template>
  <AppDialog
    :open="open"
    :title="t('rotate.title', { name: keyName })"
    :description="t('rotate.description')"
    :persistent="busy"
    @update:open="emit('update:open', $event)"
  >
    <form id="rotate-form" @submit.prevent="emit('confirm', mode)">
      <FieldGroup id="rotate-mode" :legend="t('rotate.legend')">
        <div v-for="option in options" :key="option.value" class="choice">
          <input
            :id="`rotate-${option.value}`"
            v-model="mode"
            type="radio"
            name="rotate-mode"
            :value="option.value"
            :aria-describedby="`rotate-${option.value}-help`"
          />
          <label :for="`rotate-${option.value}`" class="choice__label">
            <AppIcon :name="option.icon" />
            {{ t(`enums.rotation.${option.value}`) }}
          </label>
          <p :id="`rotate-${option.value}-help`" class="choice__help">
            {{ t(`rotate.help.${option.value}`) }}
          </p>
        </div>
      </FieldGroup>
      <p class="help">{{ t('rotate.afterwards') }}</p>
    </form>
    <template #footer>
      <button type="button" class="btn btn--secondary" :disabled="busy" @click="emit('update:open', false)">
        {{ t('common.cancel') }}
      </button>
      <button type="submit" form="rotate-form" class="btn btn--primary" :aria-busy="busy || undefined">
        <AppIcon :name="busy ? 'hourglass_top' : 'autorenew'" />
        {{ busy ? t('common.working') : t('rotate.confirm') }}
      </button>
    </template>
  </AppDialog>
</template>
