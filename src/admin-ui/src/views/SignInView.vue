<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/PageHeader.vue'
import AppIcon from '@/components/AppIcon.vue'
import { useAuthStore } from '@/stores/auth'

const props = defineProps<{ returnUrl: string; failed?: boolean }>()
const { t } = useI18n()
const auth = useAuthStore()

const loginHref = computed(() => auth.loginUrl(props.returnUrl))
const sessionExpired = computed(() => auth.user !== null && !auth.user.isAuthenticated && !!auth.user.name)
</script>

<template>
  <div class="sign-in">
    <PageHeader :title="t('auth.signInTitle')" :lead="t('auth.signInIntro')" />
    <div v-if="failed" class="notice notice--danger">
      <AppIcon name="error" />
      <p>{{ t('auth.loadFailed') }}</p>
    </div>
    <div v-else-if="sessionExpired" class="notice notice--info">
      <AppIcon name="info" />
      <p>{{ t('auth.sessionExpired') }}</p>
    </div>
    <p>
      <a :href="loginHref" class="btn btn--primary">
        <AppIcon name="login" />
        {{ t('auth.signIn') }}
      </a>
    </p>
    <p class="help">{{ t('auth.signInHelp') }}</p>
  </div>
</template>
