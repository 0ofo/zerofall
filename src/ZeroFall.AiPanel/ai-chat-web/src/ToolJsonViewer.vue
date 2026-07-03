<script setup>
import { computed, ref } from 'vue'
import { JsonViewer } from 'vue3-json-viewer'
import { state } from './chat-bridge.js'
import { coerceJsonPayload } from './json-text.js'

const props = defineProps({
  text: { type: String, default: '' },
  payload: { type: null, default: null },
  label: { type: String, default: '' },
  streaming: { type: Boolean, default: false },
  tailLines: { type: Number, default: 0 }
})

const TREE_RENDER_LIMIT = 120_000
const PREVIEW_LIMIT = 8_000

const rawText = computed(() => {
  const value = props.payload != null && props.payload !== '' ? props.payload : props.text
  if (value == null) return ''
  if (typeof value === 'string') return value
  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value ?? '')
  }
})
const parsed = computed(() => {
  if (props.streaming) return { ok: false, text: rawText.value }
  if (props.payload != null && props.payload !== '')
    return coerceJsonPayload(props.payload)
  return coerceJsonPayload(props.text)
})
const theme = computed(() => (state.theme === 'dark' ? 'dark' : 'light'))
const hasContent = computed(() => {
  if (props.payload != null && props.payload !== '') return true
  return !!String(props.text ?? '').trim()
})
const expanded = ref(false)
const fullText = computed(() => {
  if (!hasContent.value) return ''
  if (props.streaming) return rawText.value
  if (!parsed.value.ok) return parsed.value.text ?? ''
  try {
    return JSON.stringify(parsed.value.value, null, 2)
  } catch {
    return String(parsed.value.value ?? '')
  }
})
const isLong = computed(() => fullText.value.length > PREVIEW_LIMIT || fullText.value.split('\n').length > 120)
const canRenderTree = computed(() => parsed.value.ok && fullText.value.length <= TREE_RENDER_LIMIT)
const useTailPreview = computed(() => props.streaming && props.tailLines > 0)
const tailPreviewText = computed(() => {
  const normalized = fullText.value.replace(/\r\n/g, '\n').replace(/\r/g, '\n')
  const lines = normalized.split('\n')
  return lines.slice(-props.tailLines).join('\n')
})
const displayText = computed(() => {
  if (useTailPreview.value) return tailPreviewText.value
  if (props.streaming) return fullText.value
  if (expanded.value || !isLong.value) return fullText.value
  return fullText.value.slice(0, PREVIEW_LIMIT) + '\n...（已截断，点击展开全文）'
})
</script>

<template>
  <div v-if="hasContent" class="tool-json-section">
    <div class="tool-json-header">
      <div v-if="label" class="tool-json-label">{{ label }}</div>
      <div v-if="useTailPreview" class="tool-json-hint">流式预览：末尾 {{ tailLines }} 行</div>
      <button v-else-if="isLong" type="button" class="tool-json-expand" @click="expanded = !expanded">
        {{ expanded ? '收起预览' : '展开全文' }}
      </button>
    </div>
    <JsonViewer
      v-if="!useTailPreview && canRenderTree && (!isLong || expanded)"
      :value="parsed.value"
      copyable
      boxed
      sort
      :theme="theme"
      class="tool-json-viewer"
    />
    <pre v-else class="tool-result-raw" :class="{ 'streaming-preview': useTailPreview }">{{ displayText }}</pre>
  </div>
</template>
