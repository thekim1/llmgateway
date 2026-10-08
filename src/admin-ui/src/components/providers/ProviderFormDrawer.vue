<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { DataResidency, Provider, ProviderAuthMode, ProviderCapability, ProviderType } from '@/api/types'
import { DATA_RESIDENCIES, PROVIDER_AUTH_MODES, PROVIDER_CAPABILITIES, PROVIDER_TYPES } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useProvidersStore } from '@/stores/providers'
import { useUiStore } from '@/stores/ui'
import { AUTH_MODE_LABEL, CAPABILITY_LABEL, PROVIDER_TYPE_LABEL, RESIDENCY, plural } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import CheckField from '../ui/CheckField.vue'
import ConfirmDialog from '../ui/ConfirmDialog.vue'
import FieldGroup from '../ui/FieldGroup.vue'
import InlineError from '../ui/InlineError.vue'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'
import ToggleChip from '../ui/ToggleChip.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiDrawer from '../ui/UiDrawer.vue'
import DiscoverModelsDialog from './DiscoverModelsDialog.vue'

const discoverOpen = ref(false)
const props = defineProps<{ open: boolean; provider: Provider | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'deleted'): void }>()

const providers = useProvidersStore()
const ui = useUiStore()
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors({
  name: 'prov-name',
  displayName: 'prov-display',
  type: 'prov-type',
  baseUrl: 'prov-url',
  authMode: 'prov-auth',
  credential: 'prov-credential',
  residency: 'prov-residency',
  capabilities: 'prov-capabilities',
  timeoutSeconds: 'prov-timeout',
})

const name = ref('')
const displayName = ref('')
const type = ref<ProviderType>('OpenAICompatible')
const baseUrl = ref('')
const authMode = ref<ProviderAuthMode>('None')
const credential = ref('')
const clearCredential = ref(false)
const residency = ref<DataResidency>('OnPrem')
const capabilities = ref<ProviderCapability[]>(['ChatCompletions', 'Streaming'])
const timeoutSeconds = ref('120')
const isEnabled = ref(true)
const busy = ref(false)
const drainBusy = ref(false)
const drainError = ref<unknown>(null)
const confirmDelete = ref(false)
const confirmDrain = ref(false)

const typeOptions = PROVIDER_TYPES.map((v) => ({ value: v, label: PROVIDER_TYPE_LABEL[v] }))
const authOptions = PROVIDER_AUTH_MODES.map((v) => ({ value: v, label: AUTH_MODE_LABEL[v] }))
const residencyOptions = DATA_RESIDENCIES.map((v) => ({ value: v, label: `${RESIDENCY[v].glyph} ${RESIDENCY[v].label}` }))
const deleteBlocked = computed(() => (props.provider?.deploymentCount ?? 0) > 0)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    const p = props.provider
    name.value = p?.name ?? ''
    displayName.value = p?.displayName ?? ''
    type.value = p?.type ?? 'OpenAICompatible'
    baseUrl.value = p?.baseUrl ?? ''
    authMode.value = p?.authMode ?? 'None'
    credential.value = ''
    clearCredential.value = false
    residency.value = p?.residency ?? 'OnPrem'
    capabilities.value = p ? [...p.capabilities] : ['ChatCompletions', 'Streaming']
    timeoutSeconds.value = String(p?.timeoutSeconds ?? 120)
    isEnabled.value = p?.isEnabled ?? true
    drainError.value = null
  },
  { immediate: true },
)

function toggleCapability(capability: ProviderCapability, on: boolean): void {
  capabilities.value = on ? [...capabilities.value, capability] : capabilities.value.filter((c) => c !== capability)
}

function urlProblem(): string | false {
  if (!baseUrl.value.trim()) return 'Enter the base URL of the provider.'
  try {
    const url = new URL(baseUrl.value.trim())
    return url.protocol === 'http:' || url.protocol === 'https:' ? false : 'The base URL must start with http:// or https://.'
  } catch {
    return 'Enter a full URL, for example https://api.example.com/v1.'
  }
}

function timeoutProblem(): string | false {
  const value = Number(timeoutSeconds.value)
  return timeoutSeconds.value.trim() !== '' && Number.isFinite(value) && value >= 1 && value <= 900 ? false : 'Enter a timeout between 1 and 900 seconds.'
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    name: !name.value.trim() && 'Give the provider a name.',
    baseUrl: urlProblem(),
    timeoutSeconds: timeoutProblem(),
  })
  if (!ok) return
  busy.value = true
  try {
    const body = {
      name: name.value.trim(),
      displayName: displayName.value.trim() || null,
      type: type.value,
      baseUrl: baseUrl.value.trim(),
      authMode: authMode.value,
      residency: residency.value,
      capabilities: capabilities.value,
      timeoutSeconds: Number(timeoutSeconds.value),
      isEnabled: isEnabled.value,
      ...(clearCredential.value ? { credential: '' } : credential.value ? { credential: credential.value } : {}),
    }
    if (props.provider) await providers.update(props.provider.id, body)
    else await providers.create(body)
    ui.notify('Provider saved')
    emit('update:open', false)
  } catch (e) {
    applyServerError(e)
  } finally {
    busy.value = false
  }
}

async function setDrained(drained: boolean): Promise<void> {
  if (!props.provider) return
  drainBusy.value = true
  drainError.value = null
  try {
    await providers.setDrained(props.provider.id, drained)
    ui.notify(drained ? 'Provider drained' : 'Provider resumed')
  } catch (e) {
    drainError.value = e
  } finally {
    drainBusy.value = false
  }
}

