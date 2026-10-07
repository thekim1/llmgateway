<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { DATA_RESIDENCIES, PII_POLICIES, type CreateKeyRequest, type VirtualKey } from '@/api/types'
import { useKeysStore } from '@/stores/keys'
import { useTeamsStore } from '@/stores/teams'
import { useForm, useNotify } from '@/composables/useForm'
import AsyncState from './AsyncState.vue'
import FormField from './form/FormField.vue'
import ErrorSummary from './form/ErrorSummary.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import ShowSecretDialog from './ShowSecretDialog.vue'

const props = defineProps<{ existing?: VirtualKey; teamId?: string }>()
const emit = defineEmits<{ saved: [key: VirtualKey] }>()
const { t } = useI18n()
const keys = useKeysStore()
const teams = useTeamsStore()
const notify = useNotify()
const form = useForm('key', ['teamId', 'name', 'description', 'expiresAt', 'allowedModels', 'allowedResidencies', 'piiPolicy', 'requestsPerMinute', 'tokensPerMinute', 'isEnabled'] as const)
const { summary, busy } = form
const current = props.existing
const values = reactive({
  teamId: current?.teamId ?? props.teamId ?? '', name: current?.name ?? '', description: current?.description ?? '',
  allowedModels: current?.allowedModels.join(', ') ?? '', allowedResidencies: [...current?.allowedResidencies ?? []],
  piiPolicy: current?.piiPolicy ?? 'RerouteToOnPrem', requestsPerMinute: current?.requestsPerMinute?.toString() ?? '60',
  tokensPerMinute: current?.tokensPerMinute?.toString() ?? '', isEnabled: current?.isEnabled ?? true,
  expiresAt: current?.expiresAt ? localDate(current.expiresAt) : '',
})
const confirm = ref(false)
const secret = ref('')
const secretOpen = ref(false)
const savedKey = ref<VirtualKey | null>(null)
const expiryHelp = computed(() => t('keys.expiryHelp'))

function localDate(iso: string): string {
  const date = new Date(iso)
  return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
}
function validate(): void {
  if (!values.teamId) form.setError('teamId', t('common.required'))
  if (!values.name.trim()) form.setError('name', t('common.required'))
  if (values.expiresAt && (!Number.isFinite(new Date(values.expiresAt).getTime()) || new Date(values.expiresAt) <= new Date())) form.setError('expiresAt', t('common.invalid'))
  for (const field of ['requestsPerMinute', 'tokensPerMinute'] as const) {
    if (values[field] !== '' && (!Number.isInteger(Number(values[field])) || Number(values[field]) < 1)) form.setError(field, t('common.invalid'))
  }
}
function payload(): CreateKeyRequest {
  return {
    teamId: values.teamId, name: values.name.trim(), description: values.description || null,
    expiresAt: values.expiresAt ? new Date(values.expiresAt).toISOString() : null,
    allowedModels: values.allowedModels.split(',').map(v => v.trim()).filter(Boolean),
    allowedResidencies: [...values.allowedResidencies], piiPolicy: values.piiPolicy,
    requestsPerMinute: values.requestsPerMinute === '' ? null : Number(values.requestsPerMinute),
    tokensPerMinute: values.tokensPerMinute === '' ? null : Number(values.tokensPerMinute),
  }
}
async function review(): Promise<void> {
  if (!busy.value) await form.submit(validate, async () => { confirm.value = true })
}
async function save(): Promise<void> {
  if (busy.value) return
  const ok = await form.submit(validate, async () => {
    if (current) {
      const key = await keys.update(current.id, { ...payload(), isEnabled: values.isEnabled })
      emit('saved', key)
      notify.success(t('common.saved'))
    } else {
      const result = await keys.create(payload())
      secret.value = result.secret
      savedKey.value = result.key
      secretOpen.value = true
    }
    confirm.value = false
  })
  if (!ok) confirm.value = false
}
function discardSecret(): void {
  secret.value = ''
  if (savedKey.value) emit('saved', savedKey.value)
  savedKey.value = null
}
onBeforeRouteLeave(() => !secretOpen.value)
onBeforeUnmount(() => { secret.value = '' })
onMounted(() => { void teams.load() })
</script>

