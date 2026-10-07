<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppDialog from '@/components/AppDialog.vue'
import AppIcon from '@/components/AppIcon.vue'
import { useAuthStore } from '@/stores/auth'
import { useNotify } from '@/composables/useForm'

/** Warns before the session expires (WCAG 2.2.6) and lets the user extend it. */
const WARNING_MS = 5 * 60 * 1000
const MAX_TIMEOUT = 2_147_000_000

const { t } = useI18n()
const auth = useAuthStore()
const notify = useNotify()
const open = ref(false)
const busy = ref(false)
const now = ref(Date.now())
let warnTimer: number | undefined
let expireTimer: number | undefined
let tick: number | undefined

const minutesLeft = computed(() => {
  const expires = auth.sessionExpiresAt?.getTime()
  if (!expires) return 0
  return Math.max(0, Math.ceil((expires - now.value) / 60000))
})

function clearTimers(): void {
  window.clearTimeout(warnTimer)
  window.clearTimeout(expireTimer)
  window.clearInterval(tick)
}

function schedule(): void {
  clearTimers()
  const expires = auth.sessionExpiresAt?.getTime()
  if (!auth.isAuthenticated || !expires) {
    open.value = false
    return
  }
  const untilWarn = expires - WARNING_MS - Date.now()
  const untilExpire = expires - Date.now()
  if (untilWarn <= 0) show()
  else warnTimer = window.setTimeout(show, Math.min(untilWarn, MAX_TIMEOUT))
  if (untilExpire > 0) {
    expireTimer = window.setTimeout(() => {
      open.value = false
      void auth.load()
    }, Math.min(untilExpire, MAX_TIMEOUT))
  }
}

function show(): void {
  now.value = Date.now()
  open.value = true
  window.clearInterval(tick)
  tick = window.setInterval(() => (now.value = Date.now()), 30_000)
}

async function extend(): Promise<void> {
  busy.value = true
  try {
    await auth.extendSession()
    open.value = false
  } catch (error) {
    notify.error(error)
  } finally {
    busy.value = false
  }
}

async function signOut(): Promise<void> {
  try { window.location.assign(await auth.logout()) } catch (error) { notify.error(error) }
}

watch(() => [auth.sessionExpiresAt?.getTime(), auth.isAuthenticated], schedule, { immediate: true })
onBeforeUnmount(clearTimers)
</script>

<template>
  <AppDialog v-model:open="open" :title="t('session.title')" :description="t('session.body', { minutes: minutesLeft })">
    <p class="help">
      <AppIcon name="info" />
      {{ t('session.help') }}
    </p>
    <template #footer>
      <button type="button" class="btn btn--secondary" @click="signOut">
        <AppIcon name="logout" />
        {{ t('auth.signOut') }}
      </button>
      <button type="button" class="btn btn--primary" :aria-busy="busy || undefined" @click="extend">
        <AppIcon name="update" />
        {{ t('session.extend') }}
      </button>
    </template>
  </AppDialog>
</template>
