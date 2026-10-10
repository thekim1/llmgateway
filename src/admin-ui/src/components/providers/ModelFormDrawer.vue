<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { Model, ModelKind, ParameterProfile, Price, Provider } from '@/api/types'
import { MODEL_KINDS, PARAMETER_PROFILES } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useModelsStore } from '@/stores/models'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatUsd } from '@/utils/format'
import { KIND_LABEL, MODEL_FEATURES, PARAMETER_PROFILE_LABEL, featureLabel } from '@/utils/labels'
import AsyncState from '../ui/AsyncState.vue'
import CheckField from '../ui/CheckField.vue'
import ConfirmDialog from '../ui/ConfirmDialog.vue'
import DataTable, { type Column } from '../ui/DataTable.vue'
import FieldGroup from '../ui/FieldGroup.vue'
import InlineError from '../ui/InlineError.vue'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'
import ToggleChip from '../ui/ToggleChip.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiDrawer from '../ui/UiDrawer.vue'
import AddPriceDialog from './AddPriceDialog.vue'
import PriceFields from './PriceFields.vue'
import { emptyPriceDraft, isPriceDraftEmpty, toNewPrice, validatePrice, type PriceErrors } from './priceDraft'

const props = defineProps<{ open: boolean; model: Model | null; providers: Provider[] }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'deleted'): void }>()

const models = useModelsStore()
const ui = useUiStore()
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors({
  providerId: 'model-provider',
  name: 'model-name',
  upstreamModel: 'model-upstream',
  kind: 'model-kind',
  parameterProfile: 'model-profile',
  contextWindow: 'model-context',
})

const providerId = ref('')
const name = ref('')
const upstreamModel = ref('')
const kind = ref<ModelKind>('Chat')
const parameterProfile = ref<ParameterProfile>('Standard')
const contextWindow = ref('')
const isEnabled = ref(true)
const features = ref<string[]>([])
const busy = ref(false)
const priceDraft = ref(emptyPriceDraft())
const priceErrors = ref<PriceErrors>({})
const prices = ref<Price[]>([])
const pricesLoading = ref(false)
const pricesError = ref<unknown>(null)
const addPrice = ref(false)
const confirmDelete = ref(false)

const featureOptions = computed(() => [...new Set([...MODEL_FEATURES, ...features.value])])
function toggleFeature(f: string, on: boolean): void {
  features.value = on ? [...features.value, f] : features.value.filter((x) => x !== f)
}
const providerOptions = computed(() => props.providers.map((p) => ({ value: p.id, label: p.displayName ?? p.name })))
const kindOptions = MODEL_KINDS.map((v) => ({ value: v, label: KIND_LABEL[v] }))
const profileOptions = PARAMETER_PROFILES.map((v) => ({ value: v, label: PARAMETER_PROFILE_LABEL[v] }))
const priceColumns: Column[] = [
  { key: 'from', label: 'Effective from' },
  { key: 'input', label: 'Input', align: 'right' },
  { key: 'cached', label: 'Cached', align: 'right' },
  { key: 'output', label: 'Output', align: 'right' },
  { key: 'audio', label: 'Audio / min', align: 'right' },
  { key: 'audioTokens', label: 'Audio tokens in / out', align: 'right' },
]

async function loadPrices(): Promise<void> {
  if (!props.model) return
  pricesLoading.value = true
  pricesError.value = null
  try {
    prices.value = await models.prices(props.model.id)
  } catch (e) {
    pricesError.value = e
  } finally {
    pricesLoading.value = false
  }
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    const m = props.model
    providerId.value = m?.providerId ?? props.providers[0]?.id ?? ''
    name.value = m?.name ?? ''
    upstreamModel.value = m?.upstreamModel ?? ''
    kind.value = m?.kind ?? 'Chat'
    parameterProfile.value = m?.parameterProfile ?? 'Standard'
    contextWindow.value = m?.contextWindow != null ? String(m.contextWindow) : ''
    isEnabled.value = m?.isEnabled ?? true
    features.value = [...(m?.features ?? [])]
    priceDraft.value = emptyPriceDraft()
    priceErrors.value = {}
    prices.value = []
    void loadPrices()
  },
  { immediate: true },
)

function contextProblem(): string | false {
  if (contextWindow.value.trim() === '') return false
  const value = Number(contextWindow.value)
  return Number.isInteger(value) && value >= 1 ? false : 'Enter a whole number of tokens, 1 or higher.'
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    providerId: !props.model && !providerId.value && 'Choose the provider that serves this model.',
    name: !name.value.trim() && 'Give the model a name.',
    upstreamModel: !upstreamModel.value.trim() && 'Enter the model name the provider expects.',
    contextWindow: contextProblem(),
  })
  const withPrice = !props.model && !isPriceDraftEmpty(priceDraft.value)
  priceErrors.value = withPrice ? validatePrice(priceDraft.value) : {}
  if (!ok || Object.keys(priceErrors.value).length > 0) return
  busy.value = true
  try {
    const body = {
      name: name.value.trim(),
      upstreamModel: upstreamModel.value.trim(),
      kind: kind.value,
      parameterProfile: parameterProfile.value,
      contextWindow: contextWindow.value.trim() === '' ? null : Number(contextWindow.value),
      isEnabled: isEnabled.value,
      features: features.value,
    }
    if (props.model) await models.update(props.model.id, body)
    else await models.create({ ...body, providerId: providerId.value, ...(withPrice ? { price: toNewPrice(priceDraft.value) } : {}) })
    ui.notify('Model saved')
    emit('update:open', false)
  } catch (e) {
    applyServerError(e)
  } finally {
    busy.value = false
  }
}

