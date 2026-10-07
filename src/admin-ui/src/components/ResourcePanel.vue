<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { http } from '@/api/client'
import type { RouteTargetRequest } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { useForm, useNotify } from '@/composables/useForm'
import { resourceFields, resourceColumns, type Field, type Resource } from './resourceFields'
import AsyncState from './AsyncState.vue'
import ScrollTable from './ScrollTable.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import FormField from './form/FormField.vue'
import FieldGroup from './form/FieldGroup.vue'
import ErrorSummary from './form/ErrorSummary.vue'
import BudgetMeter from './BudgetMeter.vue'

const props = defineProps<{ resource: Resource; title: string; endpoint?: string; createOnly?: boolean; initial?: Record<string, string | number | boolean | string[]> }>()
const emit = defineEmits<{ saved: [record: Record<string, unknown>] }>()
const { t } = useI18n()
const auth = useAuthStore()
const notify = useNotify()
const endpoint = computed(() => props.endpoint ?? `/api/${props.resource}`)
const fields = computed(() => resourceFields[props.resource])
const columns = computed(() => resourceColumns[props.resource])
const loadable = useLoadable<Record<string, unknown>[]>([])
const { data: rows, loading, error } = loadable
const editing = ref<string | null>(null)
const deleting = ref<Record<string, unknown> | null>(null)
const deletingBusy = ref(false)
const confirmSave = ref(false)
const clearCredential = ref(false)
const values = reactive<Record<string, string | number | boolean | string[]>>({})
const targets = ref<(RouteTargetRequest & { uid: number })[]>([])
let nextTarget = 1
const lookups = reactive<Record<string, { id: string; name: string; kind?: string }[]>>({})
const form = useForm(`${props.resource}-form`, resourceFields[props.resource].map(f => f.name))
const { summary, busy } = form
const canEdit = computed(() => props.resource === 'departments' ? auth.isGatewayAdmin : auth.canManage)
const priceModel = ref<{ id: string; name: string } | null>(null)
const reviewRows = computed(() => fields.value.filter(f => !f.immutable || !editing.value).map(f => ({
  label: label(f.name), value: f.type === 'password' ? (values[f.name] ? '********' : '—') : display(payload()[f.name]),
})))

