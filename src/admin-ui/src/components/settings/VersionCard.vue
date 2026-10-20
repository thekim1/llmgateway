<script setup lang="ts">
import { onMounted } from 'vue'
import { useOpsStore } from '@/stores/ops'
import { formatDateTime } from '@/utils/format'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiCard from '@/components/ui/UiCard.vue'

const ops = useOpsStore()

// The app shell loads it for the nav badge; load it here too when Settings is opened first or the shell's load failed.
onMounted(() => {
  if (!ops.version) void ops.loadVersion()
})
</script>

<template>
  <UiCard title="Version" meta="Releases on GitHub" heading-id="version-h" class="max-w-xl">
    <AsyncState :loading="ops.versionLoading && !ops.version" :error="ops.versionError" :empty="false" @retry="ops.loadVersion">
      <template v-if="ops.version">
        <div
          v-if="ops.version.updateAvailable && ops.version.latest"
          class="mb-4 flex items-start gap-2 rounded-tile bg-accent-soft p-4 text-small text-accent-ink"
        >
          <AppIcon name="info" :size="18" class="mt-px shrink-0" />
          <span>
            Version {{ ops.version.latest.version }} is available.
            <a class="underline" :href="ops.version.latest.url" target="_blank" rel="noopener noreferrer">Release notes<span class="sr-only"> (opens in a new tab)</span></a>
            list the changes and any upgrade steps.
          </span>
        </div>

        <dl class="m-0">
          <DetailRow label="Running">
            <span class="font-mono">{{ ops.version.version }}</span>
            <UiBadge v-if="ops.version.developmentBuild" class="ml-2" tone="neutral" label="Development build" />
            <UiBadge v-else-if="ops.version.checkEnabled && ops.version.latest && !ops.version.updateAvailable" class="ml-2" tone="ok" label="Up to date" />
            <UiBadge v-else-if="ops.version.updateAvailable" class="ml-2" tone="accent" label="Update available" />
          </DetailRow>
          <DetailRow label="Latest release">
            <template v-if="!ops.version.checkEnabled">Not checked (switched off with <code class="font-mono text-small">Admin:CheckForReleases</code>)</template>
            <template v-else-if="ops.version.latest">
              <a class="text-accent-ink underline" :href="ops.version.latest.url" target="_blank" rel="noopener noreferrer">{{ ops.version.latest.name }}<span class="sr-only"> (opens in a new tab)</span></a>
              <span v-if="ops.version.latest.publishedAt" class="text-fg-3">, {{ formatDateTime(ops.version.latest.publishedAt) }}</span>
            </template>
            <template v-else-if="ops.version.checkFailed">Could not reach GitHub</template>
            <template v-else>No release published yet</template>
          </DetailRow>
        </dl>
        <p v-if="ops.version.checkEnabled && ops.version.checkedAt" class="mb-0 mt-3 text-small text-fg-3">
          Checked {{ formatDateTime(ops.version.checkedAt) }}<template v-if="ops.version.checkFailed">; the check failed and is retried within 30 minutes</template>. The admin API checks at most every 6 hours.
        </p>
      </template>
    </AsyncState>
  </UiCard>
</template>
