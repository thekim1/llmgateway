<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Budget, Department, Team } from '@/api/types'
import DepartmentDetail from '@/components/org/DepartmentDetail.vue'
import DepartmentFormDrawer from '@/components/org/DepartmentFormDrawer.vue'
import DepartmentList from '@/components/org/DepartmentList.vue'
import TeamFormDrawer from '@/components/org/TeamFormDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useAuthStore } from '@/stores/auth'
import { useDepartmentsStore } from '@/stores/departments'
import { useTeamsStore } from '@/stores/teams'
import { useUiStore } from '@/stores/ui'

const auth = useAuthStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const ui = useUiStore()
const router = useRouter()

const selectedId = ref<string | null>(null)
const budgets = ref<Budget[]>([])
const detail = ref<InstanceType<typeof DepartmentDetail> | null>(null)

const departmentDrawer = ref(false)
const editingDepartment = ref<Department | null>(null)
const teamDrawer = ref(false)
const editingTeam = ref<Team | null>(null)
const deletingDepartment = ref(false)
const deletingTeam = ref<Team | null>(null)

const selected = computed(() => departments.items.find((d) => d.id === selectedId.value) ?? null)
const selectedTeams = computed(() => teams.items.filter((t) => t.departmentId === selectedId.value))
const selectedBudget = computed(() => {
  const forDepartment = budgets.value.filter((b) => b.scope === 'Department' && b.scopeId === selectedId.value && b.isActive)
  return forDepartment.find((b) => b.period === 'Monthly') ?? forDepartment[0] ?? null
})
const manageableDepartments = computed(() => departments.items.filter((d) => auth.canManageDepartment(d.costCenterCode)))
const canManageTeams = computed(() => !!selected.value && auth.canManageDepartment(selected.value.costCenterCode))

watch(
  () => departments.items,
  (items) => {
    if (!items.some((d) => d.id === selectedId.value)) selectedId.value = items[0]?.id ?? null
  },
)

async function load(): Promise<void> {
  await Promise.all([departments.load(), teams.load(), loadBudgets()])
}

async function loadBudgets(): Promise<void> {
  try {
    budgets.value = await api.budgets.list({ scope: 'Department' })
  } catch {
    budgets.value = []
  }
}

function select(id: string): void {
  selectedId.value = id
  void nextTick(() => detail.value?.focus())
}

function newDepartment(): void {
  editingDepartment.value = null
  departmentDrawer.value = true
}
function editDepartment(): void {
  editingDepartment.value = selected.value
  departmentDrawer.value = true
}
function addTeam(): void {
  editingTeam.value = null
  teamDrawer.value = true
}
function editTeam(team: Team): void {
  editingTeam.value = team
  teamDrawer.value = true
}
function viewKeys(team: Team): void {
  void router.push({ name: 'keys', query: { team: team.id } })
}

async function teamSaved(): Promise<void> {
  await departments.load()
}

async function deleteDepartment(): Promise<void> {
  if (!selected.value) return
  await departments.remove(selected.value.id)
  ui.notify('Department deleted')
}

async function deleteTeam(): Promise<void> {
  if (!deletingTeam.value) return
  await teams.remove(deletingTeam.value.id)
  ui.notify('Team deleted')
  void departments.load()
}

onMounted(async () => {
  await load()
  if (!selectedId.value) selectedId.value = departments.items[0]?.id ?? null
})
</script>

<template>
  <PageHeader title="Organisation">
    <template #description>Departments (<span lang="sv">förvaltningar</span>) own teams. Teams own keys.</template>
    <UiButton v-if="auth.isGatewayAdmin" variant="primary" icon="add" @click="newDepartment">New department</UiButton>
  </PageHeader>

  <AsyncState
    :loading="departments.loading"
    :error="departments.error"
    :empty="departments.items.length === 0"
    empty-text="No departments yet."
    @retry="load"
  >
    <div class="grid grid-cols-[minmax(240px,300px)_1fr] items-start gap-6 max-lg:grid-cols-1">
      <DepartmentList :departments="departments.items" :selected-id="selectedId" @select="select" />
      <DepartmentDetail
        v-if="selected"
        ref="detail"
        :department="selected"
        :teams="selectedTeams"
        :budget="selectedBudget"
        :can-edit-department="auth.isGatewayAdmin"
        :can-manage-teams="canManageTeams"
        @edit="editDepartment"
        @delete="deletingDepartment = true"
        @add-team="addTeam"
        @edit-team="editTeam"
        @delete-team="deletingTeam = $event"
        @view-keys="viewKeys"
      />
    </div>
  </AsyncState>

  <DepartmentFormDrawer v-model:open="departmentDrawer" :department="editingDepartment" @saved="select($event.id)" />
  <TeamFormDrawer
    v-model:open="teamDrawer"
    :team="editingTeam"
    :departments="manageableDepartments"
    :default-department-id="selectedId"
    @saved="teamSaved"
  />

  <ConfirmDialog v-model:open="deletingDepartment" title="Delete department?" confirm-label="Delete department" danger :action="deleteDepartment">
    <span lang="sv" class="font-medium">{{ selected?.name }}</span> is removed for good.
  </ConfirmDialog>
  <ConfirmDialog :open="!!deletingTeam" title="Delete team?" confirm-label="Delete team" danger :action="deleteTeam" @update:open="!$event && (deletingTeam = null)">
    <span lang="sv" class="font-medium">{{ deletingTeam?.name }}</span> is removed for good.
  </ConfirmDialog>
</template>
