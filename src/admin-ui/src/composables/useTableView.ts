import { computed, ref, watch, type Ref } from 'vue'

export type SortDir = 'asc' | 'desc'
export type SortValue = string | number | null | undefined

const collator = new Intl.Collator('sv', { numeric: true, sensitivity: 'base' })

/** Client-side sorting and paging for a list that is already filtered. Rows with no value always sort last. */
export function useSortedPage<T>(
  rows: Ref<readonly T[]>,
  sorters: Record<string, (row: T) => SortValue>,
  initial: { key: string; dir?: SortDir },
  pageSize = 25,
) {
  const sortKey = ref(initial.key)
  const sortDir = ref<SortDir>(initial.dir ?? 'asc')
  const page = ref(1)

  function toggleSort(key: string): void {
    if (sortKey.value === key) sortDir.value = sortDir.value === 'asc' ? 'desc' : 'asc'
    else {
      sortKey.value = key
      sortDir.value = 'asc'
    }
  }

  const sorted = computed(() => {
    const get = sorters[sortKey.value]
    if (!get) return [...rows.value]
    const sign = sortDir.value === 'asc' ? 1 : -1
    return [...rows.value].sort((a, b) => {
      const x = get(a)
      const y = get(b)
      if (x == null && y == null) return 0
      if (x == null) return 1
      if (y == null) return -1
      const result = typeof x === 'number' && typeof y === 'number' ? x - y : collator.compare(String(x), String(y))
      return result * sign
    })
  })

  const paged = computed(() => sorted.value.slice((page.value - 1) * pageSize, page.value * pageSize))

  // Re-sorting starts from page 1; a shrinking result set (filters, deletes) must not leave the page out of range.
  watch([sortKey, sortDir], () => (page.value = 1))
  watch(
    () => sorted.value.length,
    (length) => {
      page.value = Math.min(page.value, Math.max(1, Math.ceil(length / pageSize)))
    },
  )

  return { sortKey, sortDir, page, pageSize, toggleSort, sorted, paged }
}
