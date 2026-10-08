<script setup lang="ts">
import { computed } from 'vue'
import AppIcon from './AppIcon.vue'

export type ButtonVariant = 'primary' | 'secondary' | 'quiet' | 'danger' | 'danger-solid'
export type ButtonSize = 'sm' | 'md' | 'lg' | 'xl'

const props = withDefaults(
  defineProps<{
    variant?: ButtonVariant
    size?: ButtonSize
    icon?: string
    type?: 'button' | 'submit' | 'reset'
    /** Stays focusable (aria-disabled) and blocks activation. Explain why next to the button. */
    disabled?: boolean
    iconOnly?: boolean
    block?: boolean
    /** Renders an anchor styled as a button (navigation, not an action). */
    href?: string
    loading?: boolean
  }>(),
  { variant: 'secondary', size: 'md', type: 'button' },
)

const emit = defineEmits<{ (e: 'click', event: MouseEvent): void }>()

const classes = computed(() => [
  'btn',
  {
    primary: 'bg-accent text-accent-fg hover:bg-accent-hover',
    secondary: 'bg-surface text-fg shadow-inset-strong hover:bg-surface-2',
    quiet: 'bg-transparent text-fg hover:bg-hover',
    danger: 'bg-transparent text-danger hover:bg-danger-soft',
    'danger-solid': 'bg-danger text-white hover:opacity-90',
  }[props.variant],
  { sm: 'h-ctl-sm', md: 'h-ctl', lg: 'h-ctl-lg', xl: 'h-ctl-xl text-body-lg' }[props.size],
  props.iconOnly ? 'w-8 !px-0' : '',
  props.size === 'sm' ? '!px-3' : '',
  props.block ? 'w-full' : '',
])

function onClick(event: MouseEvent): void {
  if (props.disabled || props.loading) {
    event.preventDefault()
    return
  }
  emit('click', event)
}
</script>

<template>
  <component
    :is="href ? 'a' : 'button'"
    :type="href ? undefined : type"
    :href="href"
    :class="classes"
    :aria-disabled="disabled || loading ? 'true' : undefined"
    :aria-busy="loading ? 'true' : undefined"
    @click="onClick"
  >
    <AppIcon v-if="icon || loading" :name="loading ? 'progress_activity' : icon!" :size="18" :class="{ 'animate-spin': loading }" />
    <slot />
  </component>
</template>
