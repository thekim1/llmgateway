<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import AppIcon from '@/components/ui/AppIcon.vue'
import UiButton from '@/components/ui/UiButton.vue'

defineProps<{ failed?: boolean }>()
const auth = useAuthStore()
const route = useRoute()
const href = computed(() => auth.loginUrl(route.fullPath))
const expired = computed(() => auth.user !== null && !auth.user.isAuthenticated && !!auth.user.name)
</script>

<template>
  <main id="main" tabindex="-1" class="flex min-h-screen items-center justify-center p-4 outline-none">
    <div class="material-overlay flex w-full max-w-md flex-col gap-5 rounded-dialog p-8">
      <span class="flex h-11 w-11 items-center justify-center rounded-control bg-fg text-on-fg" aria-hidden="true"><AppIcon name="hub" /></span>
      <div>
        <h1 id="page-title" tabindex="-1" class="text-title-2 text-fg outline-none">Sign in to AI Gateway</h1>
        <p class="mt-1 text-body text-fg-2">Admin console for Umeå kommun. Use your organisation account.</p>
      </div>
      <p v-if="failed" role="alert" class="flex gap-2 text-small text-danger"><AppIcon name="error" /> Could not check your session. Try again.</p>
      <p v-else-if="expired" role="status" class="flex gap-2 text-small text-fg-2"><AppIcon name="info" /> Your session has expired. Sign in again.</p>
      <UiButton variant="primary" size="lg" icon="login" :href="href">Sign in</UiButton>
    </div>
  </main>
</template>
