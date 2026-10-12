<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api } from '@/api'
import { MCP_APPROVAL_POLICIES, type McpApprovalPolicy, type McpGrant, type McpTool } from '@/api/types'
import { useMcpStore } from '@/stores/mcp'
import { MCP_APPROVAL_POLICY, MCP_TOOL_STATE } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import AppIcon from '@/components/ui/AppIcon.vue'
import CheckField from '@/components/ui/CheckField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import UiButton from '@/components/ui/UiButton.vue'

interface Draft {
  serverId: string
  allTools: boolean
  tools: string[]
  approvalPolicy: McpApprovalPolicy
  allowResources: boolean
  allowPrompts: boolean
}

/** A key's grants on MCP servers: deny by default, one entry per server with its tools, approval policy and documents. */
const props = defineProps<{ keyId: string; editable: boolean }>()

const mcp = useMcpStore()
const grants = ref<McpGrant[]>([])
const loading = ref(false)
const error = ref<unknown>(null)
const editing = ref(false)
const busy = ref(false)
const status = ref('')
const drafts = ref<Draft[]>([])
const addServer = ref('')
const tools = reactive<Record<string, McpTool[] | 'loading' | 'failed'>>({})

const serverName = (id: string) => mcp.servers.find((s) => s.id === id)?.displayName ?? grants.value.find((g) => g.serverId === id)?.serverName ?? id
const serverSlug = (id: string) => mcp.servers.find((s) => s.id === id)?.slug ?? grants.value.find((g) => g.serverId === id)?.serverSlug ?? ''
const serverOf = (id: string) => mcp.servers.find((s) => s.id === id)
const addOptions = computed(() =>
  mcp.servers.filter((s) => !drafts.value.some((d) => d.serverId === s.id)).map((s) => ({ value: s.id, label: `${s.displayName} (${s.slug})` })),
)
const policyOptions = MCP_APPROVAL_POLICIES.map((p) => ({ value: p, label: MCP_APPROVAL_POLICY[p].label }))

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    grants.value = await api.keys.mcpGrants(props.keyId)
  } catch (e) {
    error.value = e
  } finally {
    loading.value = false
  }
}

async function loadTools(serverId: string): Promise<void> {
  if (tools[serverId] && tools[serverId] !== 'failed') return
  tools[serverId] = 'loading'
  try {
    tools[serverId] = await api.mcp.tools(serverId)
  } catch {
    tools[serverId] = 'failed'
  }
}

function toolList(serverId: string): McpTool[] {
  const list = tools[serverId]
  return Array.isArray(list) ? list : []
}

/** Names a draft allows that the server does not offer: kept, but they allow nothing until the server offers them. */
function unknownNames(draft: Draft): string[] {
  const known = toolList(draft.serverId).map((t) => t.name)
  return Array.isArray(tools[draft.serverId]) ? draft.tools.filter((t) => !known.includes(t)) : []
}

function edit(): void {
  drafts.value = grants.value.map((g) => ({
    serverId: g.serverId,
    allTools: g.allowedTools.includes('*'),
    tools: g.allowedTools.filter((t) => t !== '*'),
    approvalPolicy: g.approvalPolicy,
    allowResources: g.allowResources,
    allowPrompts: g.allowPrompts,
  }))
  for (const draft of drafts.value) if (!draft.allTools) void loadTools(draft.serverId)
  addServer.value = ''
  status.value = ''
  error.value = null
  editing.value = true
  if (!mcp.loaded) void mcp.load()
}

function add(): void {
  if (!addServer.value) return
  drafts.value = [...drafts.value, { serverId: addServer.value, allTools: true, tools: [], approvalPolicy: 'Always', allowResources: false, allowPrompts: false }]
  addServer.value = ''
}

function remove(serverId: string): void {
  drafts.value = drafts.value.filter((d) => d.serverId !== serverId)
}

function setAll(draft: Draft, all: boolean): void {
  draft.allTools = all
  if (!all) void loadTools(draft.serverId)
}

function toggleTool(draft: Draft, name: string, on: boolean): void {
  draft.tools = on ? [...draft.tools, name] : draft.tools.filter((t) => t !== name)
}

