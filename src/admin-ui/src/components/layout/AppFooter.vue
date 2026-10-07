<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
const year = new Date().getFullYear()
</script>

<template>
  <footer class="app-footer">
    <div class="app-footer__inner">
      <div>
        <p class="app-footer__title">{{ t('app.organisation') }} · {{ t('app.product') }}</p>
        <p>{{ t('footer.description') }}</p>
      </div>
      <ul class="app-footer__links">
        <li>
          <RouterLink :to="{ name: 'getting-started' }">{{ t('footer.gettingStarted') }}</RouterLink>
        </li>
        <li>
          <RouterLink :to="{ name: 'catalog' }">{{ t('footer.catalog') }}</RouterLink>
        </li>
        <li>
          <RouterLink :to="{ name: 'settings' }">{{ t('footer.settings') }}</RouterLink>
        </li>
      </ul>
      <p class="app-footer__meta">© {{ year }} {{ t('app.organisation') }}</p>
    </div>
  </footer>
</template>
