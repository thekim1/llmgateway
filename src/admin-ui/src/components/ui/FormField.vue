<script setup lang="ts">
import { computed, useId } from 'vue'
import AppIcon from './AppIcon.vue'

const props = defineProps<{ label: string; hint?: string; error?: string | null; id?: string; errorLang?: string }>()

const uid = useId()
const fieldId = computed(() => props.id ?? `f-${uid}`)
const describedBy = computed(() => [props.error ? `${fieldId.value}-err` : null, props.hint ? `${fieldId.value}-hint` : null].filter(Boolean).join(' ') || undefined)
</script>

<template>
  <div class="flex flex-col gap-1.5">
    <label :for="fieldId" class="font-medium">{{ label }}</label>
    <slot :id="fieldId" :described-by="describedBy" :invalid="!!error" />
    <span v-if="error" :id="`${fieldId}-err`" :lang="errorLang" class="flex items-start gap-1 text-small text-danger"><AppIcon name="error" :size="16" filled class="mt-px" />{{ error }}</span>
    <span v-if="hint" :id="`${fieldId}-hint`" class="text-small text-fg-3">{{ hint }}</span>
  </div>
</template>
