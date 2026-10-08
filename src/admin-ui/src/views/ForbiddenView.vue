<script setup lang="ts">
import { RouterLink } from 'vue-router'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
</script>

<template>
  <PageHeader title="No access" description="Your role doesn’t include this page. Ask a gateway administrator if you need it." />
  <RouterLink v-slot="{ href, navigate }" :to="{ name: 'overview' }" custom>
    <UiButton variant="primary" icon="space_dashboard" :href="href" @click="navigate">Go to overview</UiButton>
  </RouterLink>
</template>
