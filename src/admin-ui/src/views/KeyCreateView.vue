<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/PageHeader.vue'
import KeyForm from '@/components/KeyForm.vue'
const { t } = useI18n()
const router = useRouter()
</script>
<template><PageHeader :title="t('keys.create.title')" /><KeyForm @saved="router.push({ name: 'key-detail', params: { id: $event.id } })" /></template>
