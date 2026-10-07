<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/PageHeader.vue'
import ResourcePanel from '@/components/ResourcePanel.vue'
import type { Resource } from '@/components/resourceFields'
defineProps<{ resource: Resource; titleKey: string }>()
const { t } = useI18n()
</script>
<template><PageHeader :title="t(titleKey)" /><ResourcePanel :key="resource" :resource="resource" :title="t(titleKey)" /></template>
