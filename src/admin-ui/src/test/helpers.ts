import type { VueWrapper } from '@vue/test-utils'

/** The first button whose visible text contains `text` (icon buttons also carry the icon’s name as text). */
export function button(wrapper: VueWrapper, text: string) {
  const found = wrapper.findAll('button').find((b) => b.text().includes(text))
  if (!found) throw new Error(`No button containing "${text}". Buttons: ${wrapper.findAll('button').map((b) => b.text()).join(' | ')}`)
  return found
}

export function buttonByLabel(wrapper: VueWrapper, label: string) {
  const found = wrapper.find(`button[aria-label="${label}"]`)
  if (!found.exists()) throw new Error(`No button labelled "${label}"`)
  return found
}
