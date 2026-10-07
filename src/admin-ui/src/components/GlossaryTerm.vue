<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { PopoverClose, PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from 'reka-ui'
import AppIcon from '@/components/AppIcon.vue'

/**
 * Glossary "toggletip" for technical terms. Opens on click/Enter (not hover), stays open until
 * dismissed (WCAG 1.4.13), and the definition is plain text.
 */
const props = defineProps<{ term: 'virtualKey' | 'route' | 'fallback' | 'onPrem' | 'pii' | 'residency' | 'drain' | 'circuit' }>()
const { t } = useI18n()
const titleId = `glossary-${props.term}-${Math.random().toString(36).slice(2, 7)}`
</script>

<template>
  <PopoverRoot>
    <PopoverTrigger class="glossary__trigger">
      <span>{{ t(`glossary.${term}.term`) }}</span>
      <AppIcon name="help" />
      <span class="visually-hidden">{{ t('glossary.explain') }}</span>
    </PopoverTrigger>
    <PopoverPortal>
      <PopoverContent class="popover" :aria-labelledby="titleId" side="bottom" align="start" :side-offset="4">
        <p :id="titleId" class="popover__title">{{ t(`glossary.${term}.term`) }}</p>
        <p>{{ t(`glossary.${term}.definition`) }}</p>
        <PopoverClose class="btn btn--secondary">
          {{ t('common.close') }}
        </PopoverClose>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>
