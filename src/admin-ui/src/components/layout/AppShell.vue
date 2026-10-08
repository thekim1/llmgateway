<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { navGroups, mobileTabs, type NavItem } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { useBudgetsStore } from '@/stores/budgets'
import { useOpsStore } from '@/stores/ops'
import AppIcon from '@/components/ui/AppIcon.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import ToastRegion from '@/components/ui/ToastRegion.vue'
import CommandPalette from './CommandPalette.vue'
import SessionExpiryDialog from './SessionExpiryDialog.vue'
import { initials } from '@/utils/format'

const auth = useAuthStore()
const ui = useUiStore()
const budgets = useBudgetsStore()
const ops = useOpsStore()
const route = useRoute()
const paletteOpen = ref(false)

const visible = (item: NavItem) => auth.hasAnyRole(item.roles)
const groups = computed(() =>
  navGroups.map((g) => ({ ...g, items: g.items.filter(visible) })).filter((g) => g.items.length > 0),
)
const tabs = computed(() => mobileTabs.filter(visible))

const openAlerts = computed(() => budgets.alerts.filter((a) => !a.acknowledged).length)
const badCircuits = computed(() => (ops.health?.providers ?? []).filter((p) => p.circuitState !== 'Closed').length)
function badgeCount(item: NavItem): number {
  if (item.badge === 'alerts') return openAlerts.value
  if (item.badge === 'circuits') return badCircuits.value
  return 0
}

const themes = [
  { value: 'light' as const, label: 'Light' },
  { value: 'dark' as const, label: 'Dark' },
  { value: 'lumen' as const, label: 'Lumen' },
]

const now = ref(Date.now())
const clock = setInterval(() => (now.value = Date.now()), 30_000)
onBeforeUnmount(() => clearInterval(clock))
const sessionLeft = computed(() => {
  const at = auth.sessionExpiresAt?.getTime()
  if (!at) return null
  const mins = Math.max(0, Math.floor((at - now.value) / 60_000))
  return mins >= 60 ? ` h ${mins % 60} min left` : ` min left`
})

const isActive = (name: string) => route.name === name

