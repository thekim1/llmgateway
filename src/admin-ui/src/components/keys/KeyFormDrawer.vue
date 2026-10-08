<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { DATA_RESIDENCIES, PII_POLICIES, type CreateKeyResponse, type DataResidency, type PiiPolicy, type VirtualKey } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useModelOptions } from '@/composables/useModelOptions'
import { useAuthStore } from '@/stores/auth'
import { useKeysStore } from '@/stores/keys'
import { useProvidersStore } from '@/stores/providers'
import { useTeamsStore } from '@/stores/teams'
import { PII_HINT, PII_LABEL, RESIDENCY } from '@/utils/labels'
import { dateInputToIso, isBlank, isoToDateInput, parseInteger } from '@/utils/validation'
import { todayIso } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import FieldGroup from '@/components/ui/FieldGroup.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import ToggleChip from '@/components/ui/ToggleChip.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'

const props = defineProps<{ open: boolean; existing?: VirtualKey | null }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'created', result: CreateKeyResponse): void
  (e: 'saved', key: VirtualKey): void
}>()

const keys = useKeysStore()
const auth = useAuthStore()
const providers = useProvidersStore()
const teams = useTeamsStore()
const modelOptions = useModelOptions()

const ids = {
  teamId: 'key-team',
  name: 'key-name',
  description: 'key-description',
  expiresAt: 'key-expires',
  allowedResidencies: 'key-residency',
  piiPolicy: 'key-pii',
  allowedModels: 'key-models',
  allowedProviders: 'key-providers',
  requestsPerMinute: 'key-rpm',
  tokensPerMinute: 'key-tpm',
} as const
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors(ids)

const values = reactive({
  teamId: '',
  name: '',
  description: '',
  residencies: [] as DataResidency[],
  piiPolicy: 'RerouteToOnPrem' as PiiPolicy,
  allowAll: true,
  models: [] as string[],
  allProviders: true,
  providerNames: [] as string[],
  requestsPerMinute: '60',
  tokensPerMinute: '',
  expiresAt: '',
  isEnabled: true,
})
const piiOptions = PII_POLICIES.map((p) => ({ value: p, label: PII_LABEL[p] }))
const busy = ref(false)
const isEdit = computed(() => !!props.existing)

const teamOptions = computed(() => {
  const list = teams.items.map((t) => ({ value: t.id, label: `${t.departmentName} · ${t.name}` }))
  const current = props.existing
  if (current && !list.some((o) => o.value === current.teamId)) list.push({ value: current.teamId, label: `${current.departmentName} · ${current.teamName}` })
  return list
})

// Only gateway-admin can list providers; for everyone else the key's providers are left unchanged.
const canPickProviders = computed(() => auth.isGatewayAdmin)
const providerChoices = computed(() => {
  const known = providers.items.map((p) => ({ name: p.name, label: p.displayName ?? p.name, residency: p.residency }))
  const extra = values.providerNames.filter((n) => !known.some((k) => k.name.toLowerCase() === n.toLowerCase())).map((name) => ({ name, label: name, residency: null }))
  return [...known, ...extra]
})

// Models offered for selection: only those served by the selected providers (all providers = no filter).
// Names without provider information (non-admin catalogue) are always shown. Models already on the key stay
// visible even if they are no longer in the catalogue.
const selectedProviders = computed(() => (!canPickProviders.value || values.allProviders ? null : values.providerNames.map((n) => n.toLowerCase())))
function servedBySelection(option: { providers: string[] }): boolean {
  const selected = selectedProviders.value
  return selected === null || option.providers.length === 0 || option.providers.some((p) => selected.includes(p.toLowerCase()))
}
const modelChoices = computed(() => {
  const known = modelOptions.options.value
  const shown = known.filter(servedBySelection)
  const extra = values.models.filter((m) => !known.some((o) => o.name === m)).map((name) => ({ name, source: 'model' as const, residencies: [] as DataResidency[], providers: [] as string[] }))
  return [...shown, ...extra]
})

// Changing the provider selection drops models the selected providers can no longer serve.
watch(selectedProviders, () => {
  const known = modelOptions.options.value
  values.models = values.models.filter((name) => {
    const option = known.find((o) => o.name === name)
    return !option || servedBySelection(option)
  })
})

function describeModel(source: 'route' | 'model', residencies: DataResidency[]): string {
  const where = residencies.map((r) => RESIDENCY[r].label).join(', ')
  return `${source === 'route' ? 'Route' : 'Model'}${where ? ` · ${where}` : ''}`
}