async function remove(): Promise<void> {
  if (!props.provider) return
  await providers.remove(props.provider.id)
  ui.notify('Provider deleted')
  emit('deleted')
  emit('update:open', false)
}
</script>

<template>
  <UiDrawer wide :open="open" :title="provider ? 'Edit provider' : 'New provider'" :subtitle="provider?.name" @update:open="emit('update:open', $event)">
    <template v-if="provider" #badge>
      <span class="flex flex-wrap gap-1.5">
        <UiBadge :tone="provider.isEnabled ? 'ok' : 'neutral'" :label="provider.isEnabled ? 'Enabled' : 'Disabled'" />
        <UiBadge v-if="provider.isDrained" tone="neutral" label="Drained" />
      </span>
    </template>
    <form id="provider-form" class="flex flex-col gap-5" novalidate @submit.prevent="submit">
      <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />
      <TextField id="prov-name" v-model="name" label="Name" mono :error="errors.name" hint="Shown in routes and reports." />
      <TextField id="prov-display" v-model="displayName" label="Display name" hint="Optional." :error="errors.displayName" />
      <SelectField id="prov-type" v-model="type" label="Type" :options="typeOptions" :error="errors.type" />
      <TextField id="prov-url" v-model="baseUrl" label="Base URL" type="url" inputmode="url" mono :error="errors.baseUrl" />
      <SelectField id="prov-auth" v-model="authMode" label="Authentication" :options="authOptions" :error="errors.authMode" />
      <div class="flex flex-col gap-2">
        <TextField
          id="prov-credential"
          v-model="credential"
          label="Credential"
          type="password"
          autocomplete="new-password"
          :disabled="clearCredential"
          :error="errors.credential"
          :hint="provider?.hasCredential ? 'A credential is stored. Leave empty to keep it. It is never shown again.' : 'Write-only. It is never shown again.'"
        />
        <CheckField v-if="provider?.hasCredential" v-model="clearCredential" label="Remove stored credential" />
      </div>
      <SelectField id="prov-residency" v-model="residency" label="Residency" :options="residencyOptions" :error="errors.residency" :hint="RESIDENCY[residency].description" />
      <FieldGroup legend="Capabilities">
        <div id="prov-capabilities" class="flex flex-wrap gap-2">
          <ToggleChip
            v-for="c in PROVIDER_CAPABILITIES"
            :key="c"
            :label="CAPABILITY_LABEL[c]"
            :model-value="capabilities.includes(c)"
            @update:model-value="toggleCapability(c, $event)"
          />
        </div>
      </FieldGroup>
      <TextField id="prov-timeout" v-model="timeoutSeconds" label="Timeout (seconds)" type="number" min="1" max="900" inputmode="numeric" :error="errors.timeoutSeconds" />
      <CheckField v-model="isEnabled" label="Enabled" description="Turn off to stop routing to this provider." />
    </form>

    <section v-if="provider" class="mt-8 flex flex-col gap-3 border-t border-border pt-6" aria-labelledby="prov-discover">
      <h3 id="prov-discover" class="text-heading">Models</h3>
      <p class="text-fg-2">Fetch the models this provider offers and add them in one go. This also tests the connection with the saved credential.</p>
      <div><UiButton icon="search" @click="discoverOpen = true">Discover models</UiButton></div>
    </section>

    <section v-if="provider" class="mt-8 flex flex-col gap-3 border-t border-border pt-6" aria-labelledby="prov-traffic">
      <h3 id="prov-traffic" class="text-heading">Traffic</h3>
      <p class="text-fg-2">
        {{ provider.isDrained ? 'This provider is drained. Routes skip it.' : 'This provider receives requests from the routes that use it.' }}
      </p>
      <InlineError v-if="drainError" :message="problemMessage(drainError)" :lang="problemLang(drainError)" />
      <div>
        <UiButton v-if="provider.isDrained" icon="play_arrow" :loading="drainBusy" @click="setDrained(false)">Resume traffic</UiButton>
        <UiButton v-else icon="pause" @click="confirmDrain = true">Drain provider</UiButton>
      </div>
    </section>

    <template #footer>
      <UiButton variant="primary" type="submit" form="provider-form" :loading="busy">{{ provider ? 'Save changes' : 'Create provider' }}</UiButton>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      <template v-if="provider">
        <span class="ml-auto flex flex-col items-end gap-1">
          <UiButton variant="danger" :disabled="deleteBlocked" :aria-describedby="deleteBlocked ? 'prov-delete-reason' : undefined" @click="confirmDelete = true">Delete</UiButton>
          <span v-if="deleteBlocked" id="prov-delete-reason" class="text-caption text-fg-3">Has {{ plural(provider.deploymentCount, 'model') }}. Delete them first.</span>
        </span>
      </template>
    </template>

    <DiscoverModelsDialog v-model:open="discoverOpen" :provider="provider" />
    <ConfirmDialog v-model:open="confirmDelete" title="Delete provider?" confirm-label="Delete provider" danger :action="remove">
      <span class="font-mono">{{ provider?.name }}</span> is removed for good.
    </ConfirmDialog>
    <ConfirmDialog
      v-model:open="confirmDrain"
      title="Drain provider?"
      consequence="Routes stop sending new requests to this provider until you resume it."
      confirm-label="Drain provider"
      :action="() => setDrained(true)"
    />
  </UiDrawer>
</template>
