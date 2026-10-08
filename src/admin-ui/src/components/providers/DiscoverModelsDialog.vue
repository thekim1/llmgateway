<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { DiscoveredModel, Provider } from '@/api/types'
import { useModelsStore } from '@/stores/models'
import { formatNumber, formatUsd } from '@/utils/format'
import { KIND_LABEL, featureLabel } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import AppIcon from '../ui/AppIcon.vue'
import InlineError from '../ui/InlineError.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'

const props = defineProps<{ open: boolean; provider: Provider | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const models = useModelsStore()
const loading = ref(false)
const adding = ref(false)
const error = ref<unknown>(null)
const found = ref<DiscoveredModel[] | null>(null)
const selected = ref<Set<string>>(new Set())
const filter = ref('')
const addedCount = ref<number | null>(null)

const visible = computed(() => {
  const q = filter.value.trim().toLowerCase()
  return (found.value ?? []).filter((m) => !q || m.id.toLowerCase().includes(q))
})
const selectable = computed(() => visible.value.filter((m) => !m.alreadyAdded))

async function discover(): Promise<void> {
  if (!props.provider) return
  loading.value = true
  error.value = null
  found.value = null
  addedCount.value = null
  selected.value = new Set()
  try {
    found.value = await api.providers.discoverModels(props.provider.id)
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

async function addSelected(): Promise<void> {
  if (!props.provider || !found.value) return
  adding.value = true
  error.value = null
  let count = 0
  try {
    for (const m of found.value.filter((x) => selected.value.has(x.id))) {
      await models.create({
        providerId: props.provider.id,
        name: m.id,
        upstreamModel: m.id,
        kind: m.kind,
        parameterProfile: m.parameterProfile,
        contextWindow: m.contextWindow,
        isEnabled: true,
        features: m.features,
        ...(m.price ? { price: { ...m.price } } : {}),
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
    :open="open"
    title="Discover models"
    :description="provider ? `Models offered by ${provider.name}. Listing them also tests the connection.` : undefined"
    @update:open="emit('update:open', $event)"
  >
    <div v-if="loading" class="text-fg-2" role="status">Contacting provider…</div>
    <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
    <p v-if="addedCount" class="text-ok" role="status">Added {{ addedCount }} {{ addedCount === 1 ? 'model' : 'models' }}.</p>

    <template v-if="found">
      <p class="flex items-center gap-2 text-ok"><AppIcon name="check_circle" />Connection works. {{ found.length }} models found.</p>
      <p v-if="found.length" class="text-small text-fg-3">
        Context size, features and prices are only filled in when the provider reports them. Most do not report prices, so check them before you route traffic.
      </p>
      <template v-if="found.length">
        <label class="flex flex-col gap-1 text-small">
          <span class="text-fg-2">Filter</span>
          <input v-model="filter" type="search" class="h-ctl rounded-input border border-border-input bg-surface px-3 text-fg" />
        </label>
        <label class="flex items-center gap-2 text-small">
          <input type="checkbox" :checked="selectable.length > 0 && selectable.every((m) => selected.has(m.id))" :disabled="!selectable.length" @change="toggleAll(($event.target as HTMLInputElement).checked)" />
          Select all shown ({{ selectable.length }})
        </label>
        <ul class="flex max-h-80 flex-col divide-y divide-border overflow-auto rounded-input border border-border">
          <li v-for="m in visible" :key="m.id">
            <label class="flex items-start gap-3 px-3 py-2" :class="m.alreadyAdded ? 'text-fg-3' : 'cursor-pointer hover:bg-hover'">
              <input
                type="checkbox"
                class="mt-1"
                :checked="m.alreadyAdded || selected.has(m.id)"
                :disabled="m.alreadyAdded"
                @change="toggle(m.id, ($event.target as HTMLInputElement).checked)"
              />
              <span class="flex min-w-0 flex-1 flex-col gap-0.5">
                <span class="break-all font-mono text-small">{{ m.id }}</span>
                <span class="flex flex-wrap items-center gap-x-3 gap-y-1 text-caption text-fg-3">
                  <span>{{ KIND_LABEL[m.kind] }}</span>
                  <span v-if="m.contextWindow">{{ formatNumber(m.contextWindow) }} tokens</span>
                  <span v-if="m.price">{{ formatUsd(m.price.inputPerMillionUsd) }} in / {{ formatUsd(m.price.outputPerMillionUsd) }} out per 1M</span>
                  <UiBadge v-for="f in m.features" :key="f" tone="neutral" :label="featureLabel(f)" />
                  <UiBadge v-if="m.alreadyAdded" tone="neutral" label="Already added" />
                </span>
              </span>
            </label>
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
