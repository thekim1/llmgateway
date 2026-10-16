<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { DATA_PERMISSIONS, type CreateDataApiKeyResponse, type DataApiKey, type DataPermission } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useDataAccessStore } from '@/stores/dataAccess'
import { DATA_PERMISSION } from '@/utils/labels'
import { dateInputToIso, isBlank, isoToDateInput } from '@/utils/validation'
import { todayIso } from '@/utils/format'
import CheckField from '@/components/ui/CheckField.vue'
import FieldGroup from '@/components/ui/FieldGroup.vue'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'

const props = defineProps<{ open: boolean; existing?: DataApiKey | null }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'created', result: CreateDataApiKeyResponse): void
  (e: 'saved', key: DataApiKey): void
}>()

const store = useDataAccessStore()
const ids = {
  name: 'data-key-name',
  description: 'data-key-description',
  permissions: 'data-key-permissions',
  expiresAt: 'data-key-expires',
} as const
const { errors, formError, clear, validate, applyServerError } = useFormErrors(ids)

const values = reactive({ name: '', description: '', permissions: [] as DataPermission[], expiresAt: '' })
const busy = ref(false)
const isEdit = computed(() => !!props.existing)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    const key = props.existing
    values.name = key?.name ?? ''
    values.description = key?.description ?? ''
    values.permissions = [...(key?.permissions ?? [])]
    values.expiresAt = isoToDateInput(key?.expiresAt)
  },
  { immediate: true },
)

function togglePermission(permission: DataPermission, on: boolean): void {
  values.permissions = on ? [...values.permissions, permission] : values.permissions.filter((p) => p !== permission)
}

function expiryError(): string | null {
  if (isBlank(values.expiresAt)) return null
  if (!dateInputToIso(values.expiresAt)) return 'Enter a valid expiry date.'
  return values.expiresAt < todayIso() ? 'Choose an expiry date that is today or later.' : null
}

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    name: isBlank(values.name) && 'Name the integration that will use this key, for example “Power BI finance”.',
    permissions: values.permissions.length === 0 && 'Choose at least one kind of data.',
    expiresAt: expiryError(),
  })
  if (!ok) return
  const body = {
    name: values.name.trim(),
    description: isBlank(values.description) ? null : values.description.trim(),
    permissions: DATA_PERMISSIONS.filter((p) => values.permissions.includes(p)),
    expiresAt: dateInputToIso(values.expiresAt),
  }
  busy.value = true
  try {
    if (props.existing) {
      const key = await store.update(props.existing.id, body)
      emit('update:open', false)
      emit('saved', key)
    } else {
      const result = await store.create(body)
      emit('update:open', false)
      emit('created', result)
    }
  } catch (e) {
    applyServerError(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDrawer
    :open="open"
    wide
    :title="isEdit ? `Edit ${existing?.name ?? 'Data API key'}` : 'New Data API key'"
    :subtitle="existing?.prefix"
    :description="isEdit ? 'Change the key’s name, data access or expiry.' : 'Create a key for a reporting or security integration. The secret is shown once.'"
    @update:open="emit('update:open', $event)"
  >
    <form id="data-key-form" class="flex flex-col gap-8" novalidate @submit.prevent="submit">
      <InlineError v-if="formError" :message="formError" />

      <FieldGroup legend="Basics">
        <TextField :id="ids.name" v-model="values.name" label="Name" autocomplete="off" :error="errors.name" hint="Names the integration in the Data API’s access log (as key:name)." />
        <TextField :id="ids.description" v-model="values.description" label="Description (optional)" :error="errors.description" hint="For example who owns the integration." />
      </FieldGroup>

      <FieldGroup legend="Data access" hint="Give each integration only the data it needs. The Data API never returns prompts, responses or secrets.">
        <div :id="ids.permissions" class="flex flex-col gap-1" tabindex="-1">
          <CheckField
            v-for="permission in DATA_PERMISSIONS"
            :key="permission"
            :label="`${DATA_PERMISSION[permission].label} (${permission})`"
            :description="DATA_PERMISSION[permission].description"
            :model-value="values.permissions.includes(permission)"
            @update:model-value="togglePermission(permission, $event)"
          />
          <span v-if="errors.permissions" class="text-small text-danger">{{ errors.permissions }}</span>
        </div>
      </FieldGroup>

      <FieldGroup legend="Expiry">
        <TextField
          :id="ids.expiresAt"
          v-model="values.expiresAt"
          type="date"
          label="Expiry date"
          :min="todayIso()"
          :error="errors.expiresAt"
          hint="The key stops working at the end of this day. Leave empty for no expiry."
        />
      </FieldGroup>
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="data-key-form" :loading="busy">{{ isEdit ? 'Save key' : 'Create key' }}</UiButton>
      <UiButton :disabled="busy" @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
