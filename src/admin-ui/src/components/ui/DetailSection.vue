<script setup lang="ts">
/** A titled section for drawers: caption heading, rows of label / value (`list`), or free content (`:list="false"`). */
withDefaults(defineProps<{ title: string; list?: boolean }>(), { list: true })
</script>

<template>
  <section class="mb-6">
    <h3 class="mb-1 text-caption text-fg-3">{{ title }}</h3>
    <!-- A dl may only hold dt/dd rows; sections with other content use a div. -->
    <component :is="list ? 'dl' : 'div'" class="m-0"><slot /></component>
  </section>
</template>
