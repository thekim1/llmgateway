<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/PageHeader.vue'
const { t } = useI18n()
</script>
<template><PageHeader :title="t('errors.forbiddenTitle')" :lead="t('errors.forbiddenBody')" /><RouterLink :to="{ name: 'overview' }">{{ t('nav.overview') }}</RouterLink></template>
