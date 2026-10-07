<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '@/components/AppIcon.vue'
import { navGroups } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'

defineProps<{ id: string; open: boolean }>()
const emit = defineEmits<{ navigate: [] }>()

const { t } = useI18n()
const auth = useAuthStore()

const groups = computed(() =>
  navGroups
    .map((group) => ({ ...group, items: group.items.filter((item) => auth.hasAnyRole(item.roles)) }))
    .filter((group) => group.items.length > 0),
)
</script>

<template>
  <nav :id="id" class="app-nav" :class="{ 'app-nav--open': open }" :aria-label="t('nav.label')">
    <div v-for="group in groups" :key="group.id" class="app-nav__group">
      <p :id="`nav-group-${group.id}`" class="app-nav__group-label">{{ t(group.labelKey) }}</p>
      <ul :aria-labelledby="`nav-group-${group.id}`">
        <li v-for="item in group.items" :key="item.name">
          <RouterLink :to="{ name: item.name }" class="app-nav__link" @click="emit('navigate')">
            <AppIcon :name="item.icon" />
            <span>{{ t(item.labelKey) }}</span>
          </RouterLink>
        </li>
      </ul>
    </div>
  </nav>
</template>
