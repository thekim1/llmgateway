<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { DATA_RESIDENCIES, MCP_AUTH_TYPES, type DataResidency, type McpAuthType, type McpServer, type McpServerRequest, type McpTestResult } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useAuthStore } from '@/stores/auth'
import { useDepartmentsStore } from '@/stores/departments'
import { toRequest, useMcpStore } from '@/stores/mcp'
import { useUiStore } from '@/stores/ui'
import { MCP_AUTH_LABEL, MCP_STATUS, RESIDENCY, plural } from '@/utils/labels'
import { forwardHeadersProblem, parseHeaderList, slugProblem, staticHeadersProblem } from '@/utils/mcp'
import { problemLang, problemMessage } from '@/utils/problem'
import { isBlank, isValidUrl, parseInteger } from '@/utils/validation'
import CheckField from '../ui/CheckField.vue'
import ConfirmDialog from '../ui/ConfirmDialog.vue'
import FieldGroup from '../ui/FieldGroup.vue'
import FormField from '../ui/FormField.vue'
import InlineError from '../ui/InlineError.vue'
import SegmentedControl from '../ui/SegmentedControl.vue'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiDrawer from '../ui/UiDrawer.vue'
import McpToolsPanel from './McpToolsPanel.vue'

type Tab = 'tools' | 'settings'

const props = defineProps<{ open: boolean; server: McpServer | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'created', server: McpServer): void }>()

const mcp = useMcpStore()
const auth = useAuthStore()
const departments = useDepartmentsStore()
const ui = useUiStore()

const ids = {
  slug: 'mcp-slug',
  displayName: 'mcp-display',
  description: 'mcp-description',
  url: 'mcp-url',
  residency: 'mcp-residency',
  ownerDepartmentId: 'mcp-owner',
  authType: 'mcp-auth',
  credential: 'mcp-credential',
  forwardHeaders: 'mcp-forward',
  timeoutSeconds: 'mcp-timeout',
  maxResultBytes: 'mcp-max-result',
} as const
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors(ids)

const tab = ref<Tab>('settings')
const slug = ref('')
const displayName = ref('')
const description = ref('')
const url = ref('')
const residency = ref<DataResidency>('OnPrem')
const owner = ref('')
const authType = ref<McpAuthType>('None')
const replaceCredential = ref(false)
const headers = ref<{ name: string; value: string }[]>([])
const tokenUrl = ref('')
const clientId = ref('')
const clientSecret = ref('')
const scope = ref('')
const forwardHeaders = ref('')
const timeoutSeconds = ref('30')
const maxResultKib = ref('1024')
const autoApprove = ref(false)
const isEnabled = ref(true)

const busy = ref(false)
const testBusy = ref(false)
const testResult = ref<McpTestResult | null>(null)
const testError = ref<unknown>(null)
const confirmDelete = ref(false)
const confirmEnabled = ref(false)

const isNew = computed(() => !props.server)
const readOnly = computed(() => !!props.server && !props.server.canManage)
const slugTouched = ref(false)
const slugLive = computed(() => (slugTouched.value ? slugProblem(slug.value.trim()) : null))

const tabs = computed(() => [
  { value: 'tools' as Tab, label: 'Tools', count: props.server?.toolCount },
  { value: 'settings' as Tab, label: 'Settings' },
])

const residencyOptions = DATA_RESIDENCIES.map((v) => ({ value: v, label: `${RESIDENCY[v].glyph} ${RESIDENCY[v].label}` }))
const authOptions = MCP_AUTH_TYPES.map((v) => ({ value: v, label: MCP_AUTH_LABEL[v] }))
const ownerOptions = computed(() => [
  ...(auth.isGatewayAdmin || props.server?.ownerDepartmentId === null ? [{ value: '', label: 'Global (managed by gateway admins)' }] : []),
  ...departments.items.map((d) => ({ value: d.id, label: d.name })),
])