<template>
  <p>{{ t('keys.help') }}</p>
  <AsyncState :loading="teams.loading" :error="teams.error" @retry="teams.load">
    <form class="form" novalidate @submit.prevent="review">
      <ErrorSummary :id="form.summaryId" :items="summary" />
      <FormField v-if="!existing" :id="form.fieldId('teamId')" :label="t('fields.teamId')" :error="form.errors.teamId" :help="t('form.help', { field: t('fields.teamId') })" required>
        <template #default="{ id, describedBy, invalid }"><select :id="id" v-model="values.teamId" class="select" :disabled="!!teamId" :aria-describedby="describedBy" :aria-invalid="invalid || undefined"><option value="">{{ t('common.select') }}</option><option v-for="team in teams.items" :key="team.id" :value="team.id">{{ team.departmentName }} · {{ team.name }}</option></select></template>
      </FormField>
      <FormField :id="form.fieldId('name')" :label="t('fields.name')" :error="form.errors.name" :help="t('form.help', { field: t('fields.name') })" required><template #default="{ id, describedBy, invalid }"><input :id="id" v-model="values.name" class="input" maxlength="200" :aria-describedby="describedBy" :aria-invalid="invalid || undefined"></template></FormField>
      <FormField :id="form.fieldId('description')" :label="t('fields.description')" :error="form.errors.description" :help="t('form.help', { field: t('fields.description') })"><template #default="{ id, describedBy }"><textarea :id="id" v-model="values.description" class="textarea" maxlength="1000" :aria-describedby="describedBy"></textarea></template></FormField>
      <FormField :id="form.fieldId('expiresAt')" :label="t('fields.expiresAt')" :error="form.errors.expiresAt" :help="expiryHelp"><template #default="{ id, describedBy, invalid }"><input :id="id" v-model="values.expiresAt" class="input" type="datetime-local" :aria-describedby="describedBy" :aria-invalid="invalid || undefined"></template></FormField>
      <FormField :id="form.fieldId('allowedModels')" :label="t('fields.allowedModels')" :error="form.errors.allowedModels" :help="t('form.listHelp')"><template #default="{ id, describedBy }"><input :id="id" v-model="values.allowedModels" class="input" :aria-describedby="describedBy"></template></FormField>
      <FormField :id="form.fieldId('allowedResidencies')" :label="t('fields.allowedResidencies')" :error="form.errors.allowedResidencies" :help="t('form.listHelp')"><template #default="{ id, describedBy }"><select :id="id" v-model="values.allowedResidencies" multiple class="select" :aria-describedby="describedBy"><option v-for="residency in DATA_RESIDENCIES" :key="residency" :value="residency">{{ t(`enums.residency.${residency}`) }}</option></select></template></FormField>
      <FormField :id="form.fieldId('piiPolicy')" :label="t('fields.piiPolicy')" :error="form.errors.piiPolicy" :help="t('portal.privacy')" required><template #default="{ id, describedBy }"><select :id="id" v-model="values.piiPolicy" class="select" :aria-describedby="describedBy"><option v-for="policy in PII_POLICIES" :key="policy" :value="policy">{{ t(`enums.pii.${policy}`) }}</option></select></template></FormField>
      <FormField v-for="field in (['requestsPerMinute', 'tokensPerMinute'] as const)" :id="form.fieldId(field)" :key="field" :label="t(`fields.${field}`)" :error="form.errors[field]" :help="t('form.help', { field: t(`fields.${field}`) })"><template #default="{ id, describedBy, invalid }"><input :id="id" v-model="values[field]" class="input" type="number" min="1" step="1" :aria-describedby="describedBy" :aria-invalid="invalid || undefined"></template></FormField>
      <div v-if="existing" class="check"><input id="key-enabled" v-model="values.isEnabled" type="checkbox"><label for="key-enabled">{{ t('fields.isEnabled') }}</label></div>
      <button class="btn btn--primary" type="submit" :disabled="busy">{{ t('common.review') }}</button>
    </form>
  </AsyncState>
  <ConfirmDialog v-model:open="confirm" :title="t('common.review')" :description="t('common.confirmBody')" :confirm-label="t('common.confirm')" :acknowledge-label="t('common.acknowledge')" :busy="busy" tone="primary" @confirm="save">
    <dl class="details">
      <dt>{{ t('fields.name') }}</dt><dd>{{ values.name }}</dd>
      <dt>{{ t('fields.teamId') }}</dt><dd>{{ teams.items.find(v => v.id === values.teamId)?.name }}</dd>
      <dt>{{ t('fields.description') }}</dt><dd>{{ values.description || '—' }}</dd>
      <dt>{{ t('fields.expiresAt') }}</dt><dd>{{ values.expiresAt || '—' }}</dd>
      <dt>{{ t('fields.piiPolicy') }}</dt><dd>{{ t(`enums.pii.${values.piiPolicy}`) }}</dd>
      <dt>{{ t('fields.allowedModels') }}</dt><dd>{{ values.allowedModels || t('common.all') }}</dd>
      <dt>{{ t('fields.allowedResidencies') }}</dt><dd>{{ values.allowedResidencies.map(v => t(`enums.residency.${v}`)).join(', ') || t('common.all') }}</dd>
      <dt>{{ t('fields.requestsPerMinute') }}</dt><dd>{{ values.requestsPerMinute || '—' }}</dd>
      <dt>{{ t('fields.tokensPerMinute') }}</dt><dd>{{ values.tokensPerMinute || '—' }}</dd>
      <template v-if="existing"><dt>{{ t('fields.isEnabled') }}</dt><dd>{{ t(values.isEnabled ? 'common.yes' : 'common.no') }}</dd></template>
    </dl>
  </ConfirmDialog>
  <ShowSecretDialog v-model:open="secretOpen" :secret="secret" :key-name="values.name" @closed="discardSecret" />
</template>
