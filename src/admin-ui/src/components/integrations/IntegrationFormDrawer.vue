<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { Integration } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useIntegrationsStore } from '@/stores/integrations'
import { useTeamsStore } from '@/stores/teams'
import { isBlank } from '@/utils/validation'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import FieldGroup from '@/components/ui/FieldGroup.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'

const props = defineProps<{ open: boolean; existing?: Integration | null }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'saved', integration: Integration, created: boolean): void
}>()

const store = useIntegrationsStore()
const teams = useTeamsStore()
const ids = { name: 'integration-name', description: 'integration-description', ownerTeamId: 'integration-owner' } as const
const { errors, formError, clear, validate, applyServerError } = useFormErrors(ids)

const values = reactive({ name: '', description: '', ownerTeamId: '', isActive: true })
const busy = ref(false)
const isEdit = computed(() => !!props.existing)
const teamOptions = computed(() => teams.items.map((t) => ({ value: t.id, label: `${t.departmentName} · ${t.name}` })))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    const i = props.existing
    values.name = i?.name ?? ''
    values.description = i?.description ?? ''
    values.ownerTeamId = i?.ownerTeamId ?? ''
    values.isActive = i?.isActive ?? true
    void teams.load()
  },
  { immediate: true },
)

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    name: isBlank(values.name) && 'Give the integration a name, such as the system it is.',
    ownerTeamId: !values.ownerTeamId && 'Choose the team that runs the integration.',
  })
  if (!ok) return
  const body = {
    name: values.name.trim(),
    description: isBlank(values.description) ? null : values.description.trim(),
    ownerTeamId: values.ownerTeamId,
    isActive: values.isActive,
  }
  busy.value = true
  try {
    const saved = props.existing ? await store.update(props.existing.id, body) : await store.create(body)
    emit('update:open', false)
    emit('saved', saved, !props.existing)
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
    :title="isEdit ? `Edit ${existing?.name ?? 'integration'}` : 'New integration'"
    description="Another system that uses gateway keys for the teams it serves, such as a RAG platform."
    @update:open="emit('update:open', $event)"
  >
    <AsyncState :loading="teams.loading" :error="teams.error" :empty="false" @retry="teams.load()">
      <form id="integration-form" class="flex flex-col gap-8" novalidate @submit.prevent="submit">
        <InlineError v-if="formError" :message="formError" />
        <FieldGroup legend="Integration">
          <TextField :id="ids.name" v-model="values.name" label="Name" autocomplete="off" :error="errors.name" hint="Shown to key managers when they assign a key to it." />
          <TextField :id="ids.description" v-model="values.description" label="Description (optional)" :error="errors.description" />
          <SelectField
            :id="ids.ownerTeamId"
            v-model="values.ownerTeamId"
            label="Owner team"
            placeholder="Select a team"
            :options="teamOptions"
            :error="errors.ownerTeamId"
            hint="The team that runs the integration. Its credential keys belong to this team and its own calls are charged to it."
          />
          <CheckField
            v-model="values.isActive"
            label="Integration is active"
            description="Turn off to refuse all its keys, credential and assigned, without revoking them."
          />
        </FieldGroup>
      </form>
    </AsyncState>
    <template #footer>
      <UiButton variant="primary" type="submit" form="integration-form" :loading="busy">{{ isEdit ? 'Save integration' : 'Create integration' }}</UiButton>
      <UiButton :disabled="busy" @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
