import { computed, type Ref } from 'vue'
import type { RoutingScope } from '@/api/types'
import { useDepartmentsStore } from '@/stores/departments'
import { useKeysStore } from '@/stores/keys'
import { useTeamsStore } from '@/stores/teams'

export interface OwnerOption {
  value: string
  label: string
}

/** The keys, teams or departments a rule of the given scope can be assigned to, and a loader for them. */
export function useRuleOwners(scope: Ref<RoutingScope>) {
  const departments = useDepartmentsStore()
  const teams = useTeamsStore()
  const keys = useKeysStore()

  const options = computed<OwnerOption[]>(() =>
    scope.value === 'Department'
      ? departments.items.map((d) => ({ value: d.id, label: d.name }))
      : scope.value === 'Team'
        ? teams.items.map((t) => ({ value: t.id, label: `${t.name} · ${t.departmentName}` }))
        : scope.value === 'VirtualKey'
          ? keys.items.map((k) => ({ value: k.id, label: `${k.name} · ${k.prefix}` }))
          : [],
  )

  async function load(next: RoutingScope = scope.value): Promise<void> {
    if (next === 'Department') await departments.load()
    else if (next === 'Team') await teams.load()
    else if (next === 'VirtualKey') await keys.load()
  }

  return { options, load }
}