function label(name: string): string {
  const aliases: Record<string, string> = { departmentName: 'departmentId', providerName: 'providerId', scopeName: 'scopeId', hasCredential: 'credential' }
  return t(`fields.${aliases[name] ?? name}`)
}
function display(value: unknown): string {
  if (typeof value === 'boolean') return t(value ? 'common.yes' : 'common.no')
  if (value == null || value === '') return '—'
  if (Array.isArray(value)) return value.map(v => typeof v === 'object' && v !== null ? ('modelName' in v ? String(v.modelName) : JSON.stringify(v)) : String(v)).join(', ')
  return String(value)
}
function options(field: Field): { id: string; name: string }[] {
  if (field.source === 'scopes') {
    const source = values.scope === 'Department' ? 'departments' : values.scope === 'VirtualKey' ? 'keys' : 'teams'
    return lookups[source] ?? []
  }
  return field.source ? lookups[field.source] ?? [] : (field.options ?? []).map(v => ({ id: v, name: v }))
}
function reset(): void {
  editing.value = null
  clearCredential.value = false
  for (const key of Object.keys(values)) delete values[key]
  for (const field of fields.value) values[field.name] = Array.isArray(field.default) ? [...field.default] : field.default ?? ''
  Object.assign(values, props.initial)
  targets.value = []
  form.clear()
}
async function load(): Promise<void> {
  await loadable.run(async () => {
    const sources = new Set(fields.value.flatMap(f => f.source && f.source !== 'scopes' ? [f.source] : []))
    if (props.resource === 'budgets') { sources.add('departments'); sources.add('teams') }
    await Promise.all([...sources].map(async source => { lookups[source] = await http.get<{ id: string; name: string; kind?: string }[]>(`/api/${source}`) }))
    if (props.resource === 'budgets') lookups.keys = await api.keys.list()
    return props.createOnly ? [] : await http.get<Record<string, unknown>[]>(endpoint.value)
  })
}
function edit(row: Record<string, unknown>): void {
  reset()
  editing.value = String(row.id)
  for (const field of fields.value) {
    const value = row[field.name]
    if (field.type === 'password' || field.type === 'targets') continue
    if (Array.isArray(value)) values[field.name] = field.type === 'multi' ? value.map(String) : value.join(',')
    else if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') values[field.name] = value
  }
  if (Array.isArray(row.targets)) {
    targets.value = row.targets.flatMap(item => {
      if (typeof item !== 'object' || item === null || !('modelId' in item)) return []
      return [{ uid: nextTarget++, modelId: String(item.modelId), priority: Number(item.priority), weight: Number(item.weight) }]
    })
  }
  void nextTick(() => document.getElementById(`${props.resource}-editor`)?.focus())
}
function payload(): Record<string, unknown> {
  const body: Record<string, unknown> = {}
  for (const field of fields.value) {
    const value = values[field.name]
    if (field.immutable && editing.value) continue
    if (field.type === 'password') {
      if (clearCredential.value) body[field.name] = ''
      else if (value) body[field.name] = value
    } else if (field.type === 'targets') {
      body.targets = targets.value.map(({ modelId, priority, weight }) => ({ modelId, priority: Number(priority), weight: Number(weight) }))
    } else if (field.name === 'alertThresholds') {
      body[field.name] = String(value).split(',').filter(v => v.trim()).map(Number)
    } else if (field.type === 'datetime-local') {
      body[field.name] = value ? new Date(String(value)).toISOString() : null
    } else if (field.type === 'number') {
      body[field.name] = value === '' ? null : Number(value)
    } else {
      body[field.name] = value
    }
  }
  return body
}
function validate(): void {
  for (const field of fields.value) {
    const value = values[field.name]
    if (field.required && !field.immutable && field.type !== 'targets' && (value === '' || value == null)) form.setError(field.name, t('common.required'))
    if (field.type === 'number' && value !== '' && (!Number.isFinite(Number(value)) || (field.min !== undefined && Number(value) < field.min) || (field.max !== undefined && Number(value) > field.max))) form.setError(field.name, t('common.invalid'))
    if (field.type === 'datetime-local' && value && Number.isNaN(new Date(String(value)).getTime())) form.setError(field.name, t('common.invalid'))
    if (field.required && field.immutable && !editing.value && !value) form.setError(field.name, t('common.required'))
  }
  if (props.resource === 'routes' && (targets.value.length === 0 || targets.value.some(v => !v.modelId || v.priority < 0 || v.weight < 1))) form.setError('targets', t('common.required'))
  if (props.resource === 'budgets' && String(values.alertThresholds).split(',').some(v => !Number.isInteger(Number(v)) || Number(v) < 1 || Number(v) > 100)) form.setError('alertThresholds', t('common.invalid'))
}
async function review(): Promise<void> {
  if (busy.value) return
  await form.submit(validate, async () => { confirmSave.value = true })
}
async function save(): Promise<void> {
  if (busy.value) return
  const succeeded = await form.submit(validate, async () => {
    const result = editing.value ? await http.put<Record<string, unknown>>(`${endpoint.value}/${encodeURIComponent(editing.value)}`, payload()) :
      await http.post<Record<string, unknown>>(endpoint.value, payload())
    confirmSave.value = false
    reset()
    emit('saved', result)
    notify.success(t('common.saved'))
    await load()
  })
  if (!succeeded) confirmSave.value = false
}
async function remove(): Promise<void> {
  if (!deleting.value || deletingBusy.value) return
  deletingBusy.value = true
  try {
    await http.del(`${endpoint.value}/${encodeURIComponent(String(deleting.value.id))}`)
    deleting.value = null
    notify.success(t('common.saved'))
    await load()
  } catch (e) { notify.error(e) } finally { deletingBusy.value = false }
}
function move(index: number, direction: number): void {
  const next = index + direction
  if (next < 0 || next >= targets.value.length) return
  const item = targets.value.splice(index, 1)[0]
  if (item) targets.value.splice(next, 0, item)
  targets.value.forEach((item, i) => { item.priority = i })
}
watch(() => props.endpoint, () => { reset(); void load() })
onMounted(() => { reset(); void load() })
</script>