function origin(value: string): string | null {
  try {
    return new URL(value).origin
  } catch {
    return null
  }
}
/** The API keeps a stored credential only for the same scheme, host and port, so it is never sent to a host its author did not choose. */
const urlMoved = computed(() => !!props.server && origin(props.server.url) !== origin(url.value))
/** The stored credential stays unless the auth type or the URL's host changes, or the admin chooses to replace it. */
const storedCredentialUsable = computed(() => !!props.server?.hasCredential && props.server.authType === authType.value && !urlMoved.value)
const credentialInputs = computed(() => authType.value !== 'None' && (!storedCredentialUsable.value || replaceCredential.value))
const credentialState = computed(() => {
  if (authType.value === 'None') return props.server?.hasCredential ? 'Saving removes the stored credential.' : 'The server is called without credentials.'
  if (storedCredentialUsable.value) return 'Set. It is stored encrypted and never shown again.'
  if (props.server?.hasCredential && props.server.authType === authType.value) return 'The stored credential is not sent to a new host. Enter credentials for this address.'
  if (props.server?.hasCredential) return 'Not set for this authentication type. Enter new credentials.'
  return 'Not set. Credentials are write-only: they are stored encrypted and never shown again.'
})

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    fill(props.server)
    tab.value = props.server ? 'tools' : 'settings'
    if (!departments.loaded) void departments.load()
  },
  { immediate: true },
)

function fill(s: McpServer | null): void {
  slug.value = s?.slug ?? ''
  slugTouched.value = false
  displayName.value = s?.displayName ?? ''
  description.value = s?.description ?? ''
  url.value = s?.url ?? ''
  residency.value = s?.residency ?? 'OnPrem'
  owner.value = s ? (s.ownerDepartmentId ?? '') : auth.isGatewayAdmin ? '' : (departments.items[0]?.id ?? '')
  authType.value = s?.authType ?? 'None'
  replaceCredential.value = false
  headers.value = [{ name: '', value: '' }]
  tokenUrl.value = clientId.value = clientSecret.value = scope.value = ''
  forwardHeaders.value = (s?.forwardHeaders ?? []).join('\n')
  timeoutSeconds.value = String(s?.timeoutSeconds ?? 30)
  maxResultKib.value = String(Math.round((s?.maxResultBytes ?? 1024 * 1024) / 1024))
  autoApprove.value = s?.autoApproveNewTools ?? false
  isEnabled.value = s?.isEnabled ?? true
  testResult.value = null
  testError.value = null
}

// A department admin's departments arrive after the drawer opens.
watch(
  () => departments.items,
  (items) => {
    if (isNew.value && !auth.isGatewayAdmin && !owner.value) owner.value = items[0]?.id ?? ''
  },
)

function addHeader(): void {
  headers.value = [...headers.value, { name: '', value: '' }]
}

function removeHeader(index: number): void {
  headers.value = headers.value.filter((_, i) => i !== index)
}

function urlProblem(value: string, what: string): string | false {
  if (isBlank(value)) return `Enter the ${what}.`
  if (!isValidUrl(value.trim())) return 'Enter a full URL that starts with https:// (http:// only for on-prem servers).'
  return value.trim().startsWith('http://') && residency.value !== 'OnPrem' ? 'Plain http:// is only allowed for on-prem servers.' : false
}

function credentialProblem(): string | false {
  if (!credentialInputs.value) return false
  if (authType.value === 'StaticHeaders') return staticHeadersProblem(headers.value) ?? false
  if (isBlank(clientId.value) || isBlank(clientSecret.value)) return 'Enter the client ID and client secret.'
  return urlProblem(tokenUrl.value, 'token URL')
}

function rangeProblem(value: string, min: number, max: number, label: string): string | false {
  const parsed = parseInteger(value)
  return parsed !== null && !Number.isNaN(parsed) && parsed >= min && parsed <= max ? false : `${label} must be a whole number from ${min} to ${max}.`
}

function body(): McpServerRequest {
  const request: McpServerRequest = {
    slug: slug.value.trim(),
    displayName: displayName.value.trim(),
    description: isBlank(description.value) ? null : description.value.trim(),
    url: url.value.trim(),
    residency: residency.value,
    authType: authType.value,
    isEnabled: props.server ? props.server.isEnabled : isEnabled.value,
    ownerDepartmentId: owner.value || null,
    forwardHeaders: parseHeaderList(forwardHeaders.value),
    timeoutSeconds: Number(timeoutSeconds.value),
    maxResultBytes: Number(maxResultKib.value) * 1024,
    autoApproveNewTools: autoApprove.value,
  }
  if (credentialInputs.value) {
    request.credential = authType.value === 'StaticHeaders'
      ? { headers: Object.fromEntries(headers.value.map((h) => [h.name.trim(), h.value])) }
      : { tokenUrl: tokenUrl.value.trim(), clientId: clientId.value.trim(), clientSecret: clientSecret.value, scope: isBlank(scope.value) ? null : scope.value.trim() }
  }
  return request
}