async function save(): Promise<void> {
  busy.value = true
  error.value = null
  status.value = ''
  try {
    grants.value = await api.keys.replaceMcpGrants(
      props.keyId,
      drafts.value.map((d) => ({
        serverId: d.serverId,
        allowedTools: d.allTools ? ['*'] : [...d.tools],
        approvalPolicy: d.approvalPolicy,
        allowResources: d.allowResources,
        allowPrompts: d.allowPrompts,
      })),
    )
    editing.value = false
    status.value = 'MCP access saved.'
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}

watch(() => props.keyId, () => {
  editing.value = false
  status.value = ''
  void load()
}, { immediate: true })
</script>

<template>
  <div class="flex flex-col gap-3" data-testid="key-mcp-grants">
    <p class="sr-only" aria-live="polite">{{ status }}</p>
    <p v-if="loading && grants.length === 0" class="text-small text-fg-3">Loading MCP access…</p>

    <template v-else-if="!editing">
      <p v-if="grants.length === 0" class="text-small text-fg-3">No MCP servers. The key sees no MCP tools.</p>
      <ul v-else class="m-0 flex list-none flex-col gap-3 p-0">
        <li v-for="g in grants" :key="g.serverId" class="flex flex-col gap-0.5 text-small">
          <span class="font-medium">{{ g.serverName }} <span class="font-mono text-fg-3">{{ g.serverSlug }}</span></span>
          <span>{{ g.allowedTools.includes('*') ? 'All approved tools' : g.allowedTools.length ? g.allowedTools.join(', ') : 'No tools' }}</span>
          <span class="text-fg-3">
            {{ MCP_APPROVAL_POLICY[g.approvalPolicy].label }}<template v-if="g.allowResources"> · documents</template><template v-if="g.allowPrompts"> · prompts</template>
          </span>
          <span v-if="g.unknownTools.length" class="flex items-start gap-1 text-warn">
            <AppIcon name="warning" :size="16" filled class="mt-px shrink-0" />
            Not offered by the server: {{ g.unknownTools.join(', ') }}. These names allow nothing until it does.
          </span>
        </li>
      </ul>
      <div v-if="editable"><UiButton size="sm" icon="edit" @click="edit">Edit MCP access</UiButton></div>
    </template>

    <form v-else class="flex flex-col gap-4" novalidate @submit.prevent="save">
      <p v-if="drafts.length === 0" class="text-small text-fg-3">No servers. Add one below.</p>
      <fieldset v-for="draft in drafts" :key="draft.serverId" class="m-0 flex min-w-0 flex-col gap-3 rounded-control border border-border p-3">
        <legend class="px-1 font-medium">{{ serverName(draft.serverId) }} <span class="font-mono text-fg-3">{{ serverSlug(draft.serverId) }}</span></legend>
        <CheckField
          :model-value="draft.allTools"
          label="All approved tools"
          description="Includes tools approved later."
          @update:model-value="setAll(draft, $event)"
        />
        <div v-if="!draft.allTools" class="flex flex-col gap-1 pl-7">
          <p v-if="tools[draft.serverId] === 'loading'" class="text-small text-fg-3">Loading tools…</p>
          <p v-else-if="tools[draft.serverId] === 'failed'" class="text-small text-danger">The server’s tools could not be loaded.</p>
          <p v-else-if="toolList(draft.serverId).length === 0" class="text-small text-fg-3">The server has no tools yet. Sync it first.</p>
          <fieldset class="m-0 flex flex-col gap-1 border-0 p-0">
            <legend class="sr-only">Allowed tools on {{ serverName(draft.serverId) }}</legend>
            <CheckField
              v-for="tool in toolList(draft.serverId)"
              :key="tool.id"
              :label="tool.name"
              :description="MCP_TOOL_STATE[tool.approvalState].label"
              :model-value="draft.tools.includes(tool.name)"
              @update:model-value="toggleTool(draft, tool.name, $event)"
            />
            <CheckField
              v-for="name in unknownNames(draft)"
              :key="name"
              :label="name"
              description="Not offered by the server; allows nothing until it is."
              :model-value="true"
              @update:model-value="toggleTool(draft, name, $event)"
            />
          </fieldset>
        </div>
        <SelectField v-model="draft.approvalPolicy" label="Approval of tool calls inside LLM requests" :options="policyOptions" :hint="MCP_APPROVAL_POLICY[draft.approvalPolicy].hint" />
        <CheckField
          v-model="draft.allowResources"
          label="Documents (resources)"
          :description="serverOf(draft.serverId)?.capabilities.includes('Resources') === false ? 'The server does not offer documents.' : 'The key may list and read the server’s documents.'"
        />
        <CheckField
          v-model="draft.allowPrompts"
          label="Prompts"
          :description="serverOf(draft.serverId)?.capabilities.includes('Prompts') === false ? 'The server does not offer prompts.' : 'The key may list and get the server’s prompts.'"
        />
        <div><UiButton size="sm" variant="danger" icon="delete" @click="remove(draft.serverId)">Remove {{ serverName(draft.serverId) }}</UiButton></div>
      </fieldset>
      <div v-if="addOptions.length" class="flex flex-wrap items-end gap-2">
        <SelectField v-model="addServer" label="Add MCP server" placeholder="Choose a server" :options="addOptions" class="min-w-60" />
        <UiButton size="sm" icon="add" :disabled="!addServer" @click="add">Add</UiButton>
      </div>
      <div class="flex flex-wrap gap-2">
        <UiButton type="submit" size="sm" variant="primary" :loading="busy">Save MCP access</UiButton>
        <UiButton size="sm" :disabled="busy" @click="editing = false">Cancel</UiButton>
      </div>
    </form>
    <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
  </div>
</template>
