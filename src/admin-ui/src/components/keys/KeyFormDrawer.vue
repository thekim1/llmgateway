<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ATTACHMENT_POLICIES, CONTENT_GUARD_POLICIES, DATA_RESIDENCIES, PII_POLICIES, type AttachmentPolicy, type ContentGuardPolicy, type CreateKeyResponse, type DataResidency, type IntegrationRole, type PiiPolicy, type VirtualKey } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useModelOptions } from '@/composables/useModelOptions'
import { useAuthStore } from '@/stores/auth'
import { useIntegrationsStore } from '@/stores/integrations'
import { useKeysStore } from '@/stores/keys'
import { useProvidersStore } from '@/stores/providers'
import { useTeamsStore } from '@/stores/teams'
import { ASSIGNED_KEY_HINT, ATTACHMENT_HINT, ATTACHMENT_LABEL, ATTACHMENT_LIMIT_NOTE, CONTENT_GUARD_HINT, CONTENT_GUARD_LABEL, CREDENTIAL_RESIDENCY_HINT, INTEGRATION_ROLE, INTEGRATION_SCOPE, PII_HINT, PII_LABEL, PROVIDER_STORAGE_HINT, PROVIDER_TOOLS_HINT, REQUIRE_MAX_RESIDENCY_HINT, RESIDENCY } from '@/utils/labels'
import { dateInputToIso, isBlank, isoToDateInput, parseInteger } from '@/utils/validation'
import { addressListError, formatAddressList, parseAddressList } from '@/utils/addresses'
import { todayIso } from '@/utils/format'
import AddressListField from '@/components/ui/AddressListField.vue'
import RequestPolicyFields from '@/components/policy/RequestPolicyFields.vue'
import { emptyPolicyDraft, fromPolicyDraft, toPolicyDraft, type PolicyErrors } from '@/components/policy/policyDraft'
import AppIcon from '@/components/ui/AppIcon.vue'
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
const integrations = useIntegrationsStore()
const modelOptions = useModelOptions()

const ids = {
  teamId: 'key-team',
  integrationId: 'key-integration',
  integrationScopes: 'key-integration-scopes',
  name: 'key-name',
  description: 'key-description',
  expiresAt: 'key-expires',
  allowedResidencies: 'key-residency',
  piiPolicy: 'key-pii',
  attachmentPolicy: 'key-attachments',
  allowedModels: 'key-models',
  allowedProviders: 'key-providers',
  allowedIpRanges: 'key-addresses',
  requestsPerMinute: 'key-rpm',
  tokensPerMinute: 'key-tpm',
  maxConcurrentRequests: 'key-concurrent',
  requestPolicy: 'key-policy-max-output',
} as const
const { errors, formError, clear, validate, applyServerError } = useFormErrors(ids)

const values = reactive({
  teamId: '',
  name: '',
  description: '',
  residencies: [] as DataResidency[],
  piiPolicy: 'RerouteToOnPrem' as PiiPolicy,
  contentGuardPolicy: 'Off' as ContentGuardPolicy,
  attachmentPolicy: 'Allowed' as AttachmentPolicy,
  allowProviderTools: false,
  allowProviderStorage: false,
  requireMaxResidency: false,
  allowAll: true,
  models: [] as string[],
  allProviders: true,
  providerNames: [] as string[],
  addresses: '',
  requestsPerMinute: '60',
  tokensPerMinute: '',
  maxConcurrentRequests: '',
  expiresAt: '',
  isEnabled: true,
  integrationId: '',
  integrationRole: 'Assigned' as IntegrationRole,
  integrationScopes: [] as string[],
})
const piiOptions = PII_POLICIES.map((p) => ({ value: p, label: PII_LABEL[p] }))
const guardOptions = CONTENT_GUARD_POLICIES.map((p) => ({ value: p, label: CONTENT_GUARD_LABEL[p] }))
const attachmentOptions = ATTACHMENT_POLICIES.map((p) => ({ value: p, label: ATTACHMENT_LABEL[p] }))
const policy = ref(emptyPolicyDraft())
const policyErrors = ref<PolicyErrors>({})
const busy = ref(false)
const isEdit = computed(() => !!props.existing)

