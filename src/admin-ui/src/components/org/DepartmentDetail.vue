<script setup lang="ts">
import { computed, ref } from 'vue'
import type { Budget, Department, Team } from '@/api/types'
import { formatSek } from '@/utils/format'
import { PERIOD_LABEL, plural } from '@/utils/labels'
import DataTable, { type Column } from '../ui/DataTable.vue'
import UiBadge from '../ui/UiBadge.vue'
import UiButton from '../ui/UiButton.vue'
import UiMeter from '../ui/UiMeter.vue'

const props = defineProps<{
  department: Department
  teams: Team[]
  budget: Budget | null
  /** Show the Edit / Delete department buttons. */
  canEditDepartment: boolean
  /** Show Add team / Edit team / Delete team. */
  canManageTeams: boolean
}>()
const emit = defineEmits<{
  (e: 'edit'): void
  (e: 'delete'): void
  (e: 'add-team'): void
  (e: 'edit-team', team: Team): void
  (e: 'delete-team', team: Team): void
  (e: 'view-keys', team: Team): void
}>()

const heading = ref<HTMLElement | null>(null)
defineExpose({ focus: () => heading.value?.focus() })

const columns = computed<Column[]>(() => [
  { key: 'name', label: 'Team' },
  { key: 'keys', label: 'Keys', align: 'right' },
  { key: 'status', label: 'Status' },
  { key: 'actions', label: 'Actions', hideLabel: true },
])

const deleteBlocked = computed(() => props.department.teamCount > 0)
const summary = computed(() => `${plural(props.department.teamCount, 'team')} · ${plural(props.teams.reduce((n, t) => n + t.keyCount, 0), 'key')}`)
</script>

<template>
  <section aria-labelledby="dept-title" class="flex min-w-0 flex-col gap-4">
    <div class="material flex flex-col gap-5 rounded-card p-6">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div class="flex min-w-0 flex-col gap-1">
          <span class="text-small text-fg-3">Department · cost center <span class="font-mono">{{ department.costCenterCode }}</span></span>
          <h2 id="dept-title" ref="heading" tabindex="-1" lang="sv" class="text-title-2 outline-none">{{ department.name }}</h2>
          <span class="flex items-center gap-2 text-small text-fg-2">
            {{ summary }}
            <UiBadge v-if="!department.isActive" tone="neutral" label="Inactive" />
          </span>
        </div>
        <div v-if="canEditDepartment" class="flex flex-wrap items-center gap-2">
          <UiButton icon="edit" @click="emit('edit')">Edit</UiButton>
          <UiButton
            variant="danger"
            icon="delete"
            :disabled="deleteBlocked"
            :aria-describedby="deleteBlocked ? 'dept-delete-reason' : undefined"
            @click="emit('delete')"
          >Delete department</UiButton>
        </div>
      </div>
      <p v-if="canEditDepartment && deleteBlocked" id="dept-delete-reason" class="-mt-3 text-small text-fg-3">
        Can't delete: it still has {{ plural(department.teamCount, 'team') }}. Delete them first.
      </p>
      <div class="flex flex-col gap-2">
        <template v-if="budget">
          <div class="flex flex-wrap items-baseline justify-between gap-2">
            <span class="text-small font-medium text-fg-2">{{ PERIOD_LABEL[budget.period] }} budget</span>
            <span class="tabular text-small text-fg-2">{{ formatSek(budget.spentSek) }} of {{ formatSek(budget.limitSek) }}</span>
          </div>
          <UiMeter :value="budget.percentUsed" label="Department budget used" :ticks="budget.alertThresholds" show-value />
        </template>
        <p v-else class="text-small text-fg-3">No budget set for this department. Add one under Budgets &amp; alerts.</p>
      </div>
    </div>

    <div class="flex flex-col gap-3">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h3 class="text-heading">Teams</h3>
        <UiButton v-if="canManageTeams" size="sm" icon="add" @click="emit('add-team')">Add team</UiButton>
      </div>
      <DataTable
        :columns="columns"
        :rows="teams"
        :row-key="(t) => t.id"
        caption="Teams"
        empty-text="No teams in this department yet."
      >
        <template #cell-name="{ row }">
          <button
            v-if="canManageTeams"
            type="button"
            class="-mx-1 flex flex-col rounded-chip px-1 text-left"
            @click="emit('edit-team', row)"
          >
            <span lang="sv" class="font-medium">{{ row.name }}</span>
            <span v-if="row.description" class="text-caption text-fg-3">{{ row.description }}</span>
          </button>
          <span v-else class="flex flex-col">
            <span lang="sv" class="font-medium">{{ row.name }}</span>
            <span v-if="row.description" class="text-caption text-fg-3">{{ row.description }}</span>
          </span>
        </template>
        <template #cell-keys="{ row }">{{ row.keyCount }}</template>
        <template #cell-status="{ row }"><UiBadge :tone="row.isActive ? 'ok' : 'neutral'" :label="row.isActive ? 'Active' : 'Inactive'" /></template>
        <template #cell-actions="{ row }">
          <div class="flex flex-col items-end gap-1">
            <div class="flex items-center justify-end gap-1">
              <UiButton size="sm" variant="quiet" @click="emit('view-keys', row)">Keys</UiButton>
              <UiButton
                v-if="canManageTeams"
                size="sm"
                variant="danger"
                :disabled="row.keyCount > 0"
                :aria-describedby="row.keyCount > 0 ? `team-reason-${row.id}` : undefined"
                @click="emit('delete-team', row)"
              >Delete</UiButton>
            </div>
            <span v-if="canManageTeams && row.keyCount > 0" :id="`team-reason-${row.id}`" class="text-caption text-fg-3">Has {{ plural(row.keyCount, 'key') }}</span>
          </div>
        </template>
      </DataTable>
    </div>
  </section>
</template>
