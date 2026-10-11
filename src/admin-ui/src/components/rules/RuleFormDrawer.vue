<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { RoutingRule, RoutingScope } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useModelOptions } from '@/composables/useModelOptions'
import { useRuleOwners } from '@/composables/useRuleOwners'
import { useRoutingRulesStore } from '@/stores/routingRules'
import { useUiStore } from '@/stores/ui'
import { ROUTING_SCOPE_HINT, ROUTING_SCOPE_LABEL, ROUTING_SCOPE_PLURAL } from '@/utils/labels'
import { targetShares } from '@/utils/routingRules'
import { isBlank, parseInteger } from '@/utils/validation'
import CheckField from '../ui/CheckField.vue'
import FieldGroup from '../ui/FieldGroup.vue'
import FormField from '../ui/FormField.vue'
import InlineError from '../ui/InlineError.vue'
import SegmentedControl from '../ui/SegmentedControl.vue'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'
import UiButton from '../ui/UiButton.vue'
import UiDrawer from '../ui/UiDrawer.vue'
import UiSwitch from '../ui/UiSwitch.vue'
import ConditionEditor, { type ConditionStatus } from './ConditionEditor.vue'
import OwnerPicker from './OwnerPicker.vue'

interface TargetDraft {
  uid: number
  model: string
  weight: string
}
interface FallbackDraft {
  uid: number
  model: string
}

/** `rule` is edited; without it a new rule is created, prefilled from `template` when given. */
const props = defineProps<{ open: boolean; rule: RoutingRule | null; template?: RoutingRule | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', rule: RoutingRule): void }>()

const rules = useRoutingRulesStore()
const ui = useUiStore()
const models = useModelOptions()
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors({
  name: 'rule-name',
  scopeIds: 'rule-owner',
  condition: 'rule-condition',
  targets: 'rule-target-0',
  fallbacks: 'rule-fallback-0',
})

const name = ref('')
const description = ref('')
const scope = ref<RoutingScope>('Global')
const scopeIds = ref<string[]>([])
const templateId = ref('')
const isEnabled = ref(true)
const chain = ref(false)
const condition = ref('')
const conditionStatus = ref<ConditionStatus>('idle')
const targets = ref<TargetDraft[]>([])
const fallbacks = ref<FallbackDraft[]>([])
const busy = ref(false)
let nextUid = 1

const SCOPE_ORDER: RoutingScope[] = ['Global', 'Department', 'Team', 'VirtualKey']
const scopeOptions = SCOPE_ORDER.map((s) => ({ value: s, label: ROUTING_SCOPE_LABEL[s] }))
const modelNames = computed(() => models.options.value.map((o) => o.name))
const known = computed(() => new Set(modelNames.value.map((n) => n.toLowerCase())))
const shares = computed(() => targetShares(targets.value.map((t) => parseInteger(t.weight) ?? 0)))

const owners = useRuleOwners(scope)
const ownerChanged = computed(() => {
  const rule = props.rule
  if (!rule || scope.value !== rule.scope) return true
  return scopeIds.value.length !== rule.scopeIds.length || scopeIds.value.some((id) => !rule.scopeIds.includes(id))
})
/** A rule without an owner cannot be switched on until it has a new one. */
const enableLocked = computed(() => !!props.rule?.isOrphaned && !ownerChanged.value)
const templates = computed(() => rules.items.filter((r) => r.isTemplate))
const templateOptions = computed(() => templates.value.map((t) => ({ value: t.id, label: t.name })))

function setScope(next: RoutingScope): void {
  scope.value = next
  scopeIds.value = []
  void owners.load(next)
}

/** Fills the form from an example rule; the owners and the on/off state stay as they are. */
function applyTemplate(id: string): void {
  templateId.value = id
  const template = templates.value.find((t) => t.id === id) ?? (props.template?.id === id ? props.template : null)
  if (!template) return
  name.value = template.name
  description.value = template.description ?? ''
  condition.value = template.condition
  chain.value = template.chain
  targets.value = template.targets.map((t) => ({ uid: nextUid++, model: t.model, weight: String(t.weight) }))
  fallbacks.value = template.fallbacks.map((m) => ({ uid: nextUid++, model: m }))
}

function addTarget(): void {
  targets.value.push({ uid: nextUid++, model: '', weight: '1' })
}
function addFallback(): void {
  fallbacks.value.push({ uid: nextUid++, model: '' })
}
function moveFallback(index: number, delta: -1 | 1): void {
  const list = fallbacks.value
  const other = index + delta
  if (other < 0 || other >= list.length) return
  ;[list[index], list[other]] = [list[other]!, list[index]!]
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    const rule = props.rule
    templateId.value = ''
    name.value = rule?.name ?? ''
    description.value = rule?.description ?? ''
    scope.value = rule?.scope ?? 'Global'
    scopeIds.value = [...(rule?.scopeIds ?? [])]
    isEnabled.value = rule ? rule.isEnabled : true
    chain.value = rule?.chain ?? false
    condition.value = rule?.condition ?? ''
    targets.value = (rule?.targets ?? []).map((t) => ({ uid: nextUid++, model: t.model, weight: String(t.weight) }))
    fallbacks.value = (rule?.fallbacks ?? []).map((m) => ({ uid: nextUid++, model: m }))
    if (!rule) addTarget()
    if (!rule && props.template) applyTemplate(props.template.id)
    void owners.load(scope.value)
  },
  { immediate: true },
)