<template>
  <section :aria-labelledby="`${resource}-title`">
    <h2 :id="`${resource}-title`">{{ title }}</h2>
    <AsyncState :loading="loading" :error="error" @retry="load">
      <p v-if="!rows.length && !createOnly">{{ t('common.empty') }}. {{ t('common.emptyHelp') }}</p>
      <ScrollTable v-if="rows.length" :label="title">
        <table class="table">
          <caption>{{ title }}</caption>
          <thead><tr><th v-for="column in columns" :key="column" scope="col">{{ label(column) }}</th><th v-if="canEdit && resource !== 'prices'" scope="col">{{ t('common.actions') }}</th></tr></thead>
          <tbody><tr v-for="(row, index) in rows" :key="String(row.id ?? row.effectiveFrom ?? index)">
            <th scope="row">{{ display(row[columns[0] ?? 'name']) }}</th>
            <td v-for="column in columns.slice(1)" :key="column">{{ display(row[column]) }}</td>
            <td v-if="canEdit && resource !== 'prices'"><div class="actions">
              <button class="btn btn--secondary" type="button" @click="edit(row)">{{ t('common.edit') }} {{ display(row.name ?? row.scopeName) }}</button>
              <button class="btn btn--danger" type="button" @click="deleting = row">{{ t('common.remove') }} {{ display(row.name ?? row.scopeName) }}</button>
              <button v-if="resource === 'models'" class="btn btn--secondary" type="button" @click="priceModel = { id: String(row.id), name: String(row.name) }">{{ t('form.addPrice') }} {{ display(row.name) }}</button>
            </div></td>
          </tr></tbody>
        </table>
      </ScrollTable>
      <template v-if="resource === 'budgets'"><BudgetMeter v-for="row in rows" :key="String(row.id)" :label="String(row.scopeName)" :spent="Number(row.spentSek)" :limit="Number(row.limitSek)" /></template>
    </AsyncState>
    <template v-if="canEdit">
      <h3 :id="`${resource}-editor`" tabindex="-1">{{ t(editing ? 'common.edit' : 'common.create') }} · {{ title }}</h3>
      <form class="form" novalidate @submit.prevent="review">
        <ErrorSummary :id="form.summaryId" :items="summary" :level="3" />
        <template v-for="field in fields" :key="field.name">
          <FieldGroup v-if="field.type === 'targets'" :id="form.fieldId(field.name)" :legend="label(field.name)" :help="t('form.targetHelp')" :error="form.errors[field.name]">
            <ol class="reorder-list">
              <li v-for="(target, index) in targets" :key="target.uid">
                <div>
                  <label :for="`target-${target.uid}-model`">{{ t('fields.modelId') }} {{ index + 1 }}</label>
                  <select :id="`target-${target.uid}-model`" v-model="target.modelId" class="select"><option value="">{{ t('common.select') }}</option><option v-for="model in (lookups.models ?? []).filter(m => m.kind === values.kind)" :key="model.id" :value="model.id">{{ model.name }}</option></select>
                  <label :for="`target-${target.uid}-priority`">{{ t('fields.priority') }} {{ index + 1 }}</label><input :id="`target-${target.uid}-priority`" v-model.number="target.priority" class="input" type="number" min="0">
                  <label :for="`target-${target.uid}-weight`">{{ t('fields.weight') }} {{ index + 1 }}</label><input :id="`target-${target.uid}-weight`" v-model.number="target.weight" class="input" type="number" min="1">
                </div>
                <div class="actions">
                  <button class="btn btn--secondary" type="button" :disabled="index === 0" @click="move(index, -1)">{{ t('form.up') }} {{ index + 1 }}</button>
                  <button class="btn btn--secondary" type="button" :disabled="index === targets.length - 1" @click="move(index, 1)">{{ t('form.down') }} {{ index + 1 }}</button>
                  <button class="btn btn--danger" type="button" @click="targets.splice(index, 1)">{{ t('form.removeTarget') }} {{ index + 1 }}</button>
                </div>
              </li>
            </ol>
            <button class="btn btn--secondary" type="button" @click="targets.push({ uid: nextTarget++, modelId: '', priority: targets.length, weight: 1 })">{{ t('form.addTarget') }}</button>
          </FieldGroup>
          <FormField v-else-if="!field.immutable || !editing" :id="form.fieldId(field.name)" :label="label(field.name)" :error="form.errors[field.name]" :required="field.required" :help="field.type === 'password' ? t('form.credentialHelp') : t('form.help', { field: label(field.name).toLowerCase() })">
            <template #default="{ id, describedBy, invalid }">
              <select v-if="field.type === 'select' || field.type === 'multi'" :id="id" v-model="values[field.name]" class="select" :multiple="field.type === 'multi'" :aria-describedby="describedBy" :aria-invalid="invalid || undefined">
                <option v-if="field.type === 'select'" value="">{{ t('common.select') }}</option><option v-for="option in options(field)" :key="option.id" :value="option.id">{{ option.name }}</option>
              </select>
              <input v-else :id="id" v-model="values[field.name]" :class="field.type === 'checkbox' ? undefined : 'input'" :type="field.type" :min="field.min" :max="field.max" :step="field.type === 'number' ? 'any' : undefined" :autocomplete="field.type === 'password' ? 'new-password' : 'off'" :aria-describedby="describedBy" :aria-invalid="invalid || undefined">
            </template>
          </FormField>
        </template>
        <div v-if="resource === 'providers' && editing" class="check"><input id="remove-credential" v-model="clearCredential" type="checkbox"><label for="remove-credential">{{ t('form.clearCredential') }}</label></div>
        <p v-if="resource === 'prices'" class="help">{{ t('form.priceHelp') }}</p>
        <div class="form-actions"><button class="btn btn--primary" type="submit" :disabled="busy">{{ t('common.review') }}</button><button v-if="editing" class="btn btn--secondary" type="button" @click="reset">{{ t('common.cancel') }}</button></div>
      </form>
    </template>
    <ConfirmDialog v-model:open="confirmSave" :title="t('common.review')" :description="t('common.confirmBody')" :confirm-label="t('common.confirm')" :acknowledge-label="t('common.acknowledge')" tone="primary" :busy="busy" @confirm="save">
      <dl class="details"><template v-for="row in reviewRows" :key="row.label"><dt>{{ row.label }}</dt><dd>{{ row.value }}</dd></template></dl>
    </ConfirmDialog>
    <ConfirmDialog :open="!!deleting" :title="t('common.remove') + ' ' + display(deleting?.name ?? deleting?.scopeName)" :description="t('common.deleteBody')" :confirm-label="t('common.remove')" :acknowledge-label="t('common.acknowledge')" :busy="deletingBusy" @update:open="!$event && (deleting = null)" @confirm="remove" />
    <ResourcePanel v-if="priceModel" resource="prices" :title="t('nav.models') + ': ' + priceModel.name" :endpoint="`/api/models/${priceModel.id}/prices`" />
  </section>
</template>