// Integrations (docs/integrations.md): set on create only. Any key manager can assign a key to an integration; only a
// gateway admin makes an integration's credential key, which belongs to the integration's owner team.
const integrationOptions = computed(() => [
  { value: '', label: 'No integration' },
  ...integrations.items.filter((i) => i.isActive).map((i) => ({ value: i.id, label: i.name })),
])
const chosenIntegration = computed(() => integrations.items.find((i) => i.id === values.integrationId) ?? null)
const roleOptions = (['Assigned', 'Credential'] as const).map((r) => ({ value: r, label: INTEGRATION_ROLE[r].label }))
const isCredential = computed(() => (isEdit.value ? props.existing?.integrationRole === 'Credential' : !!values.integrationId && values.integrationRole === 'Credential'))
const isAssigned = computed(() => (isEdit.value ? props.existing?.integrationRole === 'Assigned' : !!values.integrationId && values.integrationRole === 'Assigned'))
// A credential key's settings apply to every team the integration serves, so only gateway admins change it.
const lockedCredential = computed(() => isEdit.value && props.existing?.integrationRole === 'Credential' && !auth.isGatewayAdmin)
const scopeChoices = computed(() => {
  const known = Object.keys(INTEGRATION_SCOPE)
  return [...known, ...values.integrationScopes.filter((s) => !known.includes(s))]
})

function toggleScope(scope: string, on: boolean): void {
  values.integrationScopes = on ? [...values.integrationScopes, scope] : values.integrationScopes.filter((s) => s !== scope)
}

