<script setup lang="ts">
import { RouterLink } from 'vue-router'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
</script>

<template>
  <PageHeader title="Page not found" description="The page may have moved or the address is wrong." />
  <RouterLink v-slot="{ href, navigate }" :to="{ name: 'overview' }" custom>
    <UiButton variant="primary" icon="space_dashboard" :href="href" @click="navigate">Go to overview</UiButton>
  </RouterLink>
</template>
