import { nextTick, ref } from 'vue'
import { describe, expect, it } from 'vitest'
import { useSortedPage } from './useTableView'

interface Row {
  name: string
  price: number | null
}
const rows = ref<Row[]>([
  { name: 'b-model', price: 2 },
  { name: 'a-model', price: null },
  { name: 'c-model', price: 1 },
])
const sorters = { name: (r: Row) => r.name, price: (r: Row) => r.price }

describe('useSortedPage', () => {
  it('sorts ascending, then descending when the same column is toggled', () => {
    const view = useSortedPage(rows, sorters, { key: 'name' })
    expect(view.sorted.value.map((r) => r.name)).toEqual(['a-model', 'b-model', 'c-model'])
    view.toggleSort('name')
    expect(view.sorted.value.map((r) => r.name)).toEqual(['c-model', 'b-model', 'a-model'])
  })

  it('keeps rows without a value last in both directions', () => {
    const view = useSortedPage(rows, sorters, { key: 'price' })
    expect(view.sorted.value.map((r) => r.price)).toEqual([1, 2, null])
    view.toggleSort('price')
    expect(view.sorted.value.map((r) => r.price)).toEqual([2, 1, null])
  })

  it('pages the result and clamps the page when the list shrinks', async () => {
    const many = ref<Row[]>(Array.from({ length: 5 }, (_, i) => ({ name: `m${i}`, price: i })))
    const view = useSortedPage(many, sorters, { key: 'name' }, 2)
    view.page.value = 3
    expect(view.paged.value.map((r) => r.name)).toEqual(['m4'])
    many.value = many.value.slice(0, 2)
    await nextTick()
    expect(view.page.value).toBe(1)
  })
})
