<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { ConditionCheck } from '@/api/types'
import AppIcon from '../ui/AppIcon.vue'
import FormField from '../ui/FormField.vue'

export type ConditionStatus = 'idle' | 'checking' | 'valid' | 'invalid' | 'unavailable'

const props = withDefaults(defineProps<{ modelValue: string; id?: string; label?: string; error?: string | null; debounceMs?: number }>(), {
  id: 'rule-condition',
  label: 'Condition',
  debounceMs: 400,
})
const emit = defineEmits<{ (e: 'update:modelValue', value: string): void; (e: 'status', value: ConditionStatus): void }>()

/** Short examples that insert a complete, valid condition. */
const EXAMPLES = [
  { label: 'Premium header', code: `headers["x-tier"] == "premium"` },
  { label: 'Budget nearly used', code: 'budget_used > 90' },
  { label: 'Claude models', code: 'model.startsWith("claude-")' },
  { label: 'Personal data', code: 'pii_detected' },
  { label: 'Large prompt', code: 'prompt_tokens > 20000' },
]

const area = ref<HTMLTextAreaElement | null>(null)
const status = ref<ConditionStatus>('idle')
const check = ref<ConditionCheck | null>(null)
const available = ref<ConditionCheck['available']>([])
let timer: ReturnType<typeof setTimeout> | undefined
let sequence = 0

const statusId = computed(() => `${props.id}-status`)
const first = computed(() => (check.value && !check.value.valid ? (check.value.errors[0] ?? null) : null))
const excerpt = computed(() => {
  const error = first.value
  if (!error) return null
  const text = props.modelValue
  const start = Math.min(error.position, text.length)
  const end = Math.min(start + Math.max(error.length, 1), text.length)
  return { before: text.slice(Math.max(0, start - 24), start), bad: text.slice(start, end) || '␣', after: text.slice(end, end + 24), position: error.position + 1 }
})

function setStatus(next: ConditionStatus): void {
  status.value = next
  emit('status', next)
}

async function run(value: string): Promise<void> {
  const mine = ++sequence
  setStatus('checking')
  try {
    const result = await api.routingRules.validate(value)
    if (mine !== sequence) return // a newer check is already running
    check.value = result
    if (result.available.length > 0) available.value = result.available
    setStatus(result.valid ? 'valid' : 'invalid')
  } catch {
    if (mine !== sequence) return
    check.value = null
    setStatus('unavailable')
  }
}

function schedule(value: string): void {
  clearTimeout(timer)
  sequence++ // an older response must not overwrite what is shown for newer text
  setStatus('checking')
  timer = setTimeout(() => void run(value), props.debounceMs)
}

watch(() => props.modelValue, (value) => schedule(value))
// The first check also fetches the variable list for the helper below.
onMounted(() => void run(props.modelValue))
onBeforeUnmount(() => clearTimeout(timer))

function insert(text: string): void {
  const element = area.value
  const value = props.modelValue
  const start = element?.selectionStart ?? value.length
  const end = element?.selectionEnd ?? value.length
  emit('update:modelValue', value.slice(0, start) + text + value.slice(end))
  requestAnimationFrame(() => {
    element?.focus()
    element?.setSelectionRange(start + text.length, start + text.length)
  })
}

function replaceAll(code: string): void {
  emit('update:modelValue', code)
  requestAnimationFrame(() => area.value?.focus())
}
</script>

<template>
  <FormField :id="id" v-slot="{ id: fieldId, describedBy, invalid }" :label="label" :error="error">
    <textarea
      :id="fieldId"
      ref="area"
      :value="modelValue"
      rows="3"
      spellcheck="false"
      autocapitalize="off"
      autocomplete="off"
      :aria-invalid="invalid || status === 'invalid' ? 'true' : undefined"
      :aria-describedby="[describedBy, statusId].filter(Boolean).join(' ')"
      class="field-input !h-auto min-h-[88px] resize-y py-2 font-mono text-small"
      @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)"
    />
    <div :id="statusId" role="status" aria-live="polite" class="flex flex-col gap-1 text-small">
      <p v-if="!modelValue.trim() && status !== 'unavailable'" class="flex items-center gap-1 text-fg-3"><AppIcon name="info" :size="16" />No condition: the rule matches every request in its scope.</p>
      <p v-else-if="status === 'checking'" class="flex items-center gap-1 text-fg-3"><AppIcon name="progress_activity" :size="16" class="animate-spin" />Checking…</p>
      <p v-else-if="status === 'valid'" class="flex flex-wrap items-center gap-1 text-ok">
        <AppIcon name="check_circle" :size="16" filled />Valid<template v-if="check && check.variables.length > 0"> · uses <span class="font-mono">{{ check.variables.join(', ') }}</span></template>
      </p>
      <template v-else-if="status === 'invalid' && first && excerpt">
        <p class="flex items-start gap-1 text-danger"><AppIcon name="error" :size="16" filled class="mt-px" /><span>{{ first.message }} (character {{ excerpt.position }})</span></p>
        <p class="break-all rounded-control bg-sunken px-2 py-1 font-mono text-caption text-fg-2"><span aria-hidden="true">…</span>{{ excerpt.before }}<mark class="rounded-chip bg-danger-soft px-0.5 text-danger">{{ excerpt.bad }}</mark>{{ excerpt.after }}<span aria-hidden="true">…</span></p>
      </template>
      <p v-else-if="status === 'unavailable'" class="flex items-center gap-1 text-fg-3"><AppIcon name="info" :size="16" />Couldn’t check the condition right now. It is checked again when you save.</p>
    </div>
    <details class="rounded-control bg-surface-2 px-3 py-2 text-small">
      <summary class="cursor-pointer font-medium">Variables and examples</summary>
      <div class="mt-3 flex flex-col gap-3">
        <div>
          <p class="mb-1 text-fg-2">Examples (replaces the condition):</p>
          <ul class="m-0 flex list-none flex-wrap gap-2 p-0">
            <li v-for="example in EXAMPLES" :key="example.label">
              <button type="button" class="rounded-chip bg-surface px-2.5 py-1 shadow-inset-input hover:bg-surface-2" :title="example.code" @click="replaceAll(example.code)">{{ example.label }}</button>
            </li>
          </ul>
        </div>
        <div v-if="available.length > 0">
          <p class="mb-1 text-fg-2">Variables (inserts at the cursor):</p>
          <ul class="m-0 flex list-none flex-wrap gap-2 p-0">
            <li v-for="variable in available" :key="variable.name">
              <button type="button" class="rounded-chip bg-surface px-2.5 py-1 font-mono shadow-inset-input hover:bg-surface-2" :aria-label="`Insert ${variable.name} (${variable.type})`" @click="insert(variable.name)">{{ variable.name }}</button>
            </li>
          </ul>
        </div>
        <p class="text-fg-3">
          Compare with <span class="font-mono">== != &lt; &gt;</span>, combine with <span class="font-mono">&amp;&amp; || !</span>, test lists with <span class="font-mono">in</span>, and use
          <span class="font-mono">startsWith endsWith contains matches size</span>. A value that does not exist (a header that was not sent) makes the comparison not match.
        </p>
      </div>
    </details>
  </FormField>
</template>
