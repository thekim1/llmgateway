<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { focusSection } from '@/router'
import { DialogContent, DialogDescription, DialogOverlay, DialogPortal, DialogRoot, DialogTitle } from 'reka-ui'
import { filterEntries, navGroups, searchEntries, type SearchEntry } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'
import AppIcon from '@/components/ui/AppIcon.vue'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', v: boolean): void }>()
const router = useRouter()
const auth = useAuthStore()
const query = ref('')
const active = ref(0)
const input = ref<HTMLInputElement | null>(null)

const entries = computed(() =>
  searchEntries(navGroups.map((g) => ({ ...g, items: g.items.filter((i) => auth.hasAnyRole(i.roles)) }))),
)
const results = computed(() => filterEntries(entries.value, query.value))
/** Without a query the list is the menu, so show its section headings. */
const grouped = computed(() => !query.value.trim())

watch(() => props.open, (o) => {
  if (o) {
    query.value = ''
    active.value = 0
  }
})
watch(query, () => (active.value = 0))

const optionId = (i: number) => `palette-opt-${i}`

function move(step: number): void {
  const n = results.value.length
  if (!n) return
  active.value = (active.value + step + n) % n
  void nextTick(() => document.getElementById(optionId(active.value))?.scrollIntoView({ block: 'nearest' }))
}

async function go(entry: SearchEntry | undefined): Promise<void> {
  if (!entry) return
  emit('update:open', false)
  const failure = await router.push(entry.to)
  // Already there: jump to the section again instead of doing nothing.
  const hash = typeof entry.to === 'object' && 'hash' in entry.to ? entry.to.hash : undefined
  if (failure && hash) focusSection(hash)
}

function showsHeading(i: number): boolean {
  return grouped.value && (i === 0 || results.value[i - 1]?.group !== results.value[i]?.group)
}
</script>

<template>
  <DialogRoot :open="open" @update:open="emit('update:open', $event)">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-dialog bg-scrim" />
      <DialogContent
        class="material-overlay fixed z-dialog flex flex-col overflow-hidden text-fg max-md:inset-0 max-md:h-dvh md:left-1/2 md:top-[12vh] md:max-h-[76vh] md:w-[560px] md:-translate-x-1/2 md:rounded-dialog"
        @open-auto-focus.prevent="input?.focus()"
      >
        <DialogTitle class="sr-only">Search pages and settings</DialogTitle>
        <DialogDescription class="sr-only">Type to filter, use the arrow keys to choose and Enter to open.</DialogDescription>
        <div class="flex items-center gap-2 border-b border-border px-4 pb-3 pt-[max(12px,env(safe-area-inset-top))]">
          <AppIcon name="search" class="text-fg-3" />
          <label for="palette-q" class="sr-only">Search pages and settings</label>
          <input
            id="palette-q"
            ref="input"
            v-model="query"
            class="h-ctl-lg min-w-0 flex-1 bg-transparent [&::-webkit-search-cancel-button]:hidden text-body-lg text-fg outline-none placeholder:text-fg-3"
            type="search"
            role="combobox"
            autocomplete="off"
            enterkeyhint="go"
            aria-autocomplete="list"
            aria-controls="palette-list"
            aria-expanded="true"
            :aria-activedescendant="results.length ? optionId(active) : undefined"
            placeholder="Search pages and settings"
            @keydown.down.prevent="move(1)"
            @keydown.up.prevent="move(-1)"
            @keydown.enter.prevent="go(results[active])"
          />
          <button type="button" class="h-ctl shrink-0 rounded-control px-3 text-small font-medium text-fg-2 hover:bg-hover hover:text-fg" @click="emit('update:open', false)">
            <span class="md:hidden">Cancel</span>
            <span class="hidden md:inline"><kbd class="text-caption">Esc</kbd><span class="sr-only"> Close</span></span>
          </button>
        </div>
        <ul id="palette-list" role="listbox" aria-label="Pages and settings" class="flex min-h-0 flex-1 flex-col overflow-auto overscroll-contain p-2 pb-[max(8px,env(safe-area-inset-bottom))]">
          <template v-for="(entry, i) in results" :key="entry.id">
            <li v-if="showsHeading(i)" role="presentation" class="px-2.5 pb-1 pt-3 text-caption font-medium uppercase tracking-wide text-fg-3 first:pt-1">{{ entry.group }}</li>
            <li
              :id="optionId(i)"
              role="option"
              :aria-selected="i === active"
              tabindex="-1"
              class="flex min-h-[44px] cursor-pointer items-center gap-3 rounded-control px-2.5 py-1.5 text-small aria-selected:bg-hover"
              @click="go(entry)"
              @keydown.enter="go(entry)"
              @pointermove="active = i"
            >
              <AppIcon :name="entry.parent ? 'subdirectory_arrow_right' : entry.icon" class="shrink-0 text-fg-2" />
              <span class="flex min-w-0 flex-1 flex-col">
                <span class="truncate text-fg">
                  <template v-if="entry.parent"><span class="text-fg-3">{{ entry.parent }} › </span></template>{{ entry.label }}
                </span>
                <span v-if="entry.description" class="truncate text-caption font-normal text-fg-3">{{ entry.description }}</span>
              </span>
              <span v-if="!grouped && !entry.parent" class="hidden shrink-0 text-caption text-fg-3 sm:inline">{{ entry.group }}</span>
            </li>
          </template>
          <li v-if="!results.length" role="presentation" class="px-2.5 py-6 text-center text-small text-fg-3">Nothing matches “{{ query }}”.</li>
        </ul>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
