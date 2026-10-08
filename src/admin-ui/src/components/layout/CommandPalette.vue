<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { navGroups } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'
import UiDialog from '@/components/ui/UiDialog.vue'
import AppIcon from '@/components/ui/AppIcon.vue'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', v: boolean): void }>()
const router = useRouter()
const auth = useAuthStore()
const query = ref('')

const pages = computed(() =>
  navGroups.flatMap((g) => g.items).filter((i) => auth.hasAnyRole(i.roles)),
)
const results = computed(() => {
  const q = query.value.trim().toLowerCase()
  return q ? pages.value.filter((p) => p.label.toLowerCase().includes(q)) : pages.value
})

watch(() => props.open, (o) => { if (o) query.value = '' })

async function go(name: string): Promise<void> {
  emit('update:open', false)
  await router.push({ name })
}
</script>

<template>
  <UiDialog :open="open" title="Go to page" icon="search" @update:open="emit('update:open', $event)">
    <label for="palette-q" class="sr-only">Search pages</label>
    <input
      id="palette-q"
      v-model="query"
      class="field-input"
      type="search"
      autocomplete="off"
      placeholder="Search pages"
      @keydown.enter="results[0] && go(results[0].name)"
    />
    <ul class="flex max-h-72 flex-col gap-1 overflow-auto">
      <li v-for="p in results" :key="p.name">
        <button type="button" class="flex h-ctl w-full items-center gap-3 rounded-control px-2.5 text-left text-small text-fg hover:bg-hover" @click="go(p.name)">
          <AppIcon :name="p.icon" /> {{ p.label }}
        </button>
      </li>
      <li v-if="!results.length" class="px-2.5 py-2 text-small text-fg-3">No pages match “{{ query }}”.</li>
    </ul>
  </UiDialog>
</template>
