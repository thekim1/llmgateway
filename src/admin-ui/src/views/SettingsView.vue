<script setup lang="ts">
import PageHeader from '@/components/ui/PageHeader.vue'
import RequestLogCard from '@/components/settings/RequestLogCard.vue'
import ModelPricesCard from '@/components/settings/ModelPricesCard.vue'
import RetentionCard from '@/components/settings/RetentionCard.vue'
import TrustedProxiesCard from '@/components/settings/TrustedProxiesCard.vue'
import VersionCard from '@/components/settings/VersionCard.vue'
</script>

<template>
  <PageHeader title="Settings" description="Gateway-wide settings. Only gateway admins can change them." />

  <TrustedProxiesCard />

  <div class="mt-6">
    <RetentionCard />
  </div>

  <div class="mt-6">
    <ModelPricesCard />
  </div>

  <div class="mt-6">
    <RequestLogCard />
  </div>

  <div class="mt-6">
    <VersionCard />
  </div>
</template>