watch(ownerChanged, (changed) => {
  if (changed && props.rule?.isOrphaned && !isEnabled.value) isEnabled.value = true
})

function targetsProblem(): string | false {
  if (targets.value.length === 0) return 'Add at least one target.'
  for (const [i, t] of targets.value.entries()) {
    const weight = parseInteger(t.weight)
    if (isBlank(t.model)) return `Choose a model or route for target ${i + 1}.`
    if (weight === null || Number.isNaN(weight) || weight < 1) return `Weight for target ${i + 1} must be a whole number, 1 or higher.`
  }
  return false
}

/** New rules, and rules moved to another scope, are checked last in their scope. */
function nextPriority(): number {
  const sameScope = rules.items.filter((r) => r.scope === scope.value && r.id !== props.rule?.id)
  return sameScope.length === 0 ? 0 : Math.max(...sameScope.map((r) => r.priority)) + 10
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    name: isBlank(name.value) && 'Give the rule a title.',
    scopeIds:
      scope.value !== 'Global' && scopeIds.value.length === 0 && !(props.rule?.isOrphaned && !isEnabled.value) && `Choose at least one ${ROUTING_SCOPE_LABEL[scope.value].toLowerCase()} this rule applies to.`,
    condition: conditionStatus.value === 'invalid' && 'Fix the condition first. The problem is shown under the field.',
    targets: targetsProblem(),
    fallbacks: fallbacks.value.some((f) => isBlank(f.model)) && 'Fill in or remove the empty fallback.',
  })
  if (!ok) return
  busy.value = true
  try {
    const body = {
      name: name.value.trim(),
      description: description.value.trim() || null,
      scope: scope.value,
      scopeIds: scope.value === 'Global' ? [] : [...scopeIds.value],
      isEnabled: enableLocked.value ? false : isEnabled.value,
      priority: props.rule && props.rule.scope === scope.value ? props.rule.priority : nextPriority(),
      condition: condition.value.trim(),
      chain: chain.value,
      targets: targets.value.map((t) => ({ model: t.model.trim(), weight: parseInteger(t.weight) ?? 1 })),
      fallbacks: fallbacks.value.map((f) => f.model.trim()),
    }
    const saved = props.rule ? await rules.update(props.rule.id, body) : await rules.create(body)
    ui.notify('Rule saved')
    emit('saved', saved)
    emit('update:open', false)
  } catch (e) {
    applyServerError(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDrawer wide :open="open" :title="rule ? 'Edit rule' : 'New rule'" @update:open="emit('update:open', $event)">
    <form id="rule-form" class="flex flex-col gap-6" novalidate @submit.prevent="submit">
      <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />
      <SelectField
        v-if="!rule && templateOptions.length > 0"
        id="rule-template"
        :model-value="templateId"
        label="Start from a template"
        :options="templateOptions"
        placeholder="Blank rule"
        hint="Optional. Fills in the title, description, condition and targets of an example rule. Adjust them before you enable the rule."
        @update:model-value="applyTemplate"
      />
      <TextField id="rule-name" v-model="name" label="Title" :error="errors.name" hint="Shown in lists, the audit log and usage. Must be unique for each key, team or department." />
      <TextField id="rule-desc" v-model="description" label="Short description" hint="Optional. One sentence on what the rule is for." />
      <div class="flex items-start justify-between gap-4 rounded-tile bg-surface-2 p-4">
        <span class="flex flex-col">
          <span id="rule-enabled-label" class="font-medium">Enabled</span>
          <span id="rule-enabled-hint" class="text-small text-fg-3">{{
            enableLocked ? 'This rule has no owner. Choose new owners below to enable it.' : 'Turn off to keep the rule without applying it. A rule can only be on when its models and owners exist.'
          }}</span>
        </span>
        <UiSwitch v-model="isEnabled" labelledby="rule-enabled-label" describedby="rule-enabled-hint" :disabled="enableLocked" />
      </div>

      <FieldGroup legend="Applies to" :hint="ROUTING_SCOPE_HINT[scope]">
        <SegmentedControl :model-value="scope" label="Scope" mode="radio" :options="scopeOptions" @update:model-value="setScope($event)" />
        <OwnerPicker
          v-if="scope !== 'Global'"
          id="rule-owner"
          v-model="scopeIds"
          :label="ROUTING_SCOPE_PLURAL[scope]"
          :options="owners.options.value"
          :error="errors.scopeIds"
          hint="Choose one or more. The rule is shared: changing it changes it for all of them."
        />
      </FieldGroup>

      <ConditionEditor id="rule-condition" v-model="condition" :error="errors.condition" @status="conditionStatus = $event" />

      <FieldGroup legend="Send to" hint="Name a route alias or a model. With more than one target, traffic is split by weight and the others take over if one fails.">
        <datalist id="rule-model-names"><option v-for="n in modelNames" :key="n" :value="n" /></datalist>
        <ul class="m-0 flex list-none flex-col gap-3 p-0">
          <li v-for="(t, i) in targets" :key="t.uid"><div role="group" :aria-label="`Target ${i + 1}`" class="flex flex-col gap-3 rounded-tile bg-surface-2 p-4">
            <FormField :id="`rule-target-${i}`" v-slot="{ id: fieldId }" label="Model or route" :hint="!chain && t.model.trim() && !known.has(t.model.trim().toLowerCase()) ? 'Not an existing model or route yet. The rule can be saved switched off, and enabled once the name exists (or as a chained rule that another rule handles).' : undefined">
              <input :id="fieldId" v-model="t.model" list="rule-model-names" class="field-input font-mono" autocomplete="off" spellcheck="false" />
            </FormField>
            <div class="grid grid-cols-[1fr_auto] items-end gap-3">
              <TextField v-model="t.weight" label="Weight" type="number" min="1" inputmode="numeric" :hint="`${shares[i]}% of the traffic`" />
              <UiButton size="sm" variant="danger" icon="delete" :aria-label="`Remove target ${i + 1}`" :disabled="targets.length === 1" @click="targets.splice(i, 1)">Remove</UiButton>
            </div>
          </div></li>
        </ul>
        <InlineError v-if="errors.targets" :message="errors.targets" />
        <div><UiButton icon="add" @click="addTarget">Add target</UiButton></div>
      </FieldGroup>

      <FieldGroup legend="Fallbacks" hint="Optional. Tried in this order after all targets fail. A fallback must be an existing model or route before the rule is enabled.">
        <ol class="m-0 flex list-none flex-col gap-2 p-0">
          <li v-for="(f, i) in fallbacks" :key="f.uid" class="flex items-end gap-2">
            <FormField :id="`rule-fallback-${i}`" v-slot="{ id: fieldId }" :label="`Fallback ${i + 1}`" class="min-w-0 flex-1">
              <input :id="fieldId" v-model="f.model" list="rule-model-names" class="field-input font-mono" autocomplete="off" spellcheck="false" />
            </FormField>
            <UiButton size="sm" icon="arrow_upward" icon-only :aria-label="`Move fallback ${i + 1} earlier`" :disabled="i === 0" @click="moveFallback(i, -1)" />
            <UiButton size="sm" icon="arrow_downward" icon-only :aria-label="`Move fallback ${i + 1} later`" :disabled="i === fallbacks.length - 1" @click="moveFallback(i, 1)" />
            <UiButton size="sm" variant="danger" icon="delete" icon-only :aria-label="`Remove fallback ${i + 1}`" @click="fallbacks.splice(i, 1)" />
          </li>
        </ol>
        <InlineError v-if="errors.fallbacks" :message="errors.fallbacks" />
        <div><UiButton icon="add" @click="addFallback">Add fallback</UiButton></div>
      </FieldGroup>

      <CheckField v-model="chain" label="Chain" description="After this rule, send the new model name through the rules again. Use it to rename a model before routing it." />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="rule-form" :loading="busy">{{ rule ? 'Save changes' : 'Create rule' }}</UiButton>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
