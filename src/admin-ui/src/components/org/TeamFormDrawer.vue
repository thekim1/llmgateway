<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Department, Team } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useTeamsStore } from '@/stores/teams'
import { useUiStore } from '@/stores/ui'
import { formatUsdCost } from '@/utils/format'
import { problemMessage } from '@/utils/problem'
import { emptyPolicyDraft, fromPolicyDraft, toPolicyDraft, type PolicyErrors } from '../policy/policyDraft'
import RequestPolicyFields from '../policy/RequestPolicyFields.vue'
import AppIcon from '../ui/AppIcon.vue'
import CheckField from '../ui/CheckField.vue'
import InlineError from '../ui/InlineError.vue'
import SelectField from '../ui/SelectField.vue'
import TextField from '../ui/TextField.vue'
import UiButton from '../ui/UiButton.vue'
import UiDrawer from '../ui/UiDrawer.vue'

const props = defineProps<{
  open: boolean
  team: Team | null
  /** Departments the user may add teams to. */
  departments: Department[]
  defaultDepartmentId?: string | null
}>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', team: Team): void }>()

const teams = useTeamsStore()
const ui = useUiStore()
const router = useRouter()
const { errors, formError, clear, validate, applyServerError } = useFormErrors({
  departmentId: 'team-dept',
  name: 'team-name',
  description: 'team-desc',
  code: 'team-code',
  budget: 'team-budget',
  requestPolicy: 'team-policy-max-output',
})

const step = ref<'form' | 'done'>('form')
const departmentId = ref('')
const name = ref('')
const description = ref('')
const code = ref('')
const isActive = ref(true)
const budget = ref('')
const policy = ref(emptyPolicyDraft())
const policyErrors = ref<PolicyErrors>({})
const busy = ref(false)
const created = ref<Team | null>(null)
const budgetLimit = ref<number | null>(null)
const budgetError = ref<unknown>(null)

const departmentOptions = computed(() => props.departments.map((d) => ({ value: d.id, label: d.name })))
const title = computed(() => (step.value === 'done' ? 'Team created' : props.team ? 'Edit team' : 'Add team'))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    step.value = 'form'
    created.value = null
    budgetError.value = null
    budgetLimit.value = null
    departmentId.value = props.team?.departmentId ?? props.defaultDepartmentId ?? props.departments[0]?.id ?? ''
    name.value = props.team?.name ?? ''
    description.value = props.team?.description ?? ''
    code.value = props.team?.code ?? ''
    isActive.value = props.team?.isActive ?? true
    budget.value = ''
    policy.value = toPolicyDraft(props.team?.requestPolicy)
    policyErrors.value = {}
  },
  { immediate: true },
)

function budgetProblem(): string | false {
  if (props.team || budget.value.trim() === '') return false
  const value = Number(budget.value.replace(',', '.'))
  return Number.isFinite(value) && value > 0 ? false : 'Enter a budget above 0, or leave it empty.'
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    departmentId: !props.team && !departmentId.value && 'Choose the department this team belongs to.',
    name: !name.value.trim() && 'Give the team a name.',
    budget: budgetProblem(),
  })
  const parsed = fromPolicyDraft(policy.value)
  policyErrors.value = parsed.errors
  if (!ok || Object.keys(parsed.errors).length > 0) return
  busy.value = true
  try {
    const desc = description.value.trim() || null
    if (props.team) {
      const saved = await teams.update(props.team.id, { name: name.value.trim(), description: desc, isActive: isActive.value, requestPolicy: parsed.policy, code: code.value.trim() })
      ui.notify('Team saved')
      emit('saved', saved)
      emit('update:open', false)
      return
    }
    const team = await teams.create({ departmentId: departmentId.value, name: name.value.trim(), description: desc, requestPolicy: parsed.policy, code: code.value.trim() || null })
    created.value = team
    emit('saved', team)
    ui.notify('Team created')
    if (budget.value.trim() !== '') {
      const limit = Number(budget.value.replace(',', '.'))
      try {
        await api.budgets.create({ scope: 'Team', scopeId: team.id, limitUsd: limit, period: 'Monthly', alertThresholds: [50, 80, 100], isActive: true })
        budgetLimit.value = limit
      } catch (e) {
        budgetError.value = e
      }
    }
    step.value = 'done'
  } catch (e) {
    applyServerError(e)
  } finally {
    busy.value = false
  }
}

function go(name: 'keys' | 'budgets'): void {
  emit('update:open', false)
  void router.push({ name })
}
</script>

<template>
  <UiDrawer :open="open" :title="title" @update:open="emit('update:open', $event)">
    <form v-if="step === 'form'" id="team-form" class="flex flex-col gap-5" novalidate @submit.prevent="submit">
      <InlineError v-if="formError" :message="formError" />
      <SelectField
        v-if="!team"
        id="team-dept"
        v-model="departmentId"
        label="Department"
        :options="departmentOptions"
        placeholder="Choose a department"
        :error="errors.departmentId"
      />
      <TextField id="team-name" v-model="name" label="Name" :error="errors.name" />
      <TextField id="team-desc" v-model="description" label="Description" hint="Optional. What the team uses the gateway for." :error="errors.description" />
      <TextField
        id="team-code"
        v-model="code"
        label="Team code"
        mono
        hint="Optional. Team admins with this code in their teamCodes claim manage this team's keys themselves. Exact match, unique across teams."
        :error="errors.code"
      />
      <TextField
        v-if="!team"
        id="team-budget"
        v-model="budget"
        label="Monthly budget (USD)"
        inputmode="decimal"
        hint="Optional. You get alerts at 50, 80 and 100 percent. You can change it later under Budgets."
        :error="errors.budget"
      />
      <CheckField v-if="team" v-model="isActive" label="Active" description="Turn off to retire this team." />
      <RequestPolicyFields
        v-model="policy"
        id-prefix="team-policy"
        :errors="policyErrors"
        hint="Optional. Applies to every key of the team; a key's own policy wins where it sets something."
      />
    </form>
    <div v-else class="flex flex-col gap-5">
      <p class="flex items-start gap-2"><AppIcon name="check_circle" filled class="mt-px text-ok" /><span><span lang="sv" class="font-medium">{{ created?.name }}</span> was added to <span lang="sv">{{ created?.departmentName }}</span>.</span></p>
      <p v-if="budgetLimit !== null" class="text-fg-2">Monthly budget set to {{ formatUsdCost(budgetLimit) }}.</p>
      <InlineError
        v-if="budgetError"
        tone="warn"
        title="The team exists, but its budget was not saved"
        :message="problemMessage(budgetError)"
      >
        <p>Set it under Budgets &amp; alerts.</p>
      </InlineError>
      <p class="text-fg-2">The team needs a key before it can send requests.</p>
    </div>
    <template #footer>
      <template v-if="step === 'form'">
        <UiButton variant="primary" type="submit" form="team-form" :loading="busy">{{ team ? 'Save changes' : 'Create team' }}</UiButton>
        <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      </template>
      <template v-else>
        <UiButton icon="key" @click="go('keys')">Create key</UiButton>
        <UiButton v-if="budgetError" @click="go('budgets')">Set budget</UiButton>
        <UiButton variant="quiet" @click="emit('update:open', false)">Done</UiButton>
      </template>
    </template>
  </UiDrawer>
</template>
