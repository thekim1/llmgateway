<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import {
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuPortal,
  DropdownMenuRoot,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from 'reka-ui'
import AppIcon from '@/components/AppIcon.vue'
import { useAuthStore } from '@/stores/auth'
import { useNotify } from '@/composables/useForm'

const { t, te } = useI18n()
const auth = useAuthStore()
const notify = useNotify()
const router = useRouter()

const displayName = computed(() => auth.user?.name || auth.user?.email || t('auth.unknownUser'))
const roleNames = computed(() =>
  auth.roles.map((r) => (te(`roles.${r}`) ? t(`roles.${r}`) : r)).join(', ') || t('auth.noRoles'),
)

async function signOut(): Promise<void> {
  try { window.location.assign(await auth.logout()) } catch (error) { notify.error(error) }
}
</script>

<template>
  <DropdownMenuRoot :modal="false">
    <DropdownMenuTrigger class="btn btn--ghost user-menu__trigger">
      <AppIcon name="account_circle" />
      <span class="user-menu__name">{{ displayName }}</span>
      <AppIcon name="expand_more" />
    </DropdownMenuTrigger>
    <DropdownMenuPortal>
      <DropdownMenuContent class="menu" align="end" :side-offset="4">
        <DropdownMenuLabel class="menu__label">
          <span class="menu__label-name">{{ displayName }}</span>
          <span v-if="auth.user?.email" class="menu__label-meta">{{ auth.user.email }}</span>
          <span class="menu__label-meta">{{ t('auth.rolesLabel') }}: {{ roleNames }}</span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator class="menu__separator" />
        <DropdownMenuItem class="menu__item" @select="router.push({ name: 'settings' })">
          <AppIcon name="settings" />
          {{ t('nav.settings') }}
        </DropdownMenuItem>
        <DropdownMenuItem class="menu__item" @select="signOut">
          <AppIcon name="logout" />
          {{ t('auth.signOut') }}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenuPortal>
  </DropdownMenuRoot>
</template>
