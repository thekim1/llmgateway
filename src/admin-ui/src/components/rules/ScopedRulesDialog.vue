<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { RoutingRulesChoice, ScopedRulesProblem } from '@/api/types'
import { plural } from '@/utils/labels'
import { problemLang, problemMessage } from '@/utils/problem'
import InlineError from '../ui/InlineError.vue'
import UiButton from '../ui/UiButton.vue'
import UiDialog from '../ui/UiDialog.vue'

const props = defineProps<{
  open: boolean
  /** What is being deleted. */
  kind: 'team' | 'department'
  name: string
  rules: ScopedRulesProblem['rules']
  /** Repeats the delete with the chosen answer; the dialog closes when it resolves and shows the error when it rejects. */
  action: (choice: RoutingRulesChoice) => Promise<unknown>
}>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const choice = ref<RoutingRulesChoice>('deactivate')
const busy = ref(false)
const error = ref<unknown>(null)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    choice.value = 'deactivate'
    error.value = null
  },
)

const noun = computed(() => (props.kind === 'team' ? 'team' : 'department'))
const confirmLabel = computed(() =>
  choice.value === 'deactivate' ? `Delete ${noun.value}, deactivate ${plural(props.rules.length, 'rule')}` : `Delete ${noun.value} and ${plural(props.rules.length, 'rule')}`,
)

async function confirm(): Promise<void> {
  busy.value = true
  error.value = null
  try {
    await props.action(choice.value)
    emit('update:open', false)
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDialog :open="open" :title="`${plural(rules.length, 'routing rule')} belong${rules.length === 1 ? 's' : ''} to this ${noun}`" icon="rule" tone="warn" @update:open="emit('update:open', $event)">
    <template #description>
      <span lang="sv" class="font-medium">{{ name }}</span> has routing rules. Rules often belong to a service, so if the organisation only changed you can keep them and give them a new {{ noun }} later.
    </template>
    <ul class="m-0 flex list-none flex-col gap-1 p-0">
      <li v-for="rule in rules" :key="rule.id" class="rounded-chip bg-surface-2 px-3 py-1.5">
        <span class="font-medium">{{ rule.name }}</span><span v-if="!rule.isEnabled" class="text-small text-fg-3"> · already disabled</span>
      </li>
    </ul>
    <fieldset class="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
      <legend class="mb-2 p-0 text-heading">What should happen to the rules?</legend>
      <label class="flex cursor-pointer items-start gap-3 rounded-control p-2" :class="choice === 'deactivate' ? 'bg-accent-soft' : 'hover:bg-hover'">
        <input v-model="choice" type="radio" name="scoped-rules-choice" value="deactivate" class="mt-0.5" />
        <span class="flex flex-col"><span class="font-medium">Deactivate them (recommended)</span><span class="text-small text-fg-2">They stay switched off, with everything kept, until you assign a new {{ noun }} under Routing rules.</span></span>
      </label>
      <label class="flex cursor-pointer items-start gap-3 rounded-control p-2" :class="choice === 'delete' ? 'bg-danger-soft' : 'hover:bg-hover'">
        <input v-model="choice" type="radio" name="scoped-rules-choice" value="delete" class="mt-0.5" />
        <span class="flex flex-col"><span class="font-medium">Delete them</span><span class="text-small text-fg-2">They are removed for good.</span></span>
      </label>
    </fieldset>
    <InlineError v-if="error" :message="problemMessage(error)" :lang="problemLang(error)" />
    <template #actions>
      <UiButton @click="emit('update:open', false)">Cancel</UiButton>
      <UiButton :variant="choice === 'delete' ? 'danger-solid' : 'primary'" :loading="busy" @click="confirm">{{ confirmLabel }}</UiButton>
    </template>
  </UiDialog>
</template>