function check(): boolean {
  slugTouched.value = true
  return validate({
    slug: slugProblem(slug.value.trim()),
    displayName: isBlank(displayName.value) && 'Give the server a display name.',
    url: urlProblem(url.value, 'server URL'),
    ownerDepartmentId: !auth.isGatewayAdmin && !owner.value && 'Choose the department that owns the server.',
    credential: credentialProblem(),
    forwardHeaders: forwardHeadersProblem(parseHeaderList(forwardHeaders.value)) ?? false,
    timeoutSeconds: rangeProblem(timeoutSeconds.value, 1, 900, 'The timeout'),
    maxResultBytes: rangeProblem(maxResultKib.value, 1, 16384, 'The result limit'),
  })
}

async function submit(): Promise<void> {
  if (busy.value || readOnly.value || !check()) return
  busy.value = true
  try {
    if (props.server) {
      await mcp.update(props.server.id, body())
      ui.notify('MCP server saved')
      emit('update:open', false)
    } else {
      const created = await mcp.create(body())
      ui.notify('MCP server created. Sync it to discover its tools.')
      emit('created', created)
      tab.value = 'tools'
    }
  } catch (e) {
    applyServerError(e)
  } finally {
    busy.value = false
  }
}

async function testConnection(): Promise<void> {
  if (testBusy.value || !check()) return
  testBusy.value = true
  testResult.value = null
  testError.value = null
  try {
    testResult.value = await mcp.test(props.server?.id ?? null, body())
  } catch (e) {
    testError.value = e
    applyServerError(e)
  } finally {
    testBusy.value = false
  }
}

async function setEnabled(): Promise<void> {
  if (!props.server) return
  await mcp.update(props.server.id, toRequest(props.server, { isEnabled: !props.server.isEnabled }))
  ui.notify(props.server.isEnabled ? 'MCP server enabled' : 'MCP server disabled')
}

async function remove(): Promise<void> {
  if (!props.server) return
  await mcp.remove(props.server.id)
  ui.notify('MCP server deleted')
  emit('update:open', false)
}
</script>

