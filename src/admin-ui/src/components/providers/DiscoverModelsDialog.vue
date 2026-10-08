<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { DiscoveredModel, Model, Provider } from '@/api/types'
import { useModelsStore } from '@/stores/models'
import { formatNumber, formatUsd } from '@/utils/format'
import { KIND_LABEL, MODEL_FEATURES, featureLabel } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import AppIcon from '../ui/AppIcon.vue'
import FilterSelect from '../ui/FilterSelect.vue'
import InlineError from '../ui/InlineError.vue'
import TextField from '../ui/TextField.vue'
import ToggleChip from '../ui/ToggleChip.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'
import { emptyPriceDraft, isPriceDraftEmpty, toNewPrice, validatePrice, type PriceDraft, type PriceErrors } from './priceDraft'

interface Draft {
  features: string[]
  price: PriceDraft
}

const props = defineProps<{ open: boolean; provider: Provider | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const ALL = 'all'
const models = useModelsStore()
const loading = ref(false)
const adding = ref(false)
const error = ref<unknown>(null)
const found = ref<DiscoveredModel[] | null>(null)
const selected = ref<Set<string>>(new Set())
const addedCount = ref<number | null>(null)

const filter = ref('')
const kind = ref(ALL)
const feature = ref(ALL)
const state = ref(ALL)
const sort = ref('name')

// Edits made in the list. New models apply them when added; models already added save them on request.
const drafts = ref<Record<string, Draft>>({})
const priceErrors = ref<Record<string, PriceErrors>>({})
const editing = ref<string | null>(null)
const saving = ref<string | null>(null)
const savedId = ref<string | null>(null)
const rowError = ref<{ id: string; error: unknown } | null>(null)

const existing = (m: DiscoveredModel): Model | undefined =>
  models.items.find((x) => x.providerId === props.provider?.id && x.upstreamModel === m.id)

const priceToDraft = (p: { inputPerMillionUsd: number; cachedInputPerMillionUsd: number; outputPerMillionUsd: number } | null | undefined): PriceDraft => ({
  ...emptyPriceDraft(),
  ...(p ? { input: String(p.inputPerMillionUsd), cached: String(p.cachedInputPerMillionUsd), output: String(p.outputPerMillionUsd) } : {}),
})

const featuresOf = (m: DiscoveredModel): string[] => drafts.value[m.id]?.features ?? existing(m)?.features ?? m.features
const priceOf = (m: DiscoveredModel) => existing(m)?.currentPrice ?? m.price

const kindOptions = [{ value: ALL, label: 'All kinds' }, ...Object.entries(KIND_LABEL).map(([value, label]) => ({ value, label }))]
const stateOptions = [
  { value: ALL, label: 'All' },
  { value: 'new', label: 'Not added' },
  { value: 'added', label: 'Already added' },
]
const sortOptions = [
  { value: 'name', label: 'Name' },
  { value: 'context', label: 'Largest context' },
  { value: 'priceAsc', label: 'Lowest price' },
  { value: 'priceDesc', label: 'Highest price' },
]
const featureOptions = computed(() => {
  const present = new Set((found.value ?? []).flatMap((m) => m.features))
  const known = MODEL_FEATURES.filter((f) => present.has(f))
  const other = [...present].filter((f) => !(MODEL_FEATURES as readonly string[]).includes(f)).sort()
  return [{ value: ALL, label: 'Any capability' }, ...[...known, ...other].map((f) => ({ value: f, label: featureLabel(f) }))]
})

const visible = computed(() => {
  const q = filter.value.trim().toLowerCase()
  const list = (found.value ?? []).filter((m) => {
    if (q && !m.id.toLowerCase().includes(q) && !featuresOf(m).some((f) => f.includes(q))) return false
    if (kind.value !== ALL && m.kind !== kind.value) return false
    if (feature.value !== ALL && !featuresOf(m).includes(feature.value)) return false
    if (state.value === 'new' && m.alreadyAdded) return false
    if (state.value === 'added' && !m.alreadyAdded) return false
    return true
  })
  const price = (m: DiscoveredModel) => priceOf(m)?.inputPerMillionUsd
  return list.sort((a, b) => {
    if (sort.value === 'context') return (b.contextWindow ?? -1) - (a.contextWindow ?? -1) || a.id.localeCompare(b.id)
    if (sort.value === 'priceAsc' || sort.value === 'priceDesc') {
      const x = price(a)
      const y = price(b)
      if (x == null || y == null) return x == null && y == null ? a.id.localeCompare(b.id) : x == null ? 1 : -1
      return (sort.value === 'priceAsc' ? x - y : y - x) || a.id.localeCompare(b.id)
    }
    return a.id.localeCompare(b.id, 'sv', { numeric: true })
  })
})
/** Capabilities the provider reports that the saved model lacks, plus a context size it has not recorded yet. */
function pendingUpdate(m: DiscoveredModel): { features: string[]; context: number | null } | null {
  const current = existing(m)
  if (!current) return null
  const features = m.features.filter((f) => !current.features.includes(f))
  const context = current.contextWindow == null ? m.contextWindow : null
  return features.length || context != null ? { features, context } : null
}
const updatable = computed(() => visible.value.filter((m) => pendingUpdate(m)))
const updating = ref(false)
const updatedCount = ref<number | null>(null)

/** Adds reported capabilities to saved models. Tags set by hand are kept; nothing is removed. */
async function updateFromProvider(list: DiscoveredModel[]): Promise<void> {
  updating.value = true
  rowError.value = null
  updatedCount.value = null
  let count = 0
  try {
    for (const m of list) {
      const current = existing(m)
      const pending = pendingUpdate(m)
      if (!current || !pending) continue
      const merged = [...new Set([...current.features, ...pending.features])].sort()
      await models.update(current.id, {
        name: current.name,
        upstreamModel: current.upstreamModel,
        kind: current.kind,
        parameterProfile: current.parameterProfile,
        contextWindow: pending.context ?? current.contextWindow,
        isEnabled: current.isEnabled,
        features: merged,
      })
      const draft = drafts.value[m.id]
      if (draft) draft.features = merged
      count++
    }
  } catch (e) {
    error.value = e
  } finally {
    updating.value = false
    updatedCount.value = count
  }
}

const selectable = computed(() => visible.value.filter((m) => !m.alreadyAdded))

async function discover(): Promise<void> {
  if (!props.provider) return
  loading.value = true
  error.value = null
  found.value = null
  addedCount.value = null
  updatedCount.value = null
  selected.value = new Set()
  drafts.value = {}
  priceErrors.value = {}
  editing.value = null
  try {
    // Needed to match discovered models with the ones already added, including their saved price and tags.
    const [list] = await Promise.all([api.providers.discoverModels(props.provider.id), models.load()])
    found.value = list
  } catch (e) {
    error.value = e
  } finally {
    loading.value = false
  }
}

watch(
  () => props.open,
  (open) => {
    if (open) {
      filter.value = ''
      kind.value = feature.value = state.value = ALL
      sort.value = 'name'
      void discover()
    }
  },
)

function toggle(id: string, on: boolean): void {
  const next = new Set(selected.value)
  if (on) next.add(id)
  else next.delete(id)
  selected.value = next
}
function toggleAll(on: boolean): void {
  const next = new Set(selected.value)
  for (const m of selectable.value) {
    if (on) next.add(m.id)
    else next.delete(m.id)
  }
  selected.value = next
}

function draftFor(m: DiscoveredModel): Draft {
  return (drafts.value[m.id] ??= { features: [...featuresOf(m)], price: priceToDraft(priceOf(m)) })
}
/** Read-only view of a draft for rendering; never creates one. */
const peek = (m: DiscoveredModel): Draft => drafts.value[m.id] ?? { features: [...featuresOf(m)], price: priceToDraft(priceOf(m)) }
function toggleEditor(m: DiscoveredModel): void {
  if (editing.value === m.id) {
    editing.value = null
    return
  }
  draftFor(m)
  editing.value = m.id
  savedId.value = null
  rowError.value = null
}
function setFeature(m: DiscoveredModel, f: string, on: boolean): void {
  const d = draftFor(m)
  d.features = on ? [...d.features, f] : d.features.filter((x) => x !== f)
}
function setPrice(m: DiscoveredModel, key: 'input' | 'cached' | 'output', value: string): void {
  draftFor(m).price[key] = value
}
const featureChoices = (m: DiscoveredModel): string[] => [...new Set([...MODEL_FEATURES, ...m.features, ...peek(m).features])]

/** Returns the price to save, null when none was entered, or undefined when the entered price is invalid. */
function resolvePrice(m: DiscoveredModel): ReturnType<typeof toNewPrice> | null | undefined {
  const d = drafts.value[m.id]
  if (!d || isPriceDraftEmpty(d.price)) return null
  const price = { ...d.price, from: '' }
  // Many providers charge the same for cached input, so an empty cached price is filled in rather than rejected.
  if (!price.cached.trim() && price.input.trim()) price.cached = price.input
  const errors = validatePrice(price)
  priceErrors.value = { ...priceErrors.value, [m.id]: errors }
  return Object.keys(errors).length ? undefined : toNewPrice(price)
}

const samePrice = (a: ReturnType<typeof toNewPrice>, b: Model['currentPrice']): boolean =>
  !!b && a.inputPerMillionUsd === b.inputPerMillionUsd && a.cachedInputPerMillionUsd === b.cachedInputPerMillionUsd && a.outputPerMillionUsd === b.outputPerMillionUsd

async function saveExisting(m: DiscoveredModel): Promise<void> {
  const current = existing(m)
  const d = drafts.value[m.id]
  if (!current || !d) return
  const price = resolvePrice(m)
  if (price === undefined) return
  saving.value = m.id
  rowError.value = null
  savedId.value = null
  try {
    await models.update(current.id, {
      name: current.name,
      upstreamModel: current.upstreamModel,
      kind: current.kind,
      parameterProfile: current.parameterProfile,
      contextWindow: current.contextWindow,
      isEnabled: current.isEnabled,
      features: d.features,
    })
    if (price && !samePrice(price, current.currentPrice)) await models.addPrice(current.id, price)
    savedId.value = m.id
  } catch (e) {
    rowError.value = { id: m.id, error: e }
  } finally {
    saving.value = null
  }
}

async function addSelected(): Promise<void> {
  if (!props.provider || !found.value) return
  const chosen = found.value.filter((x) => selected.value.has(x.id))
  const prices = new Map(chosen.map((m) => [m.id, resolvePrice(m)]))
  const bad = chosen.find((m) => prices.get(m.id) === undefined)
  if (bad) {
    editing.value = bad.id
    return
  }
  adding.value = true
  error.value = null
  let count = 0
  try {
    for (const m of chosen) {
      const price = prices.get(m.id) ?? (m.price ? { ...m.price } : null)
      await models.create({
        providerId: props.provider.id,
        name: m.id,
        upstreamModel: m.id,
        kind: m.kind,
        parameterProfile: m.parameterProfile,
        contextWindow: m.contextWindow,
        isEnabled: true,
        features: featuresOf(m),
        ...(price ? { price } : {}),
      })
      count++
      m.alreadyAdded = true
      selected.value.delete(m.id)
    }
  } catch (e) {
    error.value = e
  } finally {
    adding.value = false
    addedCount.value = count
    await models.load()
  }
}
</script>

<template>
  <UiDialog
    wide
    :open="open"
    title="Discover models"
    :description="provider ? `Models offered by ${provider.name}. Listing them also tests the connection.` : undefined"
    @update:open="emit('update:open', $event)"
  >
    <div v-if="loading" class="text-fg-2" role="status">Contacting provider…</div>
    <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
    <p v-if="addedCount" class="text-ok" role="status">Added {{ addedCount }} {{ addedCount === 1 ? 'model' : 'models' }}.</p>
    <p v-if="updatedCount !== null" class="text-ok" role="status">Updated {{ updatedCount }} {{ updatedCount === 1 ? 'model' : 'models' }} with capabilities from the provider.</p>

    <template v-if="found">
      <p class="flex items-center gap-2 text-ok"><AppIcon name="check_circle" />Connection works. {{ found.length }} models found.</p>
      <p v-if="found.length" class="text-small text-fg-3">
        Context size, capabilities and prices are only filled in when the provider reports them. Most do not report prices, so use Edit to correct capabilities and set prices before you route traffic.
      </p>
      <template v-if="found.length">
        <div class="flex flex-wrap items-center gap-3">
          <div class="relative min-w-[200px] flex-1">
            <label for="discover-search" class="sr-only">Search models</label>
            <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
            <input id="discover-search" v-model="filter" type="search" autocomplete="off" placeholder="Search by name or capability" class="field-input w-full !pl-10" />
          </div>
          <FilterSelect v-model="kind" label="Kind" :options="kindOptions" />
          <FilterSelect v-model="feature" label="Capability" :options="featureOptions" />
          <FilterSelect v-model="state" label="Show" :options="stateOptions" />
          <FilterSelect v-model="sort" label="Sort by" :options="sortOptions" />
        </div>
        <div v-if="updatable.length" class="flex flex-wrap items-center justify-between gap-3 rounded-input bg-accent-soft px-4 py-3 text-small">
          <span>The provider reports new capabilities for {{ updatable.length }} {{ updatable.length === 1 ? 'model' : 'models' }} you have added. Your own tags are kept.</span>
          <UiButton size="sm" :loading="updating" @click="updateFromProvider(updatable)">Update {{ updatable.length }} shown</UiButton>
        </div>
        <label class="flex items-center gap-2 text-small">
          <input type="checkbox" :checked="selectable.length > 0 && selectable.every((m) => selected.has(m.id))" :disabled="!selectable.length" @change="toggleAll(($event.target as HTMLInputElement).checked)" />
          Select all shown ({{ selectable.length }} of {{ visible.length }} not yet added)
        </label>
        <p v-if="visible.length === 0" class="py-4 text-center text-fg-2">No models match your filters.</p>
        <ul v-else class="flex max-h-[420px] flex-col divide-y divide-border overflow-auto rounded-input border border-border">
          <li v-for="m in visible" :key="m.id">
            <div class="flex items-start gap-3 px-3 py-2" :class="m.alreadyAdded ? 'text-fg-2' : 'hover:bg-hover'">
              <input
                :id="`discover-${m.id}`"
                type="checkbox"
                class="mt-1"
                :aria-label="`Select ${m.id}`"
                :checked="m.alreadyAdded || selected.has(m.id)"
                :disabled="m.alreadyAdded"
                @change="toggle(m.id, ($event.target as HTMLInputElement).checked)"
              />
              <span class="flex min-w-0 flex-1 flex-col gap-0.5">
                <span class="break-all font-mono text-small">{{ m.id }}</span>
                <span class="flex flex-wrap items-center gap-x-3 gap-y-1 text-caption text-fg-3">
                  <span>{{ KIND_LABEL[m.kind] }}</span>
                  <span v-if="m.contextWindow">{{ formatNumber(m.contextWindow) }} tokens</span>
                  <span v-if="priceOf(m)">{{ formatUsd(priceOf(m)!.inputPerMillionUsd) }} in / {{ formatUsd(priceOf(m)!.outputPerMillionUsd) }} out per 1M</span>
                  <span v-else>No price</span>
                  <UiBadge v-for="f in featuresOf(m)" :key="f" tone="neutral" :label="featureLabel(f)" />
                  <UiBadge v-if="m.alreadyAdded" tone="neutral" label="Already added" />
                  <span v-if="pendingUpdate(m)?.features.length" class="text-accent-ink">New from provider: {{ pendingUpdate(m)!.features.map(featureLabel).join(', ') }}</span>
                </span>
              </span>
              <UiButton v-if="pendingUpdate(m)" size="sm" :loading="updating" @click="updateFromProvider([m])">
                Update<span class="sr-only"> {{ m.id }} from provider</span>
              </UiButton>
              <UiButton size="sm" icon="edit" :aria-expanded="editing === m.id" @click="toggleEditor(m)">
                Edit<span class="sr-only"> {{ m.id }}</span>
              </UiButton>
            </div>

            <div v-if="editing === m.id" class="flex flex-col gap-4 bg-surface-2 px-4 py-4">
              <fieldset class="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
                <legend class="mb-1 text-small font-medium">Capabilities</legend>
                <div class="flex flex-wrap gap-2">
                  <ToggleChip v-for="f in featureChoices(m)" :key="f" :label="featureLabel(f)" :model-value="peek(m).features.includes(f)" @update:model-value="setFeature(m, f, $event)" />
                </div>
              </fieldset>
              <fieldset class="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
                <legend class="mb-1 text-small font-medium">Price (USD per 1M tokens)</legend>
                <div class="grid gap-3 sm:grid-cols-3">
                  <TextField :model-value="peek(m).price.input" label="Input" inputmode="decimal" :error="priceErrors[m.id]?.input" @update:model-value="setPrice(m, 'input', $event)" />
                  <TextField :model-value="peek(m).price.cached" label="Cached input" inputmode="decimal" hint="Defaults to input." :error="priceErrors[m.id]?.cached" @update:model-value="setPrice(m, 'cached', $event)" />
                  <TextField :model-value="peek(m).price.output" label="Output" inputmode="decimal" :error="priceErrors[m.id]?.output" @update:model-value="setPrice(m, 'output', $event)" />
                </div>
                <p v-if="m.alreadyAdded" class="text-caption text-fg-3">A changed price is added as a new price from now. Earlier requests keep their old price.</p>
                <p v-else class="text-caption text-fg-3">Leave empty to add the model without a price.</p>
              </fieldset>
              <InlineError v-if="rowError?.id === m.id" :message="problemMessage(rowError.error)" :lang="problemLang(rowError.error)" />
              <div v-if="m.alreadyAdded" class="flex items-center justify-end gap-3">
                <span v-if="savedId === m.id" class="text-small text-ok" role="status">Saved.</span>
                <UiButton variant="primary" size="sm" :loading="saving === m.id" @click="saveExisting(m)">Save changes</UiButton>
              </div>
              <p v-else class="text-caption text-fg-3">These changes are used when you add this model.</p>
            </div>
          </li>
        </ul>
      </template>
    </template>

    <template #actions>
      <UiButton @click="emit('update:open', false)">Close</UiButton>
      <UiButton v-if="error && !found" @click="discover">Try again</UiButton>
      <UiButton v-if="found?.length" variant="primary" :loading="adding" :disabled="selected.size === 0" @click="addSelected">
        Add {{ selected.size || '' }} selected
      </UiButton>
    </template>
  </UiDialog>
</template>