async function remove(): Promise<void> {
  if (!props.model) return
  await models.remove(props.model.id)
  ui.notify('Model deleted')
  emit('deleted')
  emit('update:open', false)
}

async function priceAdded(): Promise<void> {
  await loadPrices()
}
</script>

<template>
  <UiDrawer wide :open="open" :title="model ? 'Edit model' : 'New model'" :subtitle="model?.name" @update:open="emit('update:open', $event)">
    <template v-if="model" #badge>
      <UiBadge :tone="model.isEnabled ? 'ok' : 'neutral'" :label="model.isEnabled ? 'Enabled' : 'Disabled'" />
    </template>
    <form id="model-form" class="flex flex-col gap-5" novalidate @submit.prevent="submit">
      <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />
      <SelectField v-if="!model" id="model-provider" v-model="providerId" label="Provider" :options="providerOptions" placeholder="Choose a provider" :error="errors.providerId" />
      <TextField id="model-name" v-model="name" label="Name" mono :error="errors.name" hint="The name clients can request directly." />
      <TextField id="model-upstream" v-model="upstreamModel" label="Upstream model" mono :error="errors.upstreamModel" hint="The model or deployment name at the provider." />
      <SelectField id="model-kind" v-model="kind" label="Kind" :options="kindOptions" :error="errors.kind" />
      <SelectField id="model-profile" v-model="parameterProfile" label="Parameter profile" :options="profileOptions" :error="errors.parameterProfile" />
      <TextField id="model-context" v-model="contextWindow" label="Context window (tokens)" type="number" min="1" inputmode="numeric" hint="Optional." :error="errors.contextWindow" />
      <FieldGroup legend="Capabilities" hint="What the model can do. Filled in automatically when you add it with Discover models.">
        <div class="flex flex-wrap gap-2">
          <ToggleChip v-for="f in featureOptions" :key="f" :label="featureLabel(f)" :model-value="features.includes(f)" @update:model-value="toggleFeature(f, $event)" />
        </div>
      </FieldGroup>
      <CheckField v-model="isEnabled" label="Enabled" description="Turn off to stop routing to this model." />
      <FieldGroup v-if="!model" legend="Price" hint="Optional. You can add it later.">
        <PriceFields v-model="priceDraft" :errors="priceErrors" id-prefix="model-price" />
      </FieldGroup>
    </form>

    <section v-if="model" class="mt-8 flex flex-col gap-3 border-t border-border pt-6" aria-labelledby="model-prices">
      <div class="flex items-center justify-between gap-3">
        <h3 id="model-prices" class="text-heading">Price history</h3>
        <UiButton size="sm" icon="add" @click="addPrice = true">Add price</UiButton>
      </div>
      <p class="text-small text-fg-3">USD per 1M tokens; audio in USD per minute. A dash means audio tokens are billed as text tokens.</p>
      <AsyncState :loading="pricesLoading" :error="pricesError" :empty="prices.length === 0" empty-text="No price yet. Usage of this model is not costed." @retry="loadPrices">
        <DataTable :columns="priceColumns" :rows="prices" :row-key="(p) => p.effectiveFrom" caption="Price history">
          <template #cell-from="{ row }">{{ formatDateTime(row.effectiveFrom) }}</template>
          <template #cell-input="{ row }">{{ formatUsd(row.inputPerMillionUsd) }}</template>
          <template #cell-cached="{ row }">{{ formatUsd(row.cachedInputPerMillionUsd) }}</template>
          <template #cell-output="{ row }">{{ formatUsd(row.outputPerMillionUsd) }}</template>
          <template #cell-audio="{ row }">{{ row.audioPerMinuteUsd ? formatUsd(row.audioPerMinuteUsd) : '—' }}</template>
          <template #cell-audioTokens="{ row }">
            {{ row.audioInputPerMillionUsd ? formatUsd(row.audioInputPerMillionUsd) : '—' }} / {{ row.audioOutputPerMillionUsd ? formatUsd(row.audioOutputPerMillionUsd) : '—' }}
          </template>
        </DataTable>
      </AsyncState>
    </section>

    <template #footer>
      <UiButton variant="primary" type="submit" form="model-form" :loading="busy">{{ model ? 'Save changes' : 'Create model' }}</UiButton>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      <UiButton v-if="model" class="ml-auto" variant="danger" @click="confirmDelete = true">Delete</UiButton>
    </template>

    <AddPriceDialog v-model:open="addPrice" :model="model" @saved="priceAdded" />
    <ConfirmDialog v-model:open="confirmDelete" title="Delete model?" confirm-label="Delete model" danger :action="remove">
      <span class="font-mono">{{ model?.name }}</span> is removed for good.
    </ConfirmDialog>
  </UiDrawer>
</template>
