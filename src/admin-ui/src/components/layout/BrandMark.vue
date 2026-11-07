<script setup lang="ts">
import { ref, watch } from 'vue'
import { useBrandingStore } from '@/stores/branding'
import AppIcon from '@/components/ui/AppIcon.vue'

/** The console's logo: the organisation's own when a gateway admin has set one, otherwise the built-in icon. Decorative. */
const props = withDefaults(defineProps<{ size?: 'sm' | 'md' | 'lg' }>(), { size: 'md' })
const branding = useBrandingStore()
const failed = ref(false)
watch(
  () => branding.logoUrl,
  () => (failed.value = false),
)
const box = { sm: 'h-6 w-6', md: 'h-9 w-9', lg: 'h-11 w-11' }[props.size]
</script>

<template>
  <img v-if="branding.logoUrl && !failed" :src="branding.logoUrl" alt="" :class="box" class="shrink-0 rounded-control object-contain" @error="failed = true" />
  <AppIcon v-else-if="size === 'sm'" name="hub" aria-hidden="true" />
  <span v-else :class="box" class="flex shrink-0 items-center justify-center rounded-control bg-fg text-on-fg" aria-hidden="true">
    <AppIcon name="hub" />
  </span>
</template>
