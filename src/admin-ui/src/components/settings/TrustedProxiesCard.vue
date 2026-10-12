<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { NetworkSettings } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useUiStore } from '@/stores/ui'
import { addressListError, formatAddressList, parseAddressList } from '@/utils/addresses'
import { formatDateTime } from '@/utils/format'
import AddressListField from '@/components/ui/AddressListField.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'

const ui = useUiStore()
const ids = { trustedProxies: 'trusted-proxies' } as const
const { errors, validate } = useFormErrors(ids)

const settings = ref<NetworkSettings | null>(null)
const loading = ref(true)
const loadError = ref<unknown>(null)
const text = ref('')
const pending = ref<string[]>([])
const confirmOpen = ref(false)

const observed = computed(() => settings.value?.observed ?? null)
const observedListed = computed(() => !!observed.value?.remoteAddress && parseAddressList(text.value).includes(observed.value.remoteAddress))
const consequence = computed(() =>
  pending.value.length === 0
    ? 'The gateways will ignore X-Forwarded-For and treat the connection address as the client. Behind a proxy, keys restricted to client addresses will be refused.'
    : `The gateways will believe X-Forwarded-For from ${pending.value.join(', ')}. Only list proxies you operate.`,
)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    settings.value = await api.settings.network()
    text.value = formatAddressList(settings.value.trustedProxies)
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}

function useObserved(): void {
  const address = observed.value?.remoteAddress
  if (!address) return
  text.value = [...parseAddressList(text.value), address].join('\n')
}

function review(): void {
  const entries = parseAddressList(text.value)
  const everything = entries.find((e) => /\/0$/.test(e))
  if (!validate({ trustedProxies: addressListError(entries) ?? (everything ? `“${everything}” would trust every caller. List the proxy’s own addresses.` : null) })) return
  pending.value = entries
  confirmOpen.value = true
}

// A refusal from the server is shown in the confirmation dialog.
async function save(): Promise<void> {
  settings.value = await api.settings.updateNetwork(pending.value)
  text.value = formatAddressList(settings.value.trustedProxies)
  ui.notify('Trusted proxies saved')
}

onMounted(load)
</script>

<template>
  <UiCard title="Trusted proxies" meta="Client addresses" heading-id="proxies-h" class="max-w-3xl">
    <AsyncState :loading="loading" :error="loadError" :empty="false" @retry="load">
      <div class="flex flex-col gap-3 text-fg-2">
        <p class="m-0">
          Behind a reverse proxy or load balancer, every request reaches the gateway from the proxy’s address. List the proxies here and the
          gateway uses the client address the proxy adds in <code class="font-mono text-small">X-Forwarded-For</code> instead. Keys
          restricted to client addresses, and the client addresses recorded for refused credentials, depend on this.
        </p>
        <p class="m-0">Leave the list empty when clients connect to the gateway directly.</p>
      </div>

      <div v-if="observed?.remoteAddress" class="mt-4 flex flex-col gap-2 rounded-tile bg-surface-2 p-4 text-small">
        <p class="m-0 flex items-start gap-2">
          <AppIcon name="info" :size="18" class="mt-px shrink-0" />
          <span>
            Your browser’s request to the admin API arrived from <code class="font-mono">{{ observed.remoteAddress }}</code><template v-if="observed.forwardedFor">
              with <code class="font-mono">X-Forwarded-For: {{ observed.forwardedFor }}</code></template>.
            <template v-if="observed.forwardedFor"> That address is probably your proxy. If the gateway is behind the same proxy, it is the address to trust.</template>
            <template v-else> No proxy header was added, so you may be connecting directly.</template>
          </span>
        </p>
        <div v-if="observed.forwardedFor && !observedListed">
          <UiButton size="sm" icon="add" @click="useObserved">Add {{ observed.remoteAddress }}</UiButton>
        </div>
      </div>

      <form class="mt-4 flex flex-col items-start gap-4" novalidate @submit.prevent="review">
        <div class="w-full">
          <AddressListField
            :id="ids.trustedProxies"
            v-model="text"
            label="Proxy addresses"
            placeholder="10.0.0.5&#10;10.0.1.0/28"
            :rows="4"
            :error="errors.trustedProxies"
            hint="One IP address or CIDR network per line. Takes effect on the gateways within seconds."
          />
        </div>
        <details class="w-full text-small text-fg-2">
          <summary class="cursor-pointer text-accent-ink">Before you change this</summary>
          <ul class="mt-2 flex list-disc flex-col gap-1.5 pl-5">
            <li>List only the addresses of proxies you operate. A trusted address can claim to be any client, so anyone who can connect from it can get past key address restrictions.</li>
            <li>Do not trust a network that also contains clients, for example a whole office network. A wide network such as 10.0.0.0/8 is only safe if nothing but proxies uses it.</li>
            <li>The proxy must append the client address to <code class="font-mono">X-Forwarded-For</code> (nginx: <code class="font-mono">$proxy_add_x_forwarded_for</code>; Nginx Proxy Manager does this by default). The gateway reads the header from the right and stops at the first address that is not a trusted proxy.</li>
            <li>With several proxies in a row (for example a load balancer in front of nginx), list all of them.</li>
            <li>If the list is wrong, keys restricted to client addresses are refused with <code class="font-mono">address_not_allowed</code>. Keys without restrictions keep working.</li>
            <li>Containers on a Docker network reach the gateway from the network’s gateway address, not the host’s.</li>
          </ul>
        </details>
        <p v-if="settings?.updatedAt" class="m-0 text-small text-fg-3">Last changed {{ formatDateTime(settings.updatedAt) }}. Changes are in the audit log.</p>
        <UiButton type="submit" variant="primary">Review change</UiButton>
      </form>
    </AsyncState>
  </UiCard>

  <ConfirmDialog v-model:open="confirmOpen" title="Change trusted proxies?" :consequence="consequence" confirm-label="Save proxies" :action="save" />
</template>
