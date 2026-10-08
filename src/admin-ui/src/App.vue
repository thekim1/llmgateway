<script setup lang="ts">
import { onMounted } from 'vue'
import { RouterView } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import AppShell from '@/components/layout/AppShell.vue'
import SignIn from '@/components/layout/SignIn.vue'

const auth = useAuthStore()
onMounted(() => void auth.ensureLoaded())
</script>

<template>
  <div v-if="auth.status === 'unknown' || auth.status === 'loading'" class="flex min-h-screen items-center justify-center text-small text-fg-3" role="status">Loading…</div>
  <SignIn v-else-if="auth.status === 'error'" failed />
  <SignIn v-else-if="!auth.isAuthenticated" />
  <AppShell v-else><RouterView /></AppShell>
</template>
