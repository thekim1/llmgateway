<script setup lang="ts">
import { computed } from 'vue'
import type { VirtualKey } from '@/api/types'
import { PII_HINT, PII_LABEL, modelsLabel } from '@/utils/labels'
import { formatDateTime, formatNumber, formatRelative } from '@/utils/format'
import DetailRow from '@/components/ui/DetailRow.vue'
import DetailSection from '@/components/ui/DetailSection.vue'
import KeyStatusBadge from '@/components/ui/KeyStatusBadge.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'

const props = defineProps<{ open: boolean; keyData: VirtualKey | null }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'rotate'): void
  (e: 'edit'): void
  (e: 'revoke'): void
}>()

const dash = '—'
const inactive = computed(() => props.keyData?.status === 'Revoked' || props.keyData?.status === 'Expired')
const reason = computed(() => {
  if (props.keyData?.status === 'Revoked') return 'This key is revoked, so it can’t be changed.'
  if (props.keyData?.status === 'Expired') return 'This key has expired, so it can’t be rotated or revoked.'
  return ''
})
</script>

<template>
  <UiDrawer :open="open" :title="keyData?.name ?? 'Key'" :subtitle="keyData?.prefix" @update:open="emit('update:open', $event)">
    <template #badge><KeyStatusBadge v-if="keyData" :status="keyData.status" /></template>
    <template v-if="keyData">
      <p v-if="keyData.description" class="mb-6 text-fg-2">{{ keyData.description }}</p>
      <DetailSection title="Owner">
        <DetailRow label="Team"><span lang="sv">{{ keyData.teamName }}</span></DetailRow>
        <DetailRow label="Department"><span lang="sv">{{ keyData.departmentName }}</span></DetailRow>
        <DetailRow label="Created by">{{ keyData.createdBy ?? dash }}</DetailRow>
      </DetailSection>
      <DetailSection title="Access">
        <DetailRow label="Models"><span :class="keyData.allowedModels.length ? 'font-mono text-small' : ''">{{ modelsLabel(keyData.allowedModels) }}</span></DetailRow>
        <DetailRow label="Residency">
          <span v-if="keyData.allowedResidencies.length === 0">Any residency</span>
          <span v-else class="flex flex-wrap gap-x-3">
            <ResidencyLabel v-for="r in keyData.allowedResidencies" :key="r" :residency="r" />
          </span>
        </DetailRow>
        <DetailRow label="PII policy">
          {{ PII_LABEL[keyData.piiPolicy] }}
          <span class="block text-small text-fg-3">{{ PII_HINT[keyData.piiPolicy] }}</span>
        </DetailRow>
      </DetailSection>
      <DetailSection title="Limits">
        <DetailRow label="Requests per minute"><span class="tabular">{{ keyData.requestsPerMinute === null ? 'No limit' : formatNumber(keyData.requestsPerMinute) }}</span></DetailRow>
        <DetailRow label="Tokens per minute"><span class="tabular">{{ keyData.tokensPerMinute === null ? 'No limit' : formatNumber(keyData.tokensPerMinute) }}</span></DetailRow>
        <DetailRow label="Expires">{{ keyData.expiresAt ? formatDateTime(keyData.expiresAt) : 'Never' }}</DetailRow>
      </DetailSection>
      <DetailSection title="Lifecycle">
        <DetailRow label="Created">{{ formatDateTime(keyData.createdAt) }}</DetailRow>
        <DetailRow label="Last used">{{ keyData.lastUsedAt ? formatRelative(keyData.lastUsedAt) : 'Never used' }}</DetailRow>
        <DetailRow v-if="keyData.graceUntil" label="Grace period ends">{{ formatDateTime(keyData.graceUntil) }}</DetailRow>
        <DetailRow v-if="keyData.revokedAt" label="Revoked">{{ formatDateTime(keyData.revokedAt) }}</DetailRow>
        <DetailRow label="Enabled">{{ keyData.isEnabled ? 'Yes' : 'No' }}</DetailRow>
      </DetailSection>
    </template>
    <template #footer>
      <UiButton icon="autorenew" :disabled="inactive" @click="emit('rotate')">Rotate</UiButton>
      <UiButton icon="edit" :disabled="keyData?.status === 'Revoked'" @click="emit('edit')">Edit</UiButton>
      <UiButton class="ml-auto" variant="danger" icon="block" :disabled="inactive" @click="emit('revoke')">Revoke</UiButton>
      <p v-if="reason" class="basis-full text-small text-fg-3">{{ reason }}</p>
    </template>
  </UiDrawer>
</template>