function reset(): void {
  clear()
  const key = props.existing
  values.teamId = key?.teamId ?? ''
  values.name = key?.name ?? ''
  values.description = key?.description ?? ''
  values.residencies = [...(key?.allowedResidencies ?? [])]
  values.piiPolicy = key?.piiPolicy ?? 'RerouteToOnPrem'
  values.allowAll = key ? key.allowedModels.length === 0 : true
  values.models = [...(key?.allowedModels ?? [])]
  values.allProviders = key ? (key.allowedProviders ?? []).length === 0 : true
  values.providerNames = [...(key?.allowedProviders ?? [])]
  values.requestsPerMinute = key ? (key.requestsPerMinute?.toString() ?? '') : '60'
  values.tokensPerMinute = key?.tokensPerMinute?.toString() ?? ''
  values.expiresAt = isoToDateInput(key?.expiresAt)
  values.isEnabled = key?.isEnabled ?? true
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    reset()
    void teams.load()
    void modelOptions.load()
    if (canPickProviders.value && !providers.loaded) void providers.load()
  },
  { immediate: true },
)

function toggleResidency(residency: DataResidency, on: boolean): void {
  values.residencies = on ? [...values.residencies, residency] : values.residencies.filter((r) => r !== residency)
}

function toggleModel(name: string, on: boolean): void {
  values.models = on ? [...values.models, name] : values.models.filter((m) => m !== name)
}

function toggleProvider(name: string, on: boolean): void {
  values.providerNames = on ? [...values.providerNames, name] : values.providerNames.filter((n) => n !== name)
}

function limitError(value: string, label: string): string | null {
  if (isBlank(value)) return null
  const parsed = parseInteger(value)
  return parsed === null || Number.isNaN(parsed) || parsed < 1 ? `${label} must be a whole number of 1 or more.` : null
}