function onKey(event: KeyboardEvent): void {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault()
    paletteOpen.value = true
  }
}
onMounted(() => {
  window.addEventListener('keydown', onKey)
  if (auth.hasAnyRole(['department-admin'])) void budgets.loadAlerts()
  if (auth.isGatewayAdmin) void ops.loadHealth()
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

const signingOut = ref(false)
async function signOut(): Promise<void> {
  signingOut.value = true
  try {
    window.location.assign(await auth.logout())
  } finally {
    signingOut.value = false
  }
}
</script>

<template>
  <a
    href="#main"
    class="material-overlay fixed left-4 top-2 z-toast -translate-y-24 rounded-control px-4 py-2 text-small font-medium text-fg focus:translate-y-0 md:hidden"
  >Skip to main content</a>

  <div class="flex min-h-screen">
    <aside
      class="sticky top-0 hidden h-screen bg-sidebar [backdrop-filter:var(--blur)] shrink-0 flex-col gap-5 overflow-y-auto border-r border-border p-4 md:flex md:w-[72px] lg:w-sidebar lg:p-5"
    >
      <div class="flex items-center gap-3 px-1">
        <span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-control bg-fg text-on-fg" aria-hidden="true">
          <AppIcon name="hub" />
        </span>
        <span class="hidden min-w-0 lg:block">
          <span class="block truncate text-body font-semibold text-fg">AI Gateway</span>
          <span class="block truncate text-caption text-fg-3">Umeå kommun</span>
        </span>
      </div>

      <nav aria-label="Main" class="flex flex-1 flex-col gap-5">
        <section v-for="g in groups" :key="g.id" :aria-labelledby="`nav-${g.id}`" class="flex flex-col gap-1">
          <h2 :id="`nav-${g.id}`" class="hidden px-2 pb-1 text-caption font-medium uppercase tracking-wide text-fg-3 lg:block">{{ g.label }}</h2>
          <RouterLink
            v-for="item in g.items"
            :key="item.name"
            :to="{ name: item.name }"
            :aria-current="isActive(item.name) ? 'page' : undefined"
            :title="item.label"
            class="relative flex h-ctl items-center gap-3 rounded-control px-2.5 text-small text-fg-2 hover:bg-hover hover:text-fg aria-[current=page]:bg-surface aria-[current=page]:font-semibold aria-[current=page]:text-fg aria-[current=page]:shadow-1 max-lg:justify-center"
          >
            <AppIcon :name="item.icon" :filled="isActive(item.name)" />
            <span class="hidden flex-1 truncate lg:inline">{{ item.label }}</span>
            <span
              v-if="badgeCount(item) > 0"
              class="tabular hidden h-badge min-w-[22px] items-center justify-center rounded-chip bg-danger px-1.5 text-caption font-semibold text-white lg:flex"
            >
              {{ badgeCount(item) }}<span class="sr-only"> need attention</span>
            </span>
          </RouterLink>
        </section>
      </nav>

      <div class="flex flex-col gap-3">
        <div class="hidden lg:block">
          <SegmentedControl
            :model-value="ui.theme"
            :options="themes"
            label="Theme"
            mode="radio"
            full
            @update:model-value="ui.setTheme($event)"
          />
        </div>
        <div class="flex items-center gap-3 px-1">
          <span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-accent-soft text-caption font-semibold text-accent-ink" aria-hidden="true">
            {{ initials(auth.user?.name ?? auth.user?.email ?? '?') }}
          </span>
          <span class="hidden min-w-0 flex-1 lg:block">
            <span class="block truncate text-small font-medium text-fg" lang="sv">{{ auth.user?.name ?? auth.user?.email }}</span>
            <span class="block truncate text-caption text-fg-3">{{ auth.roles[0] }}</span>
          </span>
          <button
            type="button"
            class="flex h-ctl-sm w-8 items-center justify-center rounded-control text-fg-2 hover:bg-hover hover:text-fg"
            :aria-disabled="signingOut"
            @click="signOut"
          >
            <AppIcon name="logout" />
            <span class="sr-only">Sign out</span>
          </button>
        </div>
      </div>
    </aside>

    <div class="flex min-w-0 flex-1 flex-col">
      <header class="bg-canvas-fixed sticky top-0 z-topbar flex h-topbar items-center gap-3 border-b border-border px-4 md:px-10">
        <div class="flex items-center gap-2 md:hidden">
          <AppIcon name="hub" />
          <span class="text-body font-semibold">AI Gateway</span>
        </div>
        <button
          type="button"
          class="ml-auto flex h-ctl items-center gap-2 rounded-control bg-surface px-3 text-small text-fg-3 shadow-1 hover:text-fg md:ml-0 md:w-80"
          @click="paletteOpen = true"
        >
          <AppIcon name="search" />
          <span class="hidden flex-1 text-left md:inline">Search pages and keys</span>
          <kbd class="hidden rounded-chip px-1.5 text-caption md:inline">Ctrl K</kbd>
          <span class="sr-only md:hidden">Search</span>
        </button>
        <div class="ml-auto hidden items-center gap-3 md:flex">
          <span class="inline-flex h-[26px] items-center gap-1.5 whitespace-nowrap rounded-[13px] bg-ok-soft px-2.5 text-caption font-medium text-ok"><span class="size-1.5 rounded-full bg-ok" aria-hidden="true"></span>Production</span>
          <span v-if="sessionLeft" class="tabular whitespace-nowrap text-small text-fg-3">{{ sessionLeft }}</span>
        </div>
      </header>

      <main id="main" tabindex="-1" class="mx-auto w-full max-w-content px-4 pb-24 pt-6 outline-none md:px-10 md:pb-16 md:pt-9">
        <slot />
      </main>
    </div>
  </div>

  <nav
    v-if="tabs.length"
    aria-label="Quick access"
    class="material fixed inset-x-0 bottom-0 z-topbar grid border-t border-border md:hidden"
    :style="{ gridTemplateColumns: `repeat(${tabs.length}, 1fr)` }"
  >
    <RouterLink
      v-for="item in tabs"
      :key="item.name"
      :to="{ name: item.name }"
      :aria-current="isActive(item.name) ? 'page' : undefined"
      class="relative flex h-16 flex-col items-center justify-center gap-0.5 text-caption text-fg-2 aria-[current=page]:text-fg"
    >
      <AppIcon :name="item.icon" :filled="isActive(item.name)" />
      {{ item.label }}
      <span v-if="badgeCount(item) > 0" class="absolute right-1/4 top-2 h-2 w-2 rounded-full bg-danger" aria-hidden="true" />
    </RouterLink>
  </nav>

  <CommandPalette v-model:open="paletteOpen" />
  <SessionExpiryDialog />
  <ToastRegion />
</template>
