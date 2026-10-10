<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { api } from '@/api'
import type { RoutingRuleTestRequest, RoutingRuleTestResult } from '@/api/types'
import { useModelOptions } from '@/composables/useModelOptions'
import { useKeysStore } from '@/stores/keys'
import { useTeamsStore } from '@/stores/teams'
import { ROUTING_SCOPE_LABEL } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import { isBlank, parseDecimal } from '@/utils/validation'
import AppIcon from '../ui/AppIcon.vue'
import FormField from '../ui/FormField.vue'
import InlineError from '../ui/InlineError.vue'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'

interface Pair {
  uid: number
  name: string
  value: string
}

defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const keys = useKeysStore()
const teams = useTeamsStore()
const models = useModelOptions()

const model = ref('')
const endpoint = ref('chat_completions')
const keyId = ref('')
const teamId = ref('')
const headers = ref<Pair[]>([])
const params = ref<Pair[]>([])
const budgetUsed = ref('')
const tokensUsed = ref('')
const promptTokens = ref('')
const pii = ref('')
const busy = ref(false)
const error = ref<unknown>(null)
const fieldError = ref<string | null>(null)
const result = ref<RoutingRuleTestResult | null>(null)
const resultHeading = ref<HTMLElement | null>(null)
let nextUid = 1

const modelNames = computed(() => models.options.value.map((o) => o.name))
const endpointOptions = [
  { value: 'chat_completions', label: 'Chat completions' },
  { value: 'embeddings', label: 'Embeddings' },
  { value: 'responses', label: 'Responses' },
  { value: 'anthropic_messages', label: 'Anthropic messages' },
  { value: 'audio_transcriptions', label: 'Audio transcriptions' },
  { value: 'audio_translations', label: 'Audio translations' },
  { value: 'realtime', label: 'Realtime (live audio)' },
  { value: 'realtime_translations', label: 'Realtime translations (interpreting)' },
]
const keyOptions = computed(() => [{ value: '', label: 'No key' }, ...keys.items.map((k) => ({ value: k.id, label: `${k.name} · ${k.prefix}` }))])
const teamOptions = computed(() => [{ value: '', label: 'No team' }, ...teams.items.map((t) => ({ value: t.id, label: `${t.name} · ${t.departmentName}` }))])
const piiOptions = [
  { value: '', label: 'Not set' },
  { value: 'yes', label: 'Personal data found' },
  { value: 'no', label: 'No personal data' },
]

function addPair(list: Pair[]): void {
  list.push({ uid: nextUid++, name: '', value: '' })
}

/** `true`/`false` and numbers keep their type, so conditions such as `params["temperature"] > 0.5` work. */
function typed(value: string): string | number | boolean {
  const text = value.trim()
  if (text === 'true') return true
  if (text === 'false') return false
  const number = parseDecimal(text)
  return number !== null && !Number.isNaN(number) ? number : value
}

function optionalNumber(text: string, label: string, max?: number): number | undefined | null {
  if (isBlank(text)) return undefined
  const value = parseDecimal(text)
  if (value === null || Number.isNaN(value) || value < 0 || (max !== undefined && value > max)) {
    fieldError.value = `${label} must be a number${max !== undefined ? ` from 0 to ${max}` : ', 0 or higher'}.`
    return null
  }
  return value
}

async function run(): Promise<void> {
  if (busy.value) return
  error.value = null
  fieldError.value = null
  if (isBlank(model.value)) {
    fieldError.value = 'Enter the model name the client would ask for.'
    document.getElementById('test-model')?.focus()
    return
  }
  const budget = optionalNumber(budgetUsed.value, 'Budget used', 100)
  const tokens = optionalNumber(tokensUsed.value, 'Tokens used', 100)
  const prompt = optionalNumber(promptTokens.value, 'Prompt tokens')
  if (budget === null || tokens === null || prompt === null) return

  const body: RoutingRuleTestRequest = { model: model.value.trim(), endpoint: endpoint.value }
  const headerPairs = headers.value.filter((p) => !isBlank(p.name))
  if (headerPairs.length > 0) body.headers = Object.fromEntries(headerPairs.map((p) => [p.name.trim(), p.value]))
  const paramPairs = params.value.filter((p) => !isBlank(p.name))
  if (paramPairs.length > 0) body.params = Object.fromEntries(paramPairs.map((p) => [p.name.trim(), typed(p.value)]))
  if (keyId.value) body.keyId = keyId.value
  if (teamId.value) body.teamId = teamId.value
  if (budget !== undefined) body.budgetUsed = budget
  if (tokens !== undefined) body.tokensUsed = tokens
  if (prompt !== undefined) body.promptTokens = Math.round(prompt)
  if (pii.value) body.piiDetected = pii.value === 'yes'

  busy.value = true
  try {
    result.value = await api.routingRules.test(body)
    await nextTick()
    resultHeading.value?.focus()
  } catch (e) {
    result.value = null
    error.value = e
  } finally {
    busy.value = false
  }
}

