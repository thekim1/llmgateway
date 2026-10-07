<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'
import ThemePicker from '@/components/ThemePicker.vue'
import LocalePicker from '@/components/LocalePicker.vue'
import UserMenu from './UserMenu.vue'
import { useAuthStore } from '@/stores/auth'

defineProps<{ navOpen: boolean; navId: string }>()
const emit = defineEmits<{ toggleNav: [] }>()

const { t } = useI18n()
const auth = useAuthStore()
</script>

<template>
  <header class="app-header">
    <div class="app-header__inner">
      <RouterLink to="/" class="app-header__brand">
        <span class="app-header__org">{{ t('app.organisation') }}</span>
        <span class="app-header__sep" aria-hidden="true">·</span>
        <span class="app-header__product">{{ t('app.product') }}</span>
      </RouterLink>
      <div class="app-header__tools">
        <button
          v-if="auth.isAuthenticated"
          type="button"
          class="btn btn--secondary app-header__menu-toggle"
          :aria-expanded="navOpen"
          :aria-controls="navId"
          @click="emit('toggleNav')"
        >
          <AppIcon :name="navOpen ? 'close' : 'menu'" />
          {{ t('nav.menuButton') }}
        </button>
        <ThemePicker id="header-theme" compact />
        <LocalePicker id="header-locale" compact />
        <UserMenu v-if="auth.isAuthenticated" />
      </div>
    </div>
  </header>
</template>
