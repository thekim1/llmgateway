<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/PageHeader.vue'
import ResourcePanel from '@/components/ResourcePanel.vue'
import KeyForm from '@/components/KeyForm.vue'
import type { VirtualKey } from '@/api/types'
const { t } = useI18n()
const step = ref(0)
const teamId = ref('')
const key = ref<VirtualKey | null>(null)
function teamSaved(record: Record<string, unknown>): void {
  if (typeof record.id === 'string') { teamId.value = record.id; step.value = 1 }
}
</script>
<template>
  <PageHeader :title="t('wizard.title')" :lead="t('wizard.help')" />
  <ol class="steps"><li v-for="(title, index) in ['wizard.team', 'wizard.key', 'wizard.budget', 'wizard.finish']" :key="title" :aria-current="index === step ? 'step' : undefined">{{ index + 1 }}. {{ t(title) }}</li></ol>
  <ResourcePanel v-if="step === 0" resource="teams" :title="t('wizard.team')" create-only @saved="teamSaved" />
  <KeyForm v-if="step === 1" :team-id="teamId" @saved="key = $event; step = 2" />
  <template v-if="step === 2"><ResourcePanel resource="budgets" :title="t('wizard.budget')" :initial="{ scope: 'Team', scopeId: teamId }" create-only @saved="step = 3" /><p>{{ t('keys.budgetHint') }}</p></template>
  <template v-if="step === 3"><h2>{{ t('wizard.finish') }}</h2><RouterLink v-if="key" :to="{ name: 'key-detail', params: { id: key.id } }">{{ t('common.details') }} · {{ key.name }}</RouterLink></template>
</template>
