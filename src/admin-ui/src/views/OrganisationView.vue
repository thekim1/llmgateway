<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { ref } from 'vue'
import PageHeader from '@/components/PageHeader.vue'
import ResourcePanel from '@/components/ResourcePanel.vue'
const { t } = useI18n()
const revision = ref(0)
</script>
<template>
  <PageHeader :title="t('nav.organisation')"><template #actions><RouterLink class="btn btn--primary" :to="{ name: 'team-wizard' }">{{ t('wizard.title') }}</RouterLink></template></PageHeader>
  <ResourcePanel resource="departments" :title="t('fields.departmentId')" @saved="revision++" />
  <ResourcePanel :key="revision" resource="teams" :title="t('fields.teamId')" />
</template>
