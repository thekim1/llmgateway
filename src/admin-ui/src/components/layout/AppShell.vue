<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import AppHeader from './AppHeader.vue'
import AppNav from './AppNav.vue'
import AppFooter from './AppFooter.vue'
import AppBreadcrumbs from './AppBreadcrumbs.vue'
import ToastRegion from './ToastRegion.vue'
import SessionExpiryDialog from './SessionExpiryDialog.vue'
import { useAuthStore } from '@/stores/auth'

/** Page frame: skip link, header, navigation, main, footer (landmarks), live region. */
const { t } = useI18n()
const auth = useAuthStore()
const route = useRoute()
const navOpen = ref(false)

watch(
  () => route.fullPath,
  () => (navOpen.value = false),
)

function skipToMain(): void {
  const main = document.getElementById('main')
  if (!main) return
  main.focus()
  main.scrollIntoView()
}
</script>

<template>
  <div class="app">
    <a class="skip-link" href="#main" @click.prevent="skipToMain">{{ t('app.skipLink') }}</a>
    <AppHeader :nav-open="navOpen" nav-id="main-nav" @toggle-nav="navOpen = !navOpen" />
    <div class="app__body" :class="{ 'app__body--no-nav': !auth.isAuthenticated }">
      <AppNav v-if="auth.isAuthenticated" id="main-nav" :open="navOpen" @navigate="navOpen = false" />
      <main id="main" class="app__main" tabindex="-1">
        <AppBreadcrumbs />
        <ToastRegion />
        <slot />
      </main>
    </div>
    <AppFooter />
    <SessionExpiryDialog v-if="auth.isAuthenticated" />
  </div>
</template>
