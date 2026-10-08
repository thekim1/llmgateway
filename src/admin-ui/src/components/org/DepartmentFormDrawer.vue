<script setup lang="ts">
import { ref, watch } from 'vue'
import type { Department } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useDepartmentsStore } from '@/stores/departments'
import { useUiStore } from '@/stores/ui'
import CheckField from '../ui/CheckField.vue'
import InlineError from '../ui/InlineError.vue'
import TextField from '../ui/TextField.vue'
import UiButton from '../ui/UiButton.vue'
import UiDrawer from '../ui/UiDrawer.vue'

const props = defineProps<{ open: boolean; department: Department | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', department: Department): void }>()

const departments = useDepartmentsStore()
const ui = useUiStore()
const { errors, formError, formErrorLang, clear, validate, applyServerError } = useFormErrors({
  name: 'dept-name',
  costCenterCode: 'dept-code',
})

const name = ref('')
const costCenterCode = ref('')
const isActive = ref(true)
const busy = ref(false)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clear()
    name.value = props.department?.name ?? ''
    costCenterCode.value = props.department?.costCenterCode ?? ''
    isActive.value = props.department?.isActive ?? true
  },
  { immediate: true },
)

async function submit(): Promise<void> {
  if (busy.value) return
  const ok = validate({
    name: !name.value.trim() && 'Give the department a name.',
    costCenterCode: !costCenterCode.value.trim() && 'Enter the cost center code (ansvarskod).',
  })
  if (!ok) return
  busy.value = true
  try {
    const body = { name: name.value.trim(), costCenterCode: costCenterCode.value.trim() }
    const saved = props.department
      ? await departments.update(props.department.id, { ...body, isActive: isActive.value })
      : await departments.create(body)
    ui.notify(props.department ? 'Department saved' : 'Department created')
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
  <UiDrawer :open="open" :title="department ? 'Edit department' : 'New department'" @update:open="emit('update:open', $event)">
    <form id="department-form" class="flex flex-col gap-5" novalidate @submit.prevent="submit">
      <InlineError v-if="formError" :message="formError" :lang="formErrorLang" />
      <TextField id="dept-name" v-model="name" label="Name" :error="errors.name" hint="Use the official name of the department." />
      <TextField id="dept-code" v-model="costCenterCode" label="Cost center" mono :error="errors.costCenterCode" hint="The ansvarskod that costs are charged to." />
      <CheckField v-if="department" v-model="isActive" label="Active" description="Turn off to retire this department." />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="department-form" :loading="busy">{{ department ? 'Save changes' : 'Create department' }}</UiButton>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
    </template>
  </UiDrawer>
</template>
