<script setup lang="ts">
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import AppShell from '@/components/layout/AppShell.vue'
import AppIcon from '@/components/AppIcon.vue'
import SignInView from '@/views/SignInView.vue'
import { useAuthStore } from '@/stores/auth'

const { t } = useI18n()
const auth = useAuthStore()
const route = useRoute()
</script>

<template>
  <AppShell>
    <div v-if="auth.status === 'unknown' || auth.status === 'loading'" aria-busy="true">
      <p class="loading">
        <AppIcon name="hourglass_top" />
        {{ t('common.loading') }}
      </p>
    </div>
    <SignInView v-else-if="!auth.isAuthenticated" :return-url="route.fullPath" :failed="auth.status === 'error'" />
    <RouterView v-else />
  </AppShell>
</template>
