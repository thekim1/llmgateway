<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { useI18n } from 'vue-i18n'
import type { KeyRotationMode } from '@/api/types'
import { useKeysStore } from '@/stores/keys'
import { useNotify } from '@/composables/useForm'
import PageHeader from '@/components/PageHeader.vue'
import KeyForm from '@/components/KeyForm.vue'
import AsyncState from '@/components/AsyncState.vue'
import KeyStatusBadge from '@/components/KeyStatusBadge.vue'
import RotateKeyDialog from '@/components/RotateKeyDialog.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ShowSecretDialog from '@/components/ShowSecretDialog.vue'
const props = defineProps<{ id: string }>()
const { t } = useI18n()
const keys = useKeysStore()
const notify = useNotify()
const rotateOpen = ref(false)
const revokeOpen = ref(false)
const busy = ref(false)
const secret = ref('')
const secretOpen = ref(false)
const secretName = ref('')
const load = () => keys.loadOne(props.id)
async function rotate(mode: KeyRotationMode): Promise<void> {
  if (busy.value) return
  busy.value = true
  try {
    const result = await keys.rotate(props.id, mode)
    secret.value = result.secret
    secretName.value = result.key.name
    secretOpen.value = true
    rotateOpen.value = false
  } catch (e) { notify.error(e) } finally { busy.value = false }
}
async function revoke(): Promise<void> {
  if (busy.value) return
  busy.value = true
  try { await keys.revoke(props.id); revokeOpen.value = false; notify.success(t('common.saved')) }
  catch (e) { notify.error(e) } finally { busy.value = false }
}
onBeforeRouteLeave(() => !secretOpen.value)
onBeforeUnmount(() => { secret.value = '' })
onMounted(load)
watch(() => props.id, load)
</script>
<template>
  <PageHeader :title="keys.current?.name ?? t('keys.detail.breadcrumb')" :lead="t('keys.budgetHint')" />
  <AsyncState :loading="keys.loading" :error="keys.error" @retry="load">
    <template v-if="keys.current">
      <p><code>{{ keys.current.prefix }}</code> · <KeyStatusBadge :status="keys.current.status" /></p>
      <div class="actions"><button class="btn btn--primary" type="button" :disabled="!!keys.current.rotatedToKeyId || !['Active', 'InGracePeriod'].includes(keys.current.status)" @click="rotateOpen = true">{{ t('keys.rotate') }}</button><button class="btn btn--danger" type="button" :disabled="keys.current.status === 'Revoked'" @click="revokeOpen = true">{{ t('keys.revoke') }}</button></div>
      <h2>{{ t('keys.detail.breadcrumb') }}</h2><KeyForm :key="keys.current.id" :existing="keys.current" @saved="load" />
      <RouterLink v-if="keys.current.rotatedToKeyId" :to="{ name: 'key-detail', params: { id: keys.current.rotatedToKeyId } }">{{ t('keys.detail.breadcrumb') }} · {{ t('keys.rotate') }}</RouterLink>
      <RotateKeyDialog v-model:open="rotateOpen" :key-name="keys.current.name" :busy="busy" @confirm="rotate" />
      <ConfirmDialog v-model:open="revokeOpen" :title="t('keys.revoke')" :description="t('keys.revokeHelp')" :confirm-label="t('keys.revoke')" :acknowledge-label="t('common.acknowledge')" :busy="busy" @confirm="revoke" />
    </template>
  </AsyncState>
  <ShowSecretDialog v-model:open="secretOpen" :secret="secret" :key-name="secretName" @closed="secret = ''" />
</template>