async function loadChoices(): Promise<void> {
  await Promise.all([keys.items.length ? null : keys.load(), teams.items.length ? null : teams.load()])
}

function onOpen(open: boolean): void {
  if (open) void loadChoices()
  emit('update:open', open)
}

const OUTCOME = {
  Matched: { label: 'Matched', tone: 'ok' as const },
  NotMatched: { label: 'Did not match', tone: 'neutral' as const },
  Skipped: { label: 'Skipped: already applied', tone: 'neutral' as const },
}
</script>

<template>
  <UiDialog wide :open="open" title="Test a request" description="See which routing rule would apply to a request like this, and why. Only the values you fill in are known to the rules." icon="play_arrow" @update:open="onOpen">
    <form class="flex flex-col gap-5" novalidate @submit.prevent="run">
      <FormField id="test-model" v-slot="{ id: fieldId }" label="Model the client asks for" :error="fieldError">
        <input :id="fieldId" v-model="model" list="test-model-names" class="field-input font-mono" autocomplete="off" spellcheck="false" />
        <datalist id="test-model-names"><option v-for="n in modelNames" :key="n" :value="n" /></datalist>
      </FormField>
      <div class="grid grid-cols-3 gap-3 max-md:grid-cols-1">
        <SelectField id="test-endpoint" v-model="endpoint" label="Endpoint" :options="endpointOptions" />
        <SelectField id="test-key" v-model="keyId" label="Key" :options="keyOptions" hint="Fills in its team and department." />
        <SelectField id="test-team" v-model="teamId" label="Team" :options="teamOptions" />
      </div>

      <details class="rounded-control bg-surface-2 px-3 py-2">
        <summary class="cursor-pointer font-medium">Headers and request parameters</summary>
        <div class="mt-3 flex flex-col gap-4">
          <fieldset class="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
            <legend class="mb-1 p-0 font-medium">Headers</legend>
            <div v-for="(h, i) in headers" :key="h.uid" class="grid grid-cols-[1fr_1fr_auto] items-end gap-2">
              <TextField v-model="h.name" :label="`Header ${i + 1} name`" mono placeholder="x-ume-tier" />
              <TextField v-model="h.value" :label="`Header ${i + 1} value`" mono />
              <UiButton size="sm" variant="danger" icon="delete" icon-only :aria-label="`Remove header ${i + 1}`" @click="headers.splice(i, 1)" />
            </div>
            <div><UiButton size="sm" icon="add" @click="addPair(headers)">Add header</UiButton></div>
          </fieldset>
          <fieldset class="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
            <legend class="mb-1 p-0 font-medium">Parameters</legend>
            <div v-for="(p, i) in params" :key="p.uid" class="grid grid-cols-[1fr_1fr_auto] items-end gap-2">
              <TextField v-model="p.name" :label="`Parameter ${i + 1} name`" mono placeholder="max_tokens" />
              <TextField v-model="p.value" :label="`Parameter ${i + 1} value`" mono hint="Numbers and true/false keep their type." />
              <UiButton size="sm" variant="danger" icon="delete" icon-only :aria-label="`Remove parameter ${i + 1}`" @click="params.splice(i, 1)" />
            </div>
            <div><UiButton size="sm" icon="add" @click="addPair(params)">Add parameter</UiButton></div>
          </fieldset>
        </div>
      </details>

      <details class="rounded-control bg-surface-2 px-3 py-2">
        <summary class="cursor-pointer font-medium">Usage values</summary>
        <div class="mt-3 grid grid-cols-2 gap-3 max-md:grid-cols-1">
          <TextField id="test-budget" v-model="budgetUsed" label="Budget used (%)" inputmode="decimal" hint="Leave empty if unknown." />
          <TextField id="test-tokens" v-model="tokensUsed" label="Token limit used (%)" inputmode="decimal" />
          <TextField id="test-prompt" v-model="promptTokens" label="Prompt tokens" inputmode="numeric" />
          <SelectField id="test-pii" v-model="pii" label="Personal data" :options="piiOptions" />
        </div>
      </details>

      <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
      <div><UiButton variant="primary" type="submit" icon="play_arrow" :loading="busy">Run test</UiButton></div>
    </form>

    <section v-if="result" aria-label="Test result" class="flex flex-col gap-4 border-t border-border pt-5">
      <div class="flex flex-col gap-2">
        <h3 ref="resultHeading" tabindex="-1" class="text-heading outline-none">
          <template v-if="result.matched">{{ result.applied[result.applied.length - 1]?.name }} applies</template>
          <template v-else>No rule matches</template>
        </h3>
        <p v-if="!result.matched" class="text-fg-2">The request goes to the model it asked for, <span class="break-all font-mono">{{ model }}</span>.</p>
        <template v-else>
          <ol v-if="result.applied.length > 1" class="m-0 flex list-none flex-col gap-1 p-0 text-small text-fg-2">
            <li v-for="(step, i) in result.applied" :key="step.ruleId">{{ i + 1 }}. <span class="font-medium text-fg">{{ step.name }}</span>: <span class="break-all font-mono">{{ step.fromModel }}</span> → <span class="break-all font-mono">{{ step.toModel }}</span></li>
          </ol>
          <p class="text-fg-2">The request is tried against, in this order:</p>
          <ol class="m-0 flex list-none flex-col gap-2 p-0">
            <li v-for="(m, i) in result.models" :key="m.name" class="flex flex-wrap items-center gap-2 rounded-tile bg-surface-2 px-3 py-2">
              <span class="tabular text-small text-fg-3">{{ i + 1 }}</span>
              <span class="break-all font-mono font-medium">{{ m.name }}</span>
              <UiBadge v-if="!m.exists" tone="danger" label="Does not exist" />
              <UiBadge v-else-if="!m.enabled" tone="warn" label="Disabled" />
              <UiBadge v-else tone="neutral" :label="m.type === 'alias' ? 'Route' : 'Model'" :dot="false" />
            </li>
          </ol>
        </template>
        <InlineError v-if="result.chainLimitReached" tone="warn" message="The chain stopped after the maximum number of steps. Check the rules for a loop." />
      </div>

      <div v-if="result.ignoredRules.length > 0" class="flex flex-col gap-1">
        <InlineError tone="warn" title="The gateway ignores these rules because they are invalid" message="Fix or disable them.">
          <ul class="m-0 list-disc pl-5"><li v-for="r in result.ignoredRules" :key="r.ruleId"><span class="font-medium">{{ r.ruleName }}</span>: {{ r.message }}</li></ul>
        </InlineError>
      </div>

      <div class="flex flex-col gap-3">
        <h3 class="text-heading">Rules checked</h3>
        <p v-if="result.evaluation.length === 0" class="text-fg-2">No enabled rule applies to this key, team or department.</p>
        <ol class="m-0 flex list-none flex-col gap-3 p-0">
          <li v-for="(e, i) in result.evaluation" :key="`${e.ruleId}-${e.chainStep}-${i}`" class="flex flex-col gap-2 rounded-tile bg-surface-2 p-4">
            <div class="flex flex-wrap items-center gap-2">
              <span class="font-medium">{{ e.name }}</span>
              <span class="text-small text-fg-3">{{ ROUTING_SCOPE_LABEL[e.scope] }}<template v-if="result.applied.length > 1 || e.chainStep > 0"> · step {{ e.chainStep + 1 }} for <span class="font-mono">{{ e.model }}</span></template></span>
              <UiBadge :tone="OUTCOME[e.outcome].tone" :label="OUTCOME[e.outcome].label" />
            </div>
            <ul v-if="e.trace.length > 0" class="m-0 flex list-none flex-col gap-1 p-0 text-small">
              <li v-for="(t, j) in e.trace" :key="j" class="flex flex-wrap items-start gap-2">
                <AppIcon :name="t.result === true ? 'check_circle' : t.result === false ? 'close' : 'help'" :size="18" :filled="t.result === true" class="mt-px" :class="t.result === true ? 'text-ok' : t.result === false ? 'text-fg-2' : 'text-warn'" />
                <span class="break-all font-mono">{{ t.text }}</span>
                <span class="text-fg-2">
                  {{ t.result === true ? 'true' : t.result === false ? 'false' : 'could not be evaluated (a value is missing)' }}<template v-if="t.leftValue"> · was <span class="font-mono">{{ t.leftValue }}</span></template>
                </span>
              </li>
            </ul>
            <p v-else-if="e.outcome === 'Matched'" class="text-small text-fg-2">No condition: matches every request.</p>
          </li>
        </ol>
        <p class="text-small text-fg-3">{{ result.note }}</p>
      </div>
    </section>

    <template #actions>
      <UiButton @click="onOpen(false)">Close</UiButton>
    </template>
  </UiDialog>
</template>
