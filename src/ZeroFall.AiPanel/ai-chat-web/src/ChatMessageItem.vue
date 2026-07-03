<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { revertMessage, state } from './chat-bridge.js'
import ToolJsonViewer from './ToolJsonViewer.vue'
import WaitingSkeletonCard from './WaitingSkeletonCard.vue'

const props = defineProps({
  message: { type: Object, required: true },
  readOnly: { type: Boolean, default: false }
})

const isSkeletonFadeOverlay = computed(() =>
  state.skeletonFadeOnMessageId === props.message.id
)

const emit = defineEmits(['toggle-tool', 'toggle-reasoning', 'measure'])
const STREAM_PREVIEW_CHARS = 360

const rootEl = ref(null)
const streamPreviewEl = ref(null)
let resizeObserver = null
let heightReportRaf = 0

function reportHeight() {
  const el = rootEl.value
  if (!el) return
  emit('measure', props.message, el.getBoundingClientRect().height)
}

function scheduleHeightReport() {
  if (heightReportRaf)
    return
  heightReportRaf = requestAnimationFrame(() => {
    heightReportRaf = 0
    reportHeight()
  })
}

onMounted(() => {
  reportHeight()
  if (typeof ResizeObserver === 'undefined' || !rootEl.value) return
  resizeObserver = new ResizeObserver(() => scheduleHeightReport())
  resizeObserver.observe(rootEl.value)
})

onUnmounted(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
  if (heightReportRaf) {
    cancelAnimationFrame(heightReportRaf)
    heightReportRaf = 0
  }
})

watch(
  () => [
    props.message.deferred,
    props.message.blocks?.length,
    props.message.reasoningBlocks?.length,
    props.message.toolExpanded,
    props.message.reasoningExpanded,
    props.message.isToolRunning,
    props.message.isStreaming,
    props.message.isThinking,
    props.message.waitingPlaceholder
  ],
  scheduleHeightReport
)

const deferredLabel = computed(() => {
  const message = props.message
  if (message.role === 'user') return message.content || '用户消息'
  if (message.role === 'tool') return message.title || message.command || '工具调用'
  return '助手回复'
})

function onToggleTool() {
  emit('toggle-tool', props.message)
}

function onToggleReasoning() {
  emit('toggle-reasoning', props.message)
}

/** SSE 流式原文：仅字符串直通，不做 JSON/末行解析。 */
function rawStreamText(value) {
  if (value == null || value === '') return ''
  return typeof value === 'string' ? value : ''
}

function streamTailText(value) {
  const text = rawStreamText(value)
  if (!text) return ''
  if (text.length <= STREAM_PREVIEW_CHARS) return text
  return '…' + text.slice(-STREAM_PREVIEW_CHARS)
}

function scrollPreviewToEnd() {
  nextTick(() => {
    const el = streamPreviewEl.value
    if (!el) return
    el.scrollLeft = el.scrollWidth
  })
}

function subAgentPreview(message) {
  const payload = message.toolResultJson
  if (!payload || typeof payload !== 'object' || payload.preview !== true) return null
  return Array.isArray(payload.recent_messages) ? payload : null
}

function toolHeaderPreview(message) {
  if (!message.isToolRunning) return ''
  return streamTailText(message.command) || streamTailText(message.toolArgumentsJson)
}

function reasoningHeaderPreview(message) {
  if (!message.isStreaming && !message.isThinking) return ''
  return streamTailText(message.reasoningTailMarkdown)
}

watch(
  () => [
    toolHeaderPreview(props.message),
    reasoningHeaderPreview(props.message),
    props.message.isToolRunning,
    props.message.isStreaming,
    props.message.isThinking
  ],
  scrollPreviewToEnd,
  { flush: 'post' }
)

function toolStatusText(message) {
  if (message.isToolRunning) return '执行中'
  return Number(message.toolExitCode ?? 0) === 0 ? '完成' : '失败'
}

function toolStatusClass(message) {
  if (message.isToolRunning) return 'status-running'
  return Number(message.toolExitCode ?? 0) === 0 ? 'status-success' : 'status-error'
}

function subAgentToolStatus(item) {
  if (item.running) return '执行中'
  return Number(item.exitCode ?? 0) === 0 ? '完成' : '失败'
}

function subAgentToolStatusClass(item) {
  if (item.running) return 'status-running'
  return Number(item.exitCode ?? 0) === 0 ? 'status-success' : 'status-error'
}
</script>

