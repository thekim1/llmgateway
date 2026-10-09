<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { VirtualKey } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { ATTACHMENT_HINT, ATTACHMENT_LABEL, ATTACHMENT_LIMIT_NOTE, PII_HINT, PII_LABEL, modelsLabel } from '@/utils/labels'
import { formatDateTime, formatNumber, formatRelative } from '@/utils/format'
import KeyBudgets from '@/components/keys/KeyBudgets.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
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

const auth = useAuthStore()
const ui = useUiStore()

const dash = '—'
const revealed = ref<string | null>(null)
const secretBusy = ref(false)
const secretError = ref<string | null>(null)
const showSecretSection = computed(() => auth.isGatewayAdmin && props.keyData?.status !== 'Revoked')
const masked = computed(() => `${props.keyData?.prefix ?? ''}${'•'.repeat(32)}`)

// The secret never outlives the drawer or the selected key.
watch(() => [props.open, props.keyData?.id], () => {
  revealed.value = null
  secretError.value = null
})

async function fetchSecret(purpose: 'Reveal' | 'Copy'): Promise<string | null> {
  if (!props.keyData) return null
  secretBusy.value = true
  secretError.value = null
  try {
    return (await api.keys.reveal(props.keyData.id, purpose)).secret
  } catch (e) {
    secretError.value = e instanceof Error ? e.message : 'Could not get the secret.'
    return null
  } finally {
    secretBusy.value = false
  }
}

async function show(): Promise<void> {
  revealed.value = await fetchSecret('Reveal')
}

async function copySecret(): Promise<void> {
  const secret = revealed.value ?? (await fetchSecret('Copy'))
  if (secret === null) return
  try {
    await navigator.clipboard.writeText(secret)
    ui.notify('Key copied. The copy was added to the audit log')
  } catch {
    secretError.value = 'Could not write to the clipboard.'
  }
}
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
      <DetailSection v-if="showSecretSection" title="Secret key" :list="false">
        <template v-if="keyData.canReveal">
          <code class="block break-all rounded-control bg-sunken p-3 font-mono text-small" data-testid="key-secret">{{ revealed ?? masked }}</code>
          <div class="mt-3 flex flex-wrap gap-2">
            <UiButton v-if="revealed === null" size="sm" icon="visibility" :disabled="secretBusy" @click="show">Show</UiButton>
            <UiButton v-else size="sm" icon="visibility_off" @click="revealed = null">Hide</UiButton>
            <UiButton size="sm" icon="content_copy" :disabled="secretBusy" @click="copySecret">Copy</UiButton>
          </div>
          <p class="mt-2 text-small text-fg-3">Showing or copying the key is recorded in the audit log with your name.</p>
        </template>
        <p v-else class="text-small text-fg-3">This key was created before keys could be viewed later. Rotate it to get a key that can be shown.</p>
        <p v-if="secretError" class="mt-2 text-small text-danger" role="alert">{{ secretError }}</p>
      </DetailSection>
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
        <DetailRow label="Providers">
          <span v-if="!keyData.allowedProviders?.length">Any provider</span>
          <span v-else class="font-mono text-small">{{ keyData.allowedProviders.join(', ') }}</span>
        </DetailRow>
        <DetailRow label="PII policy">
          {{ PII_LABEL[keyData.piiPolicy] }}
          <span class="block text-small text-fg-3">{{ PII_HINT[keyData.piiPolicy] }}</span>
        </DetailRow>
        <DetailRow label="Attached files">
          {{ ATTACHMENT_LABEL[keyData.attachmentPolicy] }}
          <span class="block text-small text-fg-3">{{ ATTACHMENT_HINT[keyData.attachmentPolicy] }}</span>
          <span v-if="keyData.attachmentPolicy !== 'Allowed'" class="mt-1 flex items-start gap-1.5 text-small text-fg-3">
            <AppIcon name="info" :size="16" class="mt-px shrink-0" />{{ ATTACHMENT_LIMIT_NOTE }}
          </span>
        </DetailRow>
      </DetailSection>
      <DetailSection title="Limits">
        <DetailRow label="Requests per minute"><span class="tabular">{{ keyData.requestsPerMinute === null ? 'No limit' : formatNumber(keyData.requestsPerMinute) }}</span></DetailRow>
        <DetailRow label="Tokens per minute"><span class="tabular">{{ keyData.tokensPerMinute === null ? 'No limit' : formatNumber(keyData.tokensPerMinute) }}</span></DetailRow>
        <DetailRow label="Expires">{{ keyData.expiresAt ? formatDateTime(keyData.expiresAt) : 'Never' }}</DetailRow>
      </DetailSection>
      <DetailSection title="Budgets" :list="false">
        <KeyBudgets :key-id="keyData.id" :editable="auth.canManage && keyData.status !== 'Revoked'" />
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
