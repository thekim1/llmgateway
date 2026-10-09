<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { RoutingRule, RoutingScope } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useModelOptions } from '@/composables/useModelOptions'
import { useDepartmentsStore } from '@/stores/departments'
import { useKeysStore } from '@/stores/keys'
import { useRoutingRulesStore } from '@/stores/routingRules'
import { useTeamsStore } from '@/stores/teams'
import { useUiStore } from '@/stores/ui'
import { ROUTING_SCOPE_HINT, ROUTING_SCOPE_LABEL } from '@/utils/labels'
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
import ConditionEditor, { type ConditionStatus } from './ConditionEditor.vue'

interface TargetDraft {
  uid: number
  model: string
  weight: string
}
interface FallbackDraft {
  uid: number
  model: string
}

const props = defineProps<{ open: boolean; rule: RoutingRule | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', rule: RoutingRule): void }>()

const rules = useRoutingRulesStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const keys = useKeysStore()
const ui = useUiStore()
const models = useModelOptions()
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors({
  name: 'rule-name',
  scopeId: 'rule-owner',
  condition: 'rule-condition',
  targets: 'rule-target-0',
  fallbacks: 'rule-fallback-0',
})

const name = ref('')
const description = ref('')
const scope = ref<RoutingScope>('Global')
const scopeId = ref('')
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

const ownerOptions = computed(() => {
  const options =
    scope.value === 'Department'
      ? departments.items.map((d) => ({ value: d.id, label: d.name }))
      : scope.value === 'Team'
        ? teams.items.map((t) => ({ value: t.id, label: `${t.name} · ${t.departmentName}` }))
        : keys.items.map((k) => ({ value: k.id, label: `${k.name} · ${k.prefix}` }))
  // An orphaned rule still points at the removed owner: keep that value selectable so saving does not silently change it.
  if (props.rule?.isOrphaned && props.rule.scope === scope.value && props.rule.scopeId && !options.some((o) => o.value === props.rule!.scopeId)) {
    options.unshift({ value: props.rule.scopeId, label: 'No owner (removed)' })
  }
  return options
})
const ownerChanged = computed(() => !props.rule || scope.value !== props.rule.scope || scopeId.value !== (props.rule.scopeId ?? ''))
/** A rule without an owner cannot be switched on until it has a new one. */
const enableLocked = computed(() => !!props.rule?.isOrphaned && !ownerChanged.value)

async function loadOwners(next: RoutingScope): Promise<void> {
  if (next === 'Department') await departments.load()
  else if (next === 'Team') await teams.load()
  else if (next === 'VirtualKey') await keys.load()
}

function setScope(next: RoutingScope): void {
  scope.value = next
  scopeId.value = ''
  void loadOwners(next)
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
    name.value = rule?.name ?? ''
    description.value = rule?.description ?? ''
    scope.value = rule?.scope ?? 'Global'
    scopeId.value = rule?.scopeId ?? ''
    isEnabled.value = rule ? rule.isEnabled : true
    chain.value = rule?.chain ?? false
    condition.value = rule?.condition ?? ''
    targets.value = (rule?.targets ?? []).map((t) => ({ uid: nextUid++, model: t.model, weight: String(t.weight) }))
    fallbacks.value = (rule?.fallbacks ?? []).map((m) => ({ uid: nextUid++, model: m }))
    if (!rule) addTarget()
    void loadOwners(scope.value)
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

function nextPriority(): number {
  const sameOwner = rules.items.filter((r) => r.scope === scope.value && (r.scopeId ?? '') === (scope.value === 'Global' ? '' : scopeId.value))
  return sameOwner.length === 0 ? 0 : Math.max(...sameOwner.map((r) => r.priority)) + 10
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    name: isBlank(name.value) && 'Give the rule a name.',
    scopeId: scope.value !== 'Global' && !scopeId.value && `Choose the ${ROUTING_SCOPE_LABEL[scope.value].toLowerCase()} this rule applies to.`,
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
      scopeId: scope.value === 'Global' ? null : scopeId.value,
      isEnabled: enableLocked.value ? false : isEnabled.value,
      priority: props.rule && !ownerChanged.value ? props.rule.priority : nextPriority(),
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
      <TextField id="rule-name" v-model="name" label="Name" :error="errors.name" hint="Shown in lists, the audit log and usage. Must be unique for the same owner." />
      <TextField id="rule-desc" v-model="description" label="Description" hint="Optional. Say why the rule exists." />

      <FieldGroup legend="Applies to" :hint="ROUTING_SCOPE_HINT[scope]">
        <SegmentedControl :model-value="scope" label="Scope" mode="radio" :options="scopeOptions" @update:model-value="setScope($event)" />
        <SelectField
          v-if="scope !== 'Global'"
          id="rule-owner"
          v-model="scopeId"
          :label="ROUTING_SCOPE_LABEL[scope]"
          :options="ownerOptions"
          :placeholder="`Choose a ${ROUTING_SCOPE_LABEL[scope].toLowerCase()}`"
          :error="errors.scopeId"
        />
      </FieldGroup>

      <ConditionEditor id="rule-condition" v-model="condition" :error="errors.condition" @status="conditionStatus = $event" />

      <FieldGroup legend="Send to" hint="Name a route alias or a model. With more than one target, traffic is split by weight and the others take over if one fails.">
        <datalist id="rule-model-names"><option v-for="n in modelNames" :key="n" :value="n" /></datalist>
        <ul class="m-0 flex list-none flex-col gap-3 p-0">
          <li v-for="(t, i) in targets" :key="t.uid"><div role="group" :aria-label="`Target ${i + 1}`" class="flex flex-col gap-3 rounded-tile bg-surface-2 p-4">
            <FormField :id="`rule-target-${i}`" v-slot="{ id: fieldId }" label="Model or route" :hint="!chain && t.model.trim() && !known.has(t.model.trim().toLowerCase()) ? 'Not an existing model or route. Only a chained rule may point at a name another rule handles.' : undefined">
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

      <FieldGroup legend="Fallbacks" hint="Optional. Tried in this order after all targets fail. A fallback must be an existing model or route.">
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
      <CheckField v-model="isEnabled" label="Enabled" :disabled="enableLocked" :description="enableLocked ? 'This rule has no owner. Choose a new owner above to enable it.' : 'Turn off to keep the rule without applying it.'" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="rule-form" :loading="busy">{{ rule ? 'Save changes' : 'Create rule' }}</UiButton>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
