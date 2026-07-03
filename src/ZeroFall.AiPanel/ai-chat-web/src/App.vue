<script setup>
import { onMounted, watch } from 'vue'
import {
  attachChatScrollFollow,
  onChatRootScroll,
  requestMessageWindow,
  requestReasoningPayload,
  resetCollapsedScrollLatch,
  state
} from './chat-bridge.js'
import ChatMessageItem from './ChatMessageItem.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import { confirmState } from './confirm-dialog.js'
import { useVirtualMessages } from './useVirtualMessages.js'

const chatScrollRoot = () => document.querySelector('.chat-root')

const {
  visibleIndices,
  topSpacerHeight,
  bottomSpacerHeight,
  scheduleRangeUpdate,
  resetVirtualState,
  measureMessage
} = useVirtualMessages(chatScrollRoot, (from, to) => {
  requestMessageWindow(from, to)
})

onMounted(() => {
  attachChatScrollFollow()
  scheduleRangeUpdate()
})

watch(
  () => state.messages.length,
  () => {
    scheduleRangeUpdate()
  }
)

function onRootScroll(event) {
  onChatRootScroll(event)
  scheduleRangeUpdate()
}

function onSessionReset() {
  resetVirtualState()
  if (!state.initialScrollToEnd)
    scheduleRangeUpdate()
}

watch(
  () => state.sessionEpoch,
  () => onSessionReset()
)

function toggleReasoning(message) {
  message.reasoningExpanded = !message.reasoningExpanded
  resetCollapsedScrollLatch(message, 'reasoning')
  if (typeof invokeCSharpAction === 'function') {
    invokeCSharpAction(JSON.stringify({
      type: 'setReasoningExpanded',
      id: message.id,
      expanded: message.reasoningExpanded
    }))
  }
  if (message.reasoningExpanded && !hasReasoningContent(message)) {
    requestReasoningPayload(message.id)
  }
}

function hasReasoningContent(message) {
  return (message.reasoningBlocks?.length ?? 0) > 0
    || !!(message.reasoningTailMarkdown?.trim())
}

function toggleTool(message) {
  message.toolExpanded = !message.toolExpanded
  resetCollapsedScrollLatch(message, 'tool')
  if (typeof invokeCSharpAction === 'function') {
    invokeCSharpAction(JSON.stringify({
      type: 'setToolExpanded',
      id: message.id,
      expanded: message.toolExpanded
    }))
  }
  if (message.toolExpanded && needsToolPayload(message)) {
    requestToolPayload(message.id)
  }
}

function needsToolPayload(message) {
  return isEmptyToolPayload(message.toolArgumentsJson)
    && isEmptyToolPayload(message.toolResultJson)
}

function isEmptyToolPayload(payload) {
  if (payload == null || payload === '') return true
  if (typeof payload === 'string') return payload.trim() === ''
  return false
}

function requestToolPayload(id) {
  if (!id || typeof invokeCSharpAction !== 'function') return
  invokeCSharpAction(JSON.stringify({ type: 'requestToolPayload', id }))
}
</script>

<template>
  <ConfirmDialog />
  <main class="chat-root" :class="{ 'confirm-blurred': confirmState.visible }" @scroll="onRootScroll">
    <div class="chat-inner">
      <div v-if="state.messages.length === 0" class="empty-state">
        AI 聊天视图已就绪，暂无对话内容
      </div>

      <div
        v-if="topSpacerHeight > 0"
        class="virtual-spacer"
        :style="{ height: `${topSpacerHeight}px` }"
        aria-hidden="true"
      />

      <ChatMessageItem
        v-for="index in visibleIndices"
        :key="state.messages[index].renderKey || state.messages[index].id"
        :message="state.messages[index]"
        :read-only="state.readOnly"
        @toggle-tool="toggleTool"
        @toggle-reasoning="toggleReasoning"
        @measure="measureMessage"
      />

      <div
        v-if="bottomSpacerHeight > 0"
        class="virtual-spacer"
        :style="{ height: `${bottomSpacerHeight}px` }"
        aria-hidden="true"
      />
    </div>
  </main>
</template>