// Choosing the credential role puts the key in the owner team, the only team it may belong to.
watch([() => values.integrationRole, () => values.integrationId], () => {
  if (!isEdit.value && isCredential.value && chosenIntegration.value) values.teamId = chosenIntegration.value.ownerTeamId
})

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
  values.contentGuardPolicy = key?.contentGuardPolicy ?? 'Off'
  values.attachmentPolicy = key?.attachmentPolicy ?? 'Allowed'
  values.allowProviderTools = key?.allowProviderTools ?? false
  values.allowProviderStorage = key?.allowProviderStorage ?? false
  values.requireMaxResidency = key?.requireMaxResidency ?? false
  values.allowAll = key ? key.allowedModels.length === 0 : true
  values.models = [...(key?.allowedModels ?? [])]
  values.allProviders = key ? (key.allowedProviders ?? []).length === 0 : true
  values.providerNames = [...(key?.allowedProviders ?? [])]
  values.addresses = formatAddressList(key?.allowedIpRanges)
  values.requestsPerMinute = key ? (key.requestsPerMinute?.toString() ?? '') : '60'
  values.tokensPerMinute = key?.tokensPerMinute?.toString() ?? ''
  values.maxConcurrentRequests = key?.maxConcurrentRequests?.toString() ?? ''
  policy.value = toPolicyDraft(key?.requestPolicy)
  policyErrors.value = {}
  values.expiresAt = isoToDateInput(key?.expiresAt)
  values.isEnabled = key?.isEnabled ?? true
  values.integrationId = key?.integrationId ?? ''
  values.integrationRole = key?.integrationRole && key.integrationRole !== 'None' ? key.integrationRole : 'Assigned'
  values.integrationScopes = [...(key?.integrationScopes ?? [])]
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    reset()
    void teams.load()
    void modelOptions.load()
    if (!integrations.loaded && !integrations.loading) void integrations.load()
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
  if (busy.value || lockedCredential.value) return
  const ok = validate({
    teamId:
      (!isEdit.value && !values.teamId && 'Choose the team that owns this key.') ||
      (!isEdit.value && isCredential.value && chosenIntegration.value && values.teamId !== chosenIntegration.value.ownerTeamId &&
        `A credential key belongs to the integration’s owner team (${chosenIntegration.value.ownerTeamName ?? 'its team'}).`),
    name: isBlank(values.name) && 'Give the key a name so people can recognise it.',
    allowedModels: !values.allowAll && values.models.length === 0 && 'Select at least one model, or allow all models.',
    allowedProviders: canPickProviders.value && !values.allProviders && values.providerNames.length === 0 && 'Select at least one provider, or allow all providers.',
    allowedIpRanges: addressListError(parseAddressList(values.addresses)),
    requestsPerMinute: limitError(values.requestsPerMinute, 'Requests per minute'),
    tokensPerMinute: limitError(values.tokensPerMinute, 'Tokens per minute'),
    maxConcurrentRequests: limitError(values.maxConcurrentRequests, 'Requests at once'),
    expiresAt: expiryError(),
  })
  const parsedPolicy = fromPolicyDraft(policy.value)
  policyErrors.value = parsedPolicy.errors
  if (!ok || Object.keys(parsedPolicy.errors).length > 0) return
  const body = {
    name: values.name.trim(),
    description: isBlank(values.description) ? null : values.description.trim(),
    expiresAt: dateInputToIso(values.expiresAt),
    allowedModels: values.allowAll ? [] : [...values.models],
    allowedResidencies: [...values.residencies],
    ...(canPickProviders.value ? { allowedProviders: values.allProviders ? [] : [...values.providerNames] } : {}),
    allowedIpRanges: parseAddressList(values.addresses),
    piiPolicy: values.piiPolicy,
    contentGuardPolicy: values.contentGuardPolicy,
    attachmentPolicy: values.attachmentPolicy,
    allowProviderTools: values.allowProviderTools,
    allowProviderStorage: values.allowProviderStorage,
    requireMaxResidency: values.requireMaxResidency,
    requestsPerMinute: parseInteger(values.requestsPerMinute),
    tokensPerMinute: parseInteger(values.tokensPerMinute),
    maxConcurrentRequests: parseInteger(values.maxConcurrentRequests),
    requestPolicy: parsedPolicy.policy,
    ...(isCredential.value && auth.isGatewayAdmin ? { integrationScopes: [...values.integrationScopes] } : {}),
  }
  busy.value = true
  try {
    if (props.existing) {
      const key = await keys.update(props.existing.id, { ...body, isEnabled: values.isEnabled })
      emit('update:open', false)
      emit('saved', key)
    } else {
      const integration = values.integrationId ? { integrationId: values.integrationId, integrationRole: values.integrationRole } : {}
      const result = await keys.create({ ...body, ...integration, teamId: values.teamId })
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
    :description="isEdit ? 'Change the key’s access and limits.' : 'Create a key for an application. The secret is shown once; a key assigned to an integration has none.'"
    @update:open="emit('update:open', $event)"
  >
    <AsyncState :loading="teams.loading" :error="teams.error" :empty="false" @retry="teams.load()">
      <form id="key-form" class="flex flex-col gap-8" novalidate @submit.prevent="submit">
        <InlineError v-if="formError" :message="formError" />
        <p v-if="lockedCredential" class="m-0 flex items-start gap-2 text-small text-fg-2" role="note">
          <AppIcon name="info" :size="18" class="mt-px shrink-0" />
          This is {{ existing?.integrationName ?? 'an integration' }}’s credential key. Only gateway admins can change it; you can rotate or revoke it.
        </p>

        <FieldGroup legend="Basics">
          <SelectField
            :id="ids.teamId"
            v-model="values.teamId"
            label="Team"
            placeholder="Select a team"
            :options="teamOptions"
            :disabled="isEdit || (isCredential && !!chosenIntegration)"
            :error="errors.teamId"
            :hint="isEdit ? 'A key stays with the team that owns it.' : 'The team is billed for this key’s usage.'"
          />
          <TextField :id="ids.name" v-model="values.name" label="Name" autocomplete="off" :error="errors.name" hint="Shown in the key list and in usage reports." />
          <TextField :id="ids.description" v-model="values.description" label="Description (optional)" :error="errors.description" />
          <CheckField v-if="isEdit" v-model="values.isEnabled" label="Key is enabled" description="Turn off to pause the key without revoking it." />
        </FieldGroup>

        <FieldGroup v-if="!isEdit ? integrationOptions.length > 1 : !!existing?.integrationId" legend="Integration">
          <template v-if="!isEdit">
            <SelectField
              :id="ids.integrationId"
              v-model="values.integrationId"
              label="Used through (optional)"
              :options="integrationOptions"
              :error="errors.integrationId"
              hint="For a chatbot or knowledge base of another system, such as a RAG platform. Can’t be changed later."
            />
            <template v-if="values.integrationId">
              <div v-if="auth.isGatewayAdmin" class="flex flex-col gap-1.5">
                <span class="font-medium">Role</span>
                <SegmentedControl v-model="values.integrationRole" mode="radio" label="Role in the integration" :options="roleOptions" />
              </div>
              <span class="text-small text-fg-2">{{ INTEGRATION_ROLE[values.integrationRole].description }}</span>
            </template>
          </template>
          <p v-else class="m-0">
            Used through <strong>{{ existing?.integrationName ?? 'an integration' }}</strong> as
            {{ INTEGRATION_ROLE[existing?.integrationRole ?? 'None'].label.toLowerCase() }} key.
            <span class="block text-small text-fg-2">{{ INTEGRATION_ROLE[existing?.integrationRole ?? 'None'].description }}</span>
          </p>
          <p v-if="isAssigned" class="m-0 text-small text-fg-2">{{ ASSIGNED_KEY_HINT }}</p>
          <fieldset v-if="isCredential && auth.isGatewayAdmin" :id="ids.integrationScopes" class="m-0 flex min-w-0 flex-col gap-1 border-0 p-0" tabindex="-1">
            <legend class="mb-1.5 p-0 font-medium">Integration access</legend>
            <CheckField
              v-for="scope in scopeChoices"
              :key="scope"
              :label="INTEGRATION_SCOPE[scope]?.label ?? scope"
              :description="INTEGRATION_SCOPE[scope]?.description ?? scope"
              :model-value="values.integrationScopes.includes(scope)"
              @update:model-value="toggleScope(scope, $event)"
            />
            <span v-if="errors.integrationScopes" class="text-small text-danger">{{ errors.integrationScopes }}</span>
          </fieldset>
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
            <span v-if="isCredential" class="text-small text-fg-2">{{ CREDENTIAL_RESIDENCY_HINT }}</span>
            <span v-if="errors.allowedResidencies" class="text-small text-danger">{{ errors.allowedResidencies }}</span>
            <CheckField v-model="values.requireMaxResidency" label="Require residency header" :description="REQUIRE_MAX_RESIDENCY_HINT" />
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

          <div class="flex flex-col gap-1.5">
            <span class="font-medium">Content guard</span>
            <SegmentedControl v-model="values.contentGuardPolicy" mode="radio" label="Content guard" :options="guardOptions" />
            <span class="text-small text-fg-2">{{ CONTENT_GUARD_HINT[values.contentGuardPolicy] }}</span>
          </div>

          <div :id="ids.attachmentPolicy" class="flex flex-col gap-1.5" tabindex="-1">
            <span class="font-medium">Attached files</span>
            <SegmentedControl
              v-model="values.attachmentPolicy"
              mode="radio"
              label="Attached files"
              :options="attachmentOptions"
            />
            <span class="text-small text-fg-2">{{ ATTACHMENT_HINT[values.attachmentPolicy] }}</span>
            <p v-if="values.attachmentPolicy !== 'Allowed'" class="m-0 flex items-start gap-2 text-small text-fg-2">
              <AppIcon name="info" :size="18" class="mt-px shrink-0" />
              {{ ATTACHMENT_LIMIT_NOTE }}
            </p>
            <span v-if="errors.attachmentPolicy" class="text-small text-danger">{{ errors.attachmentPolicy }}</span>
          </div>

          <fieldset class="m-0 flex min-w-0 flex-col gap-1.5 border-0 p-0">
            <legend class="mb-1.5 p-0 font-medium">Provider features</legend>
            <CheckField v-model="values.allowProviderTools" label="Allow provider tools" :description="PROVIDER_TOOLS_HINT" />
            <CheckField v-model="values.allowProviderStorage" label="Allow provider storage" :description="PROVIDER_STORAGE_HINT" />
          </fieldset>

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

          <AddressListField
            :id="ids.allowedIpRanges"
            v-model="values.addresses"
            label="Allowed client addresses (optional)"
            placeholder="203.0.113.0/24&#10;2001:db8::10"
            :error="errors.allowedIpRanges"
            hint="One IP address or CIDR network per line. Leave empty to allow any address. Behind a reverse proxy this only works once a gateway admin has listed the proxy under Settings, Trusted proxies."
          />

        </FieldGroup>

        <FieldGroup legend="Limits" hint="Leave a field empty for no limit.">
          <div class="grid grid-cols-2 gap-4 max-md:grid-cols-1">
            <TextField :id="ids.requestsPerMinute" v-model="values.requestsPerMinute" label="Requests per minute" inputmode="numeric" :error="errors.requestsPerMinute" />
            <TextField :id="ids.tokensPerMinute" v-model="values.tokensPerMinute" label="Tokens per minute" inputmode="numeric" :error="errors.tokensPerMinute" />
            <TextField
              :id="ids.maxConcurrentRequests"
              v-model="values.maxConcurrentRequests"
              label="Requests at once"
              inputmode="numeric"
              hint="Requests in progress at the same time, across all gateway instances. Protects shared on-prem servers from one busy key."
              :error="errors.maxConcurrentRequests"
            />
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

        <RequestPolicyFields
          v-model="policy"
          id-prefix="key-policy"
          :errors="policyErrors"
          hint="Optional. On top of the team's policy: what you set here wins, the rest comes from the team."
        />
      </form>
    </AsyncState>
    <template #footer>
      <UiButton variant="primary" type="submit" form="key-form" :loading="busy" :disabled="lockedCredential">{{ isEdit ? 'Save key' : 'Create key' }}</UiButton>
      <UiButton :disabled="busy" @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
