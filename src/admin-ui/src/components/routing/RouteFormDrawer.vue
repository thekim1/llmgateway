<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ModelKind, Route } from '@/api/types'
import { MODEL_KINDS } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useModelsStore } from '@/stores/models'
import { useRoutesStore } from '@/stores/routes'
import { useUiStore } from '@/stores/ui'
import { KIND_LABEL } from '@/utils/labels'
import CheckField from '../ui/CheckField.vue'
import FieldGroup from '../ui/FieldGroup.vue'
import InlineError from '../ui/InlineError.vue'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'
import UiButton from '../ui/UiButton.vue'
import UiDrawer from '../ui/UiDrawer.vue'

interface TargetDraft {
  uid: number
  modelId: string
  priority: string
  weight: string
}

const props = defineProps<{ open: boolean; route: Route | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', route: Route): void }>()

const routes = useRoutesStore()
const models = useModelsStore()
const ui = useUiStore()
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors({
  name: 'route-name',
  description: 'route-desc',
  kind: 'route-kind',
  targets: 'route-target-0',
})

const name = ref('')
const description = ref('')
const kind = ref<ModelKind>('Chat')
const isEnabled = ref(true)
const targets = ref<TargetDraft[]>([])
const busy = ref(false)
let nextUid = 1

const kindOptions = MODEL_KINDS.map((k) => ({ value: k, label: KIND_LABEL[k] }))
const modelOptions = computed(() =>
  models.items
    .filter((m) => m.kind === kind.value)
    .map((m) => ({ value: m.id, label: `${m.name} · ${m.providerName}${m.isEnabled ? '' : ' (disabled)'}` })),
)

function addTarget(): void {
  targets.value.push({ uid: nextUid++, modelId: '', priority: String(targets.value.length), weight: '1' })
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    name.value = props.route?.name ?? ''
    description.value = props.route?.description ?? ''
    kind.value = props.route?.kind ?? 'Chat'
    isEnabled.value = props.route?.isEnabled ?? true
    targets.value = (props.route?.targets ?? []).map((t) => ({ uid: nextUid++, modelId: t.modelId, priority: String(t.priority), weight: String(t.weight) }))
    if (!props.route) addTarget()
  },
  { immediate: true },
)

watch(kind, () => {
  const valid = new Set(modelOptions.value.map((o) => o.value))
  for (const t of targets.value) if (!valid.has(t.modelId)) t.modelId = ''
})

function targetProblem(): string | false {
  if (targets.value.length === 0) return 'Add at least one target.'
  for (const [i, t] of targets.value.entries()) {
    const priority = Number(t.priority)
    const weight = Number(t.weight)
    if (!t.modelId) return `Choose a model for target ${i + 1}.`
    if (t.priority.trim() === '' || !Number.isInteger(priority) || priority < 0) return `Priority for target ${i + 1} must be a whole number, 0 or higher.`
    if (t.weight.trim() === '' || !Number.isInteger(weight) || weight < 1) return `Weight for target ${i + 1} must be a whole number, 1 or higher.`
  }
  return false
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    name: !name.value.trim() && 'Give the route a name. Clients use it as the model name.',
    targets: targetProblem(),
  })
  if (!ok) return
  busy.value = true
  try {
    const body = {
      name: name.value.trim(),
      description: description.value.trim() || null,
      kind: kind.value,
      isEnabled: isEnabled.value,
      targets: targets.value.map((t) => ({ modelId: t.modelId, priority: Number(t.priority), weight: Number(t.weight) })),
    }
    const saved = props.route ? await routes.update(props.route.id, body) : await routes.create(body)
    ui.notify('Route saved')
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
  <UiDrawer wide :open="open" :title="route ? 'Edit route' : 'New route'" @update:open="emit('update:open', $event)">
    <form id="route-form" class="flex flex-col gap-6" novalidate @submit.prevent="submit">
      <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />
      <TextField id="route-name" v-model="name" label="Name" mono :error="errors.name" hint="The model name clients put in their requests." />
      <TextField id="route-desc" v-model="description" label="Description" hint="Optional." :error="errors.description" />
      <SelectField id="route-kind" v-model="kind" label="Kind" :options="kindOptions" :error="errors.kind" />
      <CheckField v-model="isEnabled" label="Enabled" description="Turn off to stop serving this route." />

      <FieldGroup legend="Targets" hint="The lowest priority number is tried first. Targets with the same priority share traffic by weight.">
        <ul class="m-0 flex list-none flex-col gap-3 p-0">
          <li v-for="(t, i) in targets" :key="t.uid" role="group" :aria-label="`Target ${i + 1}`" class="flex flex-col gap-3 rounded-tile bg-surface-2 p-4">
            <SelectField :id="`route-target-${i}`" v-model="t.modelId" label="Model" :options="modelOptions" placeholder="Choose a model" />
            <div class="grid grid-cols-2 gap-3">
              <TextField v-model="t.priority" label="Priority" type="number" min="0" inputmode="numeric" />
              <TextField v-model="t.weight" label="Weight" type="number" min="1" inputmode="numeric" />
            </div>
            <div><UiButton size="sm" variant="danger" icon="delete" :aria-label="`Remove target ${i + 1}`" @click="targets.splice(i, 1)">Remove</UiButton></div>
          </li>
        </ul>
        <InlineError v-if="errors.targets" :message="errors.targets" />
        <div><UiButton icon="add" @click="addTarget">Add target</UiButton></div>
      </FieldGroup>
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="route-form" :loading="busy">{{ route ? 'Save changes' : 'Create route' }}</UiButton>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