<template>
  <UiDrawer wide :open="open" :title="server ? server.displayName : 'New MCP server'" :subtitle="server?.slug" @update:open="emit('update:open', $event)">
    <template v-if="server" #badge>
      <span class="flex flex-wrap gap-1.5">
        <UiBadge :tone="server.isEnabled ? 'ok' : 'neutral'" :label="server.isEnabled ? 'Enabled' : 'Disabled'" />
        <UiBadge v-if="server.lastCheckedAt" :tone="MCP_STATUS[server.status].tone" :label="MCP_STATUS[server.status].label" />
        <UiBadge v-if="!server.ownerDepartmentId" tone="neutral" label="Global" />
      </span>
    </template>

    <p v-if="readOnly" class="mb-5 text-small text-fg-2">Global servers and their tools are managed by gateway administrators. You can grant this server to your keys.</p>
    <SegmentedControl v-if="server" v-model="tab" label="Show" :options="tabs" class="mb-6" />

    <McpToolsPanel v-if="server && tab === 'tools'" :server="server" />

    <template v-else>
      <form id="mcp-form" class="flex flex-col gap-8" novalidate @submit.prevent="submit">
        <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />
        <fieldset class="m-0 flex min-w-0 flex-col gap-8 border-0 p-0" :disabled="readOnly">
          <FieldGroup legend="Server">
            <TextField
              :id="ids.slug"
              v-model="slug"
              label="Short name"
              mono
              :error="errors.slug ?? slugLive"
              :hint="`Tools appear as ${slug.trim() || 'name'}__tool on /mcp, and the server alone at /mcp/${slug.trim() || 'name'}.`"
              @update:model-value="slugTouched = true"
            />
            <TextField :id="ids.displayName" v-model="displayName" label="Display name" :error="errors.displayName" />
            <TextField :id="ids.description" v-model="description" label="Description (optional)" :error="errors.description" />
            <TextField
              :id="ids.url"
              v-model="url"
              label="URL"
              type="url"
              inputmode="url"
              mono
              :error="errors.url"
              hint="The server’s Streamable HTTP endpoint, for example https://diarium.internal/mcp. Link-local and cloud metadata addresses are refused; internal addresses need a gateway administrator."
            />
            <SelectField :id="ids.residency" v-model="residency" label="Residency" :options="residencyOptions" :error="errors.residency" :hint="RESIDENCY[residency].description" />
            <SelectField
              :id="ids.ownerDepartmentId"
              v-model="owner"
              label="Owner"
              :options="ownerOptions"
              :error="errors.ownerDepartmentId"
              hint="The department’s administrators manage the server and approve its tools. Global servers are managed by gateway administrators."
            />
          </FieldGroup>

          <FieldGroup legend="Authentication" hint="How the gateway authenticates to the server. A caller’s own key is never sent on.">
            <SelectField :id="ids.authType" v-model="authType" label="Authentication" :options="authOptions" :error="errors.authType" />
            <p class="m-0 text-small text-fg-2" data-testid="credential-state"><span class="font-medium">Credential:</span> {{ credentialState }}</p>
            <CheckField v-if="storedCredentialUsable" v-model="replaceCredential" label="Replace the stored credential" />
            <div v-if="credentialInputs" :id="ids.credential" class="flex flex-col gap-4" tabindex="-1">
              <template v-if="authType === 'StaticHeaders'">
                <div v-for="(header, index) in headers" :key="index" class="grid grid-cols-[1fr_1fr_auto] items-end gap-2 max-md:grid-cols-1">
                  <TextField v-model="header.name" :label="`Header ${index + 1} name`" mono placeholder="X-Api-Key" />
                  <TextField v-model="header.value" :label="`Header ${index + 1} value`" type="password" autocomplete="new-password" />
                  <UiButton size="sm" icon="delete" :disabled="headers.length === 1" :aria-label="`Remove header ${index + 1}`" @click="removeHeader(index)" />
                </div>
                <div><UiButton size="sm" icon="add" @click="addHeader">Add header</UiButton></div>
              </template>
              <template v-else>
                <TextField v-model="tokenUrl" label="Token URL" type="url" inputmode="url" mono hint="The OAuth token endpoint; the same address rules as the server URL apply." />
                <TextField v-model="clientId" label="Client ID" mono />
                <TextField v-model="clientSecret" label="Client secret" type="password" autocomplete="new-password" />
                <TextField v-model="scope" label="Scope (optional)" mono />
              </template>
              <span v-if="errors.credential" class="text-small text-danger" role="alert">{{ errors.credential }}</span>
            </div>
            <span v-else-if="errors.credential" :id="ids.credential" class="text-small text-danger" tabindex="-1" role="alert">{{ errors.credential }}</span>
          </FieldGroup>

          <FieldGroup legend="Calls">
            <FormField
              :id="ids.forwardHeaders"
              v-slot="{ id, describedBy, invalid }"
              label="Forwarded caller headers (optional)"
              :error="errors.forwardHeaders"
              hint="One header name per line, sent on with each caller’s own tool call, for example X-Correlation-Id. Authorization, keys and cookies are never forwarded."
            >
              <textarea :id="id" v-model="forwardHeaders" rows="3" spellcheck="false" :aria-describedby="describedBy" :aria-invalid="invalid ? 'true' : undefined" class="field-input !h-auto py-2 font-mono" />
            </FormField>
            <div class="grid grid-cols-2 gap-4 max-md:grid-cols-1">
              <TextField :id="ids.timeoutSeconds" v-model="timeoutSeconds" label="Timeout (seconds)" inputmode="numeric" :error="errors.timeoutSeconds" />
              <TextField :id="ids.maxResultBytes" v-model="maxResultKib" label="Largest result (KiB)" inputmode="numeric" :error="errors.maxResultBytes" hint="Larger results become an error. 1024 KiB = 1 MiB." />
            </div>
          </FieldGroup>

          <FieldGroup legend="Approval">
            <CheckField v-model="autoApprove" label="Approve new tools automatically" description="Only for trusted internal servers." />
            <InlineError
              v-if="autoApprove"
              tone="warn"
              title="New tools reach keys without review"
              message="Every tool this server adds is approved at the next sync, without anyone reading its description. A tool description is text the model follows, so a compromised or careless server can steer models (tool poisoning). Changes to tools that were already approved still wait for review."
            />
            <CheckField v-if="isNew" v-model="isEnabled" label="Enabled" description="Turn off to keep the server hidden from every key." />
          </FieldGroup>
        </fieldset>
      </form>

      <section v-if="!readOnly" class="mt-8 flex flex-col gap-3 border-t border-border pt-6" aria-labelledby="mcp-test-h">
        <h3 id="mcp-test-h" class="text-heading">Test connection</h3>
        <p class="text-fg-2">Connects with the values above and lists the server’s tools. Nothing is saved.</p>
        <div><UiButton icon="lan" :loading="testBusy" @click="testConnection">Test connection</UiButton></div>
        <div aria-live="polite" data-testid="mcp-test-result">
          <template v-if="testResult">
            <div v-if="testResult.succeeded" class="flex flex-col gap-2 rounded-control bg-ok-soft p-3 text-small text-ok">
              <p class="font-medium">Connected. The server offers {{ plural(testResult.tools.length, 'tool') }}{{ testResult.capabilities.length ? ` (${testResult.capabilities.join(', ').toLowerCase()})` : '' }}.</p>
              <ul v-if="testResult.tools.length" class="m-0 flex list-none flex-wrap gap-1.5 p-0 text-fg">
                <li v-for="tool in testResult.tools" :key="tool.name" class="rounded-chip bg-surface px-2 py-0.5 font-mono">{{ tool.name }}</li>
              </ul>
              <p v-for="warning in testResult.warnings" :key="warning" lang="sv" class="text-warn">{{ warning }}</p>
            </div>
            <InlineError v-else title="The connection failed" :message="testResult.error ?? 'The server could not be reached.'" lang="sv">
              <p v-if="testResult.httpStatus">HTTP {{ testResult.httpStatus }}</p>
            </InlineError>
          </template>
          <InlineError v-else-if="testError" :message="problemMessage(testError)" :lang="problemLang(testError)" />
        </div>
      </section>

      <section v-if="server && !readOnly" class="mt-8 flex flex-col gap-3 border-t border-border pt-6" aria-labelledby="mcp-availability-h">
        <h3 id="mcp-availability-h" class="text-heading">Availability</h3>
        <p class="text-fg-2">{{ server.isEnabled ? 'Keys with a grant can use this server’s approved tools.' : 'The server is disabled: its tools are hidden from every key.' }}</p>
        <div><UiButton :icon="server.isEnabled ? 'pause' : 'play_arrow'" @click="confirmEnabled = true">{{ server.isEnabled ? 'Disable server' : 'Enable server' }}</UiButton></div>
      </section>
    </template>

    <template v-if="!readOnly && (isNew || tab === 'settings')" #footer>
      <UiButton variant="primary" type="submit" form="mcp-form" :loading="busy">{{ server ? 'Save changes' : 'Create server' }}</UiButton>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      <UiButton v-if="server" class="ml-auto" variant="danger" @click="confirmDelete = true">Delete</UiButton>
    </template>

    <ConfirmDialog
      v-model:open="confirmEnabled"
      :title="server?.isEnabled ? 'Disable MCP server?' : 'Enable MCP server?'"
      :consequence="server?.isEnabled ? 'Its tools disappear from every key at once, and calls to them fail as unknown tools.' : 'Keys with a grant see the server’s approved tools again.'"
      :confirm-label="server?.isEnabled ? 'Disable server' : 'Enable server'"
      :danger="server?.isEnabled"
      :action="setEnabled"
    />
    <ConfirmDialog v-model:open="confirmDelete" title="Delete MCP server?" confirm-label="Delete server" danger :action="remove">
      <span class="font-mono">{{ server?.slug }}</span> is removed for good, with its tools, their approvals and every key’s grant on it.
    </ConfirmDialog>
  </UiDrawer>
</template>
