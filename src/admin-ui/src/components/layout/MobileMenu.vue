<script setup lang="ts">
import { watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { isNavItemActive, type NavGroup, type NavItem } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'
import { useUiStore, type Theme } from '@/stores/ui'
import AppIcon from '@/components/ui/AppIcon.vue'
import ModalDialog from '@/components/ui/ModalDialog.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import { initials } from '@/utils/format'

const props = defineProps<{
  open: boolean
  groups: NavGroup[]
  badgeCount: (item: NavItem) => number
  updateAvailable: boolean
  themes: { value: Theme; label: string }[]
  sessionLeft: string | null
  signingOut: boolean
}>()
const emit = defineEmits<{ (e: 'update:open', v: boolean): void; (e: 'search'): void; (e: 'sign-out'): void }>()

const auth = useAuthStore()
const ui = useUiStore()
const route = useRoute()

// Close once a link has been followed, also when it points at the page already open.
watch(() => route.fullPath, () => props.open && emit('update:open', false))

function search(): void {
  emit('update:open', false)
  emit('search')
}
</script>

<template>
  <ModalDialog
    v-slot="{ titleId, descriptionId }"
    :open="open"
    class="material-overlay inset-x-0 bottom-0 flex h-[92dvh] w-full flex-col rounded-t-sheet text-fg"
    @update:open="emit('update:open', $event)"
  >
    <header class="flex items-center justify-between gap-3 px-5 pb-2 pt-4">
      <h2 :id="titleId" class="text-title-3">Menu</h2>
      <p :id="descriptionId" class="sr-only">All pages, theme and your account.</p>
      <button type="button" aria-label="Close menu" class="grid size-10 place-items-center rounded-control text-fg-2 hover:bg-hover" @click="emit('update:open', false)">
        <AppIcon name="close" />
      </button>
    </header>
    <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain px-3 pb-[max(16px,env(safe-area-inset-bottom))]">
      <button
        type="button"
        class="mx-2 mb-4 flex h-ctl-lg w-[calc(100%-16px)] items-center gap-2 rounded-control bg-surface px-3 text-body-lg text-fg-3 shadow-inset-input"
        @click="search"
      >
        <AppIcon name="search" /> Search pages and settings
      </button>

      <nav aria-label="Menu" class="flex flex-col gap-4">
        <section v-for="g in groups" :key="g.id" :aria-labelledby="`menu-${g.id}`">
          <h2 :id="`menu-${g.id}`" class="px-3 pb-1 text-caption font-medium uppercase tracking-wide text-fg-3">{{ g.label }}</h2>
          <ul>
            <li v-for="item in g.items" :key="item.name">
              <RouterLink
                :to="{ name: item.name }"
                :aria-current="isNavItemActive(item, route.name) ? 'page' : undefined"
                class="flex min-h-[50px] items-center gap-3 rounded-control px-3 py-1.5 text-body-lg text-fg hover:bg-hover aria-[current=page]:bg-surface aria-[current=page]:font-semibold aria-[current=page]:shadow-1"
                @click="emit('update:open', false)"
              >
                <AppIcon :name="item.icon" :filled="isNavItemActive(item, route.name)" class="text-fg-2" />
                <span class="flex min-w-0 flex-1 flex-col">
                  <span class="truncate">{{ item.label }}</span>
                  <span v-if="item.description" class="truncate text-caption font-normal text-fg-3">{{ item.description }}</span>
                </span>
                <span
                  v-if="badgeCount(item) > 0"
                  class="tabular flex h-badge min-w-[22px] items-center justify-center rounded-chip bg-danger px-1.5 text-caption font-semibold text-danger-fg"
                >{{ badgeCount(item) }}<span class="sr-only"> need attention</span></span>
                <span v-if="item.badge === 'update' && updateAvailable" class="flex h-badge items-center rounded-chip bg-accent-soft px-1.5 text-caption font-semibold text-accent-ink">
                  New<span class="sr-only"> version available</span>
                </span>
              </RouterLink>
            </li>
          </ul>
        </section>
      </nav>

      <div class="mt-6 flex flex-col gap-3 border-t border-border px-2 pt-4">
        <SegmentedControl :model-value="ui.theme" :options="themes" label="Theme" mode="radio" full @update:model-value="ui.setTheme($event)" />
        <div class="flex items-center gap-2 text-caption">
          <span
            class="inline-flex h-[26px] items-center gap-1.5 whitespace-nowrap rounded-[13px] px-2.5 font-medium"
            :class="auth.isDevelopment ? 'bg-warn-soft text-warn' : 'bg-ok-soft text-ok'"
          ><span class="size-1.5 rounded-full" :class="auth.isDevelopment ? 'bg-warn' : 'bg-ok'" aria-hidden="true"></span>{{ auth.isDevelopment ? 'Development' : 'Production' }}</span>
          <span v-if="sessionLeft" class="tabular text-fg-3">Session: {{ sessionLeft }}</span>
        </div>
        <div class="flex items-center gap-3 rounded-[12px] bg-surface p-2.5 shadow-1">
          <span class="flex size-9 shrink-0 items-center justify-center rounded-full bg-accent-soft text-caption font-semibold text-accent-ink" aria-hidden="true">
            {{ initials(auth.user?.name ?? auth.user?.email ?? '?') }}
          </span>
          <span class="min-w-0 flex-1 leading-5">
            <span class="block truncate text-body font-medium text-fg" lang="sv">{{ auth.user?.name ?? auth.user?.email }}</span>
            <span class="block truncate text-caption text-fg-3">{{ auth.roles[0] }}</span>
          </span>
          <button
            type="button"
            class="flex h-ctl-lg shrink-0 items-center gap-1.5 rounded-control px-3 text-small font-medium text-fg-2 hover:bg-hover hover:text-fg"
            :aria-disabled="signingOut"
            @click="emit('sign-out')"
          >
            <AppIcon name="logout" /> Sign out
          </button>
        </div>
      </div>
    </div>
  </ModalDialog>
</template>