function expiryError(): string | null {
  if (isBlank(values.expiresAt)) return null
  if (!dateInputToIso(values.expiresAt)) return 'Enter a valid expiry date.'
  return values.expiresAt < todayIso() ? 'Choose an expiry date that is today or later.' : null
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    teamId: !isEdit.value && !values.teamId && 'Choose the team that owns this key.',
    name: isBlank(values.name) && 'Give the key a name so people can recognise it.',
    allowedModels: !values.allowAll && values.models.length === 0 && 'Select at least one model, or allow all models.',
    allowedProviders: canPickProviders.value && !values.allProviders && values.providerNames.length === 0 && 'Select at least one provider, or allow all providers.',
    requestsPerMinute: limitError(values.requestsPerMinute, 'Requests per minute'),
    tokensPerMinute: limitError(values.tokensPerMinute, 'Tokens per minute'),
    expiresAt: expiryError(),
  })
  if (!ok) return
  const body = {
    name: values.name.trim(),
    description: isBlank(values.description) ? null : values.description.trim(),
    expiresAt: dateInputToIso(values.expiresAt),
    allowedModels: values.allowAll ? [] : [...values.models],
    allowedResidencies: [...values.residencies],
    ...(canPickProviders.value ? { allowedProviders: values.allProviders ? [] : [...values.providerNames] } : {}),
    piiPolicy: values.piiPolicy,
    requestsPerMinute: parseInteger(values.requestsPerMinute),
    tokensPerMinute: parseInteger(values.tokensPerMinute),
  }
  busy.value = true
  try {
    if (props.existing) {
      const key = await keys.update(props.existing.id, { ...body, isEnabled: values.isEnabled })
      emit('update:open', false)
      emit('saved', key)
    } else {
      const result = await keys.create({ ...body, teamId: values.teamId })
      emit('update:open', false)
      emit('created', result)
    }
  } catch (e) {
    applyServerError(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDrawer
    :open="open"
    wide
    :title="isEdit ? `Edit ${existing?.name ?? 'key'}` : 'New key'"
    :subtitle="existing?.prefix"
    :description="isEdit ? 'Change the key’s access and limits.' : 'Create a key for an application. The secret is shown once.'"
    @update:open="emit('update:open', $event)"
  >
    <AsyncState :loading="teams.loading" :error="teams.error" :empty="false" @retry="teams.load()">
      <form id="key-form" class="flex flex-col gap-8" novalidate @submit.prevent="submit">
        <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />

        <FieldGroup legend="Basics">
          <SelectField
            :id="ids.teamId"
            v-model="values.teamId"
            label="Team"
            placeholder="Select a team"
            :options="teamOptions"
            :disabled="isEdit"
            :error="errors.teamId"
            :hint="isEdit ? 'A key stays with the team that owns it.' : 'The team is billed for this key’s usage.'"
          />
          <TextField :id="ids.name" v-model="values.name" label="Name" autocomplete="off" :error="errors.name" hint="Shown in the key list and in usage reports." />
          <TextField :id="ids.description" v-model="values.description" label="Description (optional)" :error="errors.description" />
          <CheckField v-if="isEdit" v-model="values.isEnabled" label="Key is enabled" description="Turn off to pause the key without revoking it." />
        </FieldGroup>

        <FieldGroup legend="Access">
          <div :id="ids.allowedResidencies" class="flex flex-col gap-1.5" tabindex="-1">
            <span id="key-residency-label" class="font-medium">Data residency</span>
            <div class="flex flex-wrap gap-2" role="group" aria-labelledby="key-residency-label">
              <ToggleChip
                v-for="r in DATA_RESIDENCIES"
                :key="r"
                :label="RESIDENCY[r].label"
                :glyph="RESIDENCY[r].glyph"
                :model-value="values.residencies.includes(r)"
                @update:model-value="toggleResidency(r, $event)"
              />
            </div>
            <span class="text-small text-fg-3">{{ values.residencies.length === 0 ? 'No selection means any residency.' : 'Requests only reach providers in the selected locations.' }}</span>
            <span v-if="errors.allowedResidencies" class="text-small text-danger">{{ errors.allowedResidencies }}</span>
          </div>

          <div :id="ids.piiPolicy" class="flex flex-col gap-1.5" tabindex="-1">
            <span class="font-medium">PII policy</span>
            <SegmentedControl
              v-model="values.piiPolicy"
              mode="radio"
              label="PII policy"
              :options="piiOptions"
            />
            <span class="text-small text-fg-2">{{ PII_HINT[values.piiPolicy] }}</span>
            <span v-if="errors.piiPolicy" class="text-small text-danger">{{ errors.piiPolicy }}</span>
          </div>

          <div v-if="canPickProviders" :id="ids.allowedProviders" class="flex flex-col gap-2" tabindex="-1">
            <CheckField v-model="values.allProviders" label="Allow all providers" description="Includes providers added later." />
            <template v-if="!values.allProviders">
              <AsyncState :loading="providers.loading" :error="providers.error" :empty="false" @retry="providers.load()">
                <fieldset class="m-0 flex min-w-0 flex-col gap-1 border-0 p-0 pl-7">
                  <legend class="sr-only">Allowed providers</legend>
                  <p v-if="providerChoices.length === 0" class="text-small text-fg-3">No providers are available to choose from yet.</p>
                  <CheckField
                    v-for="option in providerChoices"
                    :key="option.name"
                    :label="option.label"
                    :description="option.residency ? `${option.name} · ${RESIDENCY[option.residency].label}` : option.name"
                    :model-value="values.providerNames.includes(option.name)"
                    @update:model-value="toggleProvider(option.name, $event)"
                  />
                </fieldset>
              </AsyncState>
            </template>
            <span class="text-small text-fg-3">Choose providers first. The model list below only shows models from the selected providers.</span>
            <span v-if="errors.allowedProviders" class="flex items-start gap-1 text-small text-danger">{{ errors.allowedProviders }}</span>
          </div>

          <div :id="ids.allowedModels" class="flex flex-col gap-2" tabindex="-1">
            <CheckField v-model="values.allowAll" label="Allow all models" description="Includes models added later." />
            <template v-if="!values.allowAll">
              <AsyncState :loading="modelOptions.loading.value" :error="modelOptions.error.value" :empty="false" @retry="modelOptions.load()">
                <fieldset class="m-0 flex min-w-0 flex-col gap-1 border-0 p-0 pl-7">
                  <legend class="sr-only">Allowed models</legend>
                  <p v-if="modelChoices.length === 0" class="text-small text-fg-3">{{ selectedProviders ? 'The selected providers have no models yet.' : 'No models are available to choose from yet.' }}</p>
                  <CheckField
                    v-for="option in modelChoices"
                    :key="option.name"
                    :label="option.name"
                    :description="describeModel(option.source, option.residencies)"
                    :model-value="values.models.includes(option.name)"
                    @update:model-value="toggleModel(option.name, $event)"
                  />
                </fieldset>
              </AsyncState>
            </template>
            <span v-if="errors.allowedModels" class="flex items-start gap-1 text-small text-danger">{{ errors.allowedModels }}</span>
          </div>

        </FieldGroup>

        <FieldGroup legend="Limits" hint="Leave a field empty for no limit.">
          <div class="grid grid-cols-2 gap-4 max-md:grid-cols-1">
            <TextField :id="ids.requestsPerMinute" v-model="values.requestsPerMinute" label="Requests per minute" inputmode="numeric" :error="errors.requestsPerMinute" />
            <TextField :id="ids.tokensPerMinute" v-model="values.tokensPerMinute" label="Tokens per minute" inputmode="numeric" :error="errors.tokensPerMinute" />
          </div>
          <TextField
            :id="ids.expiresAt"
            v-model="values.expiresAt"
            type="date"
            label="Expiry date"
            :min="todayIso()"
            :error="errors.expiresAt"
            hint="The key stops working at the end of this day. Leave empty for no expiry."
          />
        </FieldGroup>
      </form>
    </AsyncState>
    <template #footer>
      <UiButton variant="primary" type="submit" form="key-form" :loading="busy">{{ isEdit ? 'Save key' : 'Create key' }}</UiButton>
      <UiButton :disabled="busy" @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
