<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useAuthStore } from '@/stores/auth'
import UiDialog from '@/components/ui/UiDialog.vue'
import UiButton from '@/components/ui/UiButton.vue'
import InlineError from '@/components/ui/InlineError.vue'
import { problemMessage } from '@/utils/problem'

const WARN_MS = 5 * 60 * 1000
const auth = useAuthStore()
const now = ref(Date.now())
const busy = ref(false)
const error = ref<string | null>(null)
const dismissedFor = ref<number | null>(null)
const timer = setInterval(() => (now.value = Date.now()), 1000)
onBeforeUnmount(() => clearInterval(timer))

const expiresAt = computed(() => auth.sessionExpiresAt?.getTime() ?? null)
const remaining = computed(() => (expiresAt.value === null ? null : expiresAt.value - now.value))
const open = computed(
  () => auth.isAuthenticated && remaining.value !== null && remaining.value <= WARN_MS && remaining.value > 0 && dismissedFor.value !== expiresAt.value,
)
const label = computed(() => {
  const s = Math.max(0, Math.ceil((remaining.value ?? 0) / 1000))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
})

watch(expiresAt, () => (error.value = null))

async function stay(): Promise<void> {
  busy.value = true
  error.value = null
  try {
    await auth.extendSession()
  } catch (e) {
    error.value = problemMessage(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDialog
    :open="open"
    title="Your session is about to expire"
    description="Stay signed in to keep your unsaved work."
    icon="timer"
    tone="warn"
    @update:open="(v) => !v && (dismissedFor = expiresAt)"
  >
    <p class="tabular text-title-2 text-fg">{{ label }}</p>
    <InlineError v-if="error" :message="error" />
    <div class="flex justify-end gap-3">
      <UiButton variant="primary" :loading="busy" @click="stay">Stay signed in</UiButton>
    </div>
  </UiDialog>
</template>
