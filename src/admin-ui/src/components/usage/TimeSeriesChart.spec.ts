import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TimeSeriesChart, { type ChartSeries } from './TimeSeriesChart.vue'

const slots = [Date.UTC(2026, 9, 1), Date.UTC(2026, 9, 2), Date.UTC(2026, 9, 3)]
const series: ChartSeries[] = [
  { id: 'a', name: 'Success', color: 'var(--ok)', values: [10, 0, 30] },
  { id: 'b', name: 'Error', color: 'var(--danger)', values: [5, null, 0] },
]
const format = (v: number) => `${v} req`
const formatSlot = (ms: number) => new Date(ms).toISOString().slice(0, 10)

function render(props: Partial<InstanceType<typeof TimeSeriesChart>['$props']> = {}) {
  return mount(TimeSeriesChart, { props: { label: 'Requests', slots, series, format, formatSlot, ...props } })
}

describe('TimeSeriesChart', () => {
  it('draws one bar segment per non-zero value and describes the data for screen readers', () => {
    const wrapper = render({ stacked: true })
    // 10 and 5 on day one, 30 on day three; zeros and gaps draw nothing.
    expect(wrapper.findAll('svg path')).toHaveLength(3)
    const summary = wrapper.find('svg[role="img"]').attributes('aria-label')!
    expect(summary).toContain('Requests, 3 periods from 2026-10-01 to 2026-10-03.')
    expect(summary).toContain('Success: highest 30 req at 2026-10-03.')
  })

  it('scales the axis to clean steps above the stacked total', () => {
    const wrapper = render({ stacked: true, series: [{ ...series[0]!, values: [10, 0, 37] }, series[1]!] })
    expect(wrapper.findAll('[aria-hidden="true"] span.tabular').map((t) => t.text())).toEqual(['0 req', '10 req', '20 req', '30 req', '40 req'])
  })

  it('draws a line per series, broken where a value is missing', () => {
    const wrapper = render({ kind: 'line' })
    const paths = wrapper.findAll('svg path')
    expect(paths).toHaveLength(2)
    expect(paths[1]!.attributes('d')).toContain('l 0.01 0') // the lone first value becomes a dot
  })

  it('shows every value in the table view', () => {
    const wrapper = render({ asTable: true })
    expect(wrapper.find('svg').exists()).toBe(false)
    const rows = wrapper.findAll('tbody tr').map((r) => r.findAll('th, td').map((c) => c.text()))
    expect(rows).toEqual([
      ['2026-10-01', '10 req', '5 req'],
      ['2026-10-02', '0 req', '—'],
      ['2026-10-03', '30 req', '0 req'],
    ])
  })
  describe('time labels', () => {
    const days = Array.from({ length: 24 }, (_, i) => Date.UTC(2026, 9, 23, 16) + i * 3_600_000)
    const hourly = [{ ...series[0]!, values: days.map(() => 1) }]
    const label = (ms: number) => new Date(ms).toISOString().slice(5, 16).replace('T', ' ')
    const original = globalThis.ResizeObserver
    // The test setup's stub never reports a size; this one reports the plot as `px` wide.
    const plotWidth = (px: number) => {
      globalThis.ResizeObserver = class {
        constructor(private callback: ResizeObserverCallback) {}
        observe() { this.callback([{ contentRect: { width: px } } as ResizeObserverEntry], this as unknown as ResizeObserver) }
        unobserve() {}
        disconnect() {}
      }
    }
    const texts = async (px: number) => {
      plotWidth(px)
      const wrapper = render({ slots: days, series: hourly, formatSlot: label })
      await flushPromises()
      return wrapper.findAll('.ml-16 span').map((s) => s.text())
    }
    afterEach(() => { globalThis.ResizeObserver = original })

    it('shows the first, middle and last time when there is room', async () => {
      expect(await texts(600)).toEqual(['10-23 16:00', '10-24 03:00', '10-24 15:00'])
    })

    it('leaves out the middle time on a narrow chart, so the labels do not run together', async () => {
      expect(await texts(200)).toEqual(['10-23 16:00', '10-24 15:00'])
    })
  })
})
