<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Branding } from '@/api/types'
import { useFormErrors } from '@/composables/useFormErrors'
import { useBrandingStore } from '@/stores/branding'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { problemMessage } from '@/utils/problem'
import BrandMark from '@/components/layout/BrandMark.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'

/** Same limits as the admin API (Branding.MaxOrganisationNameLength, Branding.MaxLogoBytes). */
const MAX_NAME = 80
const MAX_LOGO_BYTES = 256 * 1024
const LOGO_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/svg+xml']

const ui = useUiStore()
const store = useBrandingStore()
const ids = { organisationName: 'branding-name' } as const
const { errors, formError, validate, applyServerError } = useFormErrors(ids)

const loading = ref(true)
const loadError = ref<unknown>(null)
const name = ref('')
const savingName = ref(false)
const logoError = ref<string | null>(null)
const uploading = ref(false)
const removeOpen = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

function show(value: Branding): void {
  store.set(value)
  name.value = value.organisationName
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    show(await api.bff.branding())
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}

async function saveName(): Promise<void> {
  const trimmed = name.value.trim()
  if (!validate({ organisationName: trimmed.length > MAX_NAME ? `Use at most ${MAX_NAME} characters.` : null })) return
  savingName.value = true
  try {
    show(await api.settings.updateBranding(trimmed))
    ui.notify(trimmed ? 'Organisation name saved' : 'Organisation name removed')
  } catch (e) {
    applyServerError(e)
  } finally {
    savingName.value = false
  }
}

function base64Of(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''))
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(file)
  })
}

async function onFile(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  logoError.value = null
  if (!LOGO_TYPES.includes(file.type)) {
    logoError.value = 'Choose a PNG, JPEG, WebP or SVG image.'
    return
  }
  if (file.size > MAX_LOGO_BYTES) {
    logoError.value = `The image is ${Math.ceil(file.size / 1024)} KB; the limit is ${MAX_LOGO_BYTES / 1024} KB. It is shown at 36 pixels, so a small image is enough.`
    return
  }
  uploading.value = true
  try {
    show(await api.settings.uploadLogo(await base64Of(file)))
    ui.notify('Logo saved')
  } catch (e) {
    logoError.value = problemMessage(e)
  } finally {
    uploading.value = false
  }
}

// A refusal from the server is shown in the confirmation dialog.
async function removeLogo(): Promise<void> {
  show(await api.settings.removeLogo())
  ui.notify('Logo removed')
}

onMounted(load)
</script>

<template>
  <UiCard title="Branding" meta="Admin console" heading-id="branding-h" class="max-w-3xl">
    <AsyncState :loading="loading" :error="loadError" :empty="false" @retry="load">
      <p class="m-0 text-fg-2">
        The logo and organisation name shown at the top of the menu and on the sign-in page, for everyone who opens the console. Changes show
        at once for you and within 30 seconds for others.
      </p>

      <form class="mt-4 flex flex-col items-start gap-3" novalidate @submit.prevent="saveName">
        <div class="w-full max-w-md">
          <TextField
            :id="ids.organisationName"
            v-model="name"
            label="Organisation name"
            :placeholder="'Umeå kommun'"
            :error="errors.organisationName"
            :hint="`Shown under “AI Gateway”. At most ${MAX_NAME} characters; leave empty to show no name.`"
          />
        </div>
        <InlineError v-if="formError" :message="formError" />
        <UiButton type="submit" variant="primary" :loading="savingName">Save name</UiButton>
      </form>

      <div class="mt-6 flex flex-col gap-3">
        <h3 class="m-0 text-body font-semibold text-fg">Logo</h3>
        <div class="flex items-center gap-3">
          <BrandMark size="lg" />
          <span class="text-small text-fg-2">{{ store.logoUrl ? 'Your logo' : 'The built-in icon (no logo set)' }}</span>
        </div>
        <p class="m-0 text-small text-fg-3">PNG, JPEG, WebP or SVG, at most 256 KB. Shown at 36 × 36 pixels; a square image works best. SVG files may not contain scripts.</p>
        <input
          ref="fileInput"
          type="file"
          class="sr-only"
          accept="image/png,image/jpeg,image/webp,image/svg+xml"
          tabindex="-1"
          aria-hidden="true"
          @change="onFile"
        />
        <div class="flex flex-wrap gap-3">
          <UiButton icon="upload" :loading="uploading" @click="fileInput?.click()">{{ store.logoUrl ? 'Replace logo…' : 'Upload logo…' }}</UiButton>
          <UiButton v-if="store.logoUrl" variant="quiet" icon="delete" @click="removeOpen = true">Remove logo…</UiButton>
        </div>
        <InlineError v-if="logoError" :message="logoError" />
      </div>

      <p v-if="store.branding?.updatedAt" class="mb-0 mt-4 text-small text-fg-3">
        Last changed {{ formatDateTime(store.branding.updatedAt) }}. Changes are in the audit log.
      </p>
    </AsyncState>
  </UiCard>

  <ConfirmDialog
    v-model:open="removeOpen"
    title="Remove the logo?"
    consequence="The console shows the built-in icon again, for everyone."
    confirm-label="Remove"
    :action="removeLogo"
  />
</template>