<template>
  <article
    ref="rootEl"
    class="message"
    :class="[
      'role-' + message.role,
      {
        streaming: message.isStreaming,
        deferred: message.deferred,
        'has-skeleton-overlay': isSkeletonFadeOverlay
      }
    ]"
  >
    <WaitingSkeletonCard v-if="message.waitingPlaceholder" />

    <div v-else-if="message.deferred" class="message-deferred" :class="'role-' + message.role">
      <span class="message-deferred-label">{{ deferredLabel }}</span>
    </div>

    <template v-else>
      <div v-if="message.role === 'user'" class="user-row">
        <button
          v-if="!readOnly"
          type="button"
          class="revert-btn"
          title="撤销此条及之后的对话"
          @click="revertMessage(message)"
        >
          撤销
        </button>
        <div class="bubble user-bubble">{{ message.content }}</div>
      </div>

      <div v-else-if="message.role === 'tool'" class="tool-card">
        <button type="button" class="tool-card-header" @click="onToggleTool">
          <span class="tool-chevron" :class="{ expanded: message.toolExpanded }">›</span>
          <span class="tool-card-title">{{ message.title || message.command || '工具调用' }}</span>
          <span v-if="toolHeaderPreview(message)" ref="streamPreviewEl" class="tool-card-preview">
            {{ toolHeaderPreview(message) }}
          </span>
          <span class="tool-card-status" :class="toolStatusClass(message)">
            {{ toolStatusText(message) }}
          </span>
        </button>
        <div
          class="tool-card-body collapse-panel"
          :class="{ expanded: message.toolExpanded, collapsed: !message.toolExpanded }"
          :aria-hidden="!message.toolExpanded"
        >
          <div v-if="message.toolExpanded" class="collapse-panel-inner">
            <div v-if="subAgentPreview(message)" class="subagent-panel">
              <div class="subagent-panel-meta">
                <span>{{ subAgentPreview(message).title || '子 Agent' }}</span>
                <span v-if="subAgentPreview(message).task" class="subagent-panel-task">
                  {{ subAgentPreview(message).task }}
                </span>
              </div>
              <div class="subagent-message-list">
                <div
                  v-for="(item, idx) in subAgentPreview(message).recent_messages"
                  :key="idx"
                  class="subagent-message"
                  :class="'subagent-role-' + item.role"
                >
                  <div v-if="item.role === 'user'" class="subagent-user-bubble">
                    {{ item.content }}
                  </div>

                  <div v-else-if="item.role === 'tool'" class="subagent-tool-card">
                    <div class="subagent-tool-header">
                      <span class="subagent-tool-title">{{ item.name || '工具调用' }}</span>
                      <span class="tool-card-status" :class="subAgentToolStatusClass(item)">
                        {{ subAgentToolStatus(item) }}
                      </span>
                    </div>
                    <div v-if="item.command" class="subagent-tool-command">{{ item.command }}</div>
                    <pre v-if="item.output" class="subagent-tool-output">{{ item.output }}</pre>
                  </div>

                  <div v-else class="subagent-assistant-body">
                    <div v-if="item.reasoning" class="subagent-reasoning">
                      <div class="subagent-reasoning-title">
                        {{ item.thinking ? '思考中' : '思考内容' }}
                      </div>
                      <div class="subagent-reasoning-text">{{ item.reasoning }}</div>
                    </div>
                    <div v-if="item.content" class="subagent-assistant-text">{{ item.content }}</div>
                    <div v-if="item.streaming" class="subagent-streaming-dot">流式输出中…</div>
                  </div>
                </div>
              </div>
            </div>
            <template v-else>
              <ToolJsonViewer
                :payload="message.toolArgumentsJson"
                label="调用参数"
                :streaming="message.isToolRunning"
              />
              <ToolJsonViewer
                :payload="message.toolResultJson"
                label="返回结果"
                :streaming="message.isToolRunning"
              />
            </template>
          </div>
        </div>
      </div>

      <div v-else class="assistant-body">
        <div v-if="message.hasReasoning" class="tool-card reasoning-panel">
          <button type="button" class="tool-card-header reasoning-toggle" @click="onToggleReasoning">
            <span class="tool-chevron reasoning-chevron" :class="{ expanded: message.reasoningExpanded }">›</span>
            <span class="tool-card-title">{{ message.thinkingLabel || '思考内容' }}</span>
            <span v-if="reasoningHeaderPreview(message)" ref="streamPreviewEl" class="tool-card-preview">
              {{ reasoningHeaderPreview(message) }}
            </span>
            <span v-if="message.isThinking" class="tool-card-status status-running">思考中</span>
          </button>
          <div
            class="tool-card-body reasoning-body collapse-panel"
            :class="{
              expanded: message.reasoningExpanded,
              collapsed: !message.reasoningExpanded
            }"
            :aria-hidden="!message.reasoningExpanded"
          >
            <div v-if="message.reasoningExpanded" class="collapse-panel-inner">
              <div
                v-for="block in message.reasoningBlocks"
                v-show="!message.isThinking"
                :key="block.id"
                class="markdown-block reasoning-block"
                v-html="block.html"
              />
              <div v-if="message.reasoningTailMarkdown" class="stream-tail stream-tail-reasoning">
                {{ message.reasoningTailMarkdown }}
              </div>
            </div>
          </div>
        </div>
        <div
          v-if="message.blocks?.length || message.tailMarkdown"
          class="assistant-response-card"
        >
          <div class="assistant-response-inner">
            <div v-for="block in message.blocks" :key="block.id" class="markdown-block" v-html="block.html" />
            <div v-if="message.tailMarkdown" class="stream-tail">{{ message.tailMarkdown }}</div>
          </div>
        </div>
      </div>
    </template>

    <div
      v-if="isSkeletonFadeOverlay"
      class="message-skeleton-overlay"
      :class="{ 'is-fading': state.waitingOverlayFading }"
      aria-hidden="true"
    >
      <WaitingSkeletonCard />
    </div>
  </article>
</template>
