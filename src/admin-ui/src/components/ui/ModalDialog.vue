<script lang="ts">
import { shallowReactive } from 'vue'

/** Open dialogs, topmost last. Toasts render inside the topmost one, since a modal makes the rest of the page inert. */
export const openDialogs = shallowReactive<HTMLDialogElement[]>([])

let locks = 0
/** The page behind a dialog does not scroll; counted, since dialogs can open on top of each other. */
function lockScroll(on: boolean): void {
  locks += on ? 1 : -1
  document.documentElement.style.overflow = locks > 0 ? 'hidden' : ''
}
</script>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'

/**
 * A native modal <dialog>: the browser keeps focus inside it, makes the page behind it inert and puts it on top of
 * everything. This adds what the element lacks: open/close from a prop, Esc and backdrop clicks that can be turned
 * off, focus on the first control when it opens and back on the opener when it closes, and no page scrolling behind.
 * The content is only rendered while open, so forms start fresh each time.
 */
const props = withDefaults(defineProps<{ open: boolean; dismissible?: boolean }>(), { dismissible: true })
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const el = ref<HTMLDialogElement | null>(null)
const titleId = useId()
const descriptionId = useId()
let opener: HTMLElement | null = null

const TABBABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

function show(dialog: HTMLDialogElement): void {
  if (dialog.open) return
  opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
  if (!document.getElementById(descriptionId)) dialog.removeAttribute('aria-describedby')
  dialog.showModal?.()
  if (!openDialogs.includes(dialog)) {
    openDialogs.push(dialog)
    lockScroll(true)
  }
  const first = dialog.querySelector<HTMLElement>('[data-autofocus]') ?? dialog.querySelector<HTMLElement>(TABBABLE)
  ;(first ?? dialog).focus()
}

function hide(dialog: HTMLDialogElement | null): void {
  if (!dialog) return
  const index = openDialogs.indexOf(dialog)
  if (index >= 0) {
    openDialogs.splice(index, 1)
    lockScroll(false)
  }
  if (dialog.open) dialog.close()
  if (opener?.isConnected) opener.focus()
  opener = null
}

// Runs before the DOM update that removes the dialog, so it is closed (and focus restored) while still attached.
watch(() => props.open, (open) => {
  if (!open) hide(el.value)
}, { flush: 'pre' })
watch(el, (dialog) => {
  if (dialog && props.open) show(dialog)
})
onBeforeUnmount(() => hide(el.value))

function requestClose(): void {
  if (props.dismissible) emit('update:open', false)
}

function onCancel(event: Event): void {
  event.preventDefault()
  requestClose()
}

/**
 * The browser can close a dialog itself (a second Esc); a dialog that must stay open opens again. The event arrives
 * a moment after the close, so one from an earlier, already replaced dialog (closed, then quickly reopened) is ignored.
 */
function onClose(event: Event): void {
  if (!props.open || event.target !== el.value) return
  if (props.dismissible) emit('update:open', false)
  else void nextTick(() => el.value && !el.value.open && el.value.showModal?.())
}

let pressedOutside = false
function outside(event: MouseEvent): boolean {
  const box = el.value?.getBoundingClientRect()
  if (!box || event.target !== el.value) return false
  return event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom
}
function onPointerDown(event: PointerEvent): void {
  pressedOutside = outside(event)
}
function onClick(event: MouseEvent): void {
  if (pressedOutside && outside(event)) requestClose()
  pressedOutside = false
}
</script>

<template>
  <!-- The click handler only notices clicks on the backdrop; the keyboard equivalent is Esc (the cancel event). -->
  <!-- eslint-disable-next-line vuejs-accessibility/click-events-have-key-events, vuejs-accessibility/no-static-element-interactions -->
  <dialog
    v-if="open"
    ref="el"
    class="modal"
    :aria-labelledby="titleId"
    :aria-describedby="descriptionId"
    @cancel="onCancel"
    @close="onClose"
    @pointerdown="onPointerDown"
    @click="onClick"
  >
    <slot :title-id="titleId" :description-id="descriptionId" :close="requestClose" />
  </dialog>
</template>
