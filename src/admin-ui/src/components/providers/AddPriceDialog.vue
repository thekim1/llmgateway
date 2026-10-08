<script setup lang="ts">
import { ref, watch } from 'vue'
import type { Model } from '@/api/types'
import { useModelsStore } from '@/stores/models'
import { useUiStore } from '@/stores/ui'
import { problemLang, problemMessage } from '@/utils/problem'
import InlineError from '../ui/InlineError.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'
import PriceFields from './PriceFields.vue'
import { emptyPriceDraft, toNewPrice, validatePrice, type PriceErrors } from './priceDraft'

const props = defineProps<{ open: boolean; model: Model | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved'): void }>()

const models = useModelsStore()
const ui = useUiStore()
const draft = ref(emptyPriceDraft())
const errors = ref<PriceErrors>({})
const error = ref<unknown>(null)
const busy = ref(false)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    draft.value = emptyPriceDraft()
    errors.value = {}
    error.value = null
  },
)

async function submit(): Promise<void> {
  if (!props.model || busy.value) return
  errors.value = validatePrice(draft.value)
  if (Object.keys(errors.value).length > 0) return
  busy.value = true
  error.value = null
  try {
    await models.addPrice(props.model.id, toNewPrice(draft.value))
    ui.notify('Price added')
    emit('saved')
    emit('update:open', false)
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDialog :open="open" title="Add price" description="The new price applies from the date you choose. Earlier requests keep their old price." @update:open="emit('update:open', $event)">
    <form id="price-form" class="flex flex-col gap-4" novalidate @submit.prevent="submit">
      <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
      <PriceFields v-model="draft" :errors="errors" id-prefix="price" optional-from />
    </form>
    <template #actions>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      <UiButton variant="primary" type="submit" form="price-form" :loading="busy">Add price</UiButton>
    </template>
  </UiDialog>
</template>
