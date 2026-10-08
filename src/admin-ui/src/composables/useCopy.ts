import { onBeforeUnmount, ref } from 'vue'

/** Copies text and flips `copied` for two seconds (the UI announces it through a status region). */
export function useCopy() {
  const copied = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined

  async function copy(text: string): Promise<boolean> {
    try {
      await navigator.clipboard.writeText(text)
    } catch {
      return false
    }
    copied.value = true
    clearTimeout(timer)
    timer = setTimeout(() => (copied.value = false), 2000)
    return true
  }

  onBeforeUnmount(() => clearTimeout(timer))
  return { copied, copy }
}
