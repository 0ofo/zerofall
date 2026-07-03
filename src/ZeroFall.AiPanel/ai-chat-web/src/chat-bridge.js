import { nextTick, reactive } from 'vue'
import { openConfirm } from './confirm-dialog.js'
export const state = reactive({
  messages: [],
  autoFollow: true,
  initialScrollToEnd: true,
  waitingReply: false,
  waitingOverlayVisible: false,
  waitingOverlayFading: false,
  /** SSE 到达后，骨架 overlay 悬浮在该消息 id 上方淡出。 */
  skeletonFadeOnMessageId: null,
  readOnly: false,
  theme: document.documentElement.classList.contains('dark') ? 'dark' : 'light',
  sessionEpoch: 0
})

function findMessage(id) {
  return state.messages.find(m => m.id === id)
}

function normalizeMessage(message) {
  const normalized = {
    seq: Number.MAX_SAFE_INTEGER,
    blocks: [],
    tailMarkdown: '',
    reasoningBlocks: [],
    reasoningTailMarkdown: '',
    hasReasoning: false,
    isThinking: false,
    reasoningExpanded: false,
    toolExpanded: false,
    thinkingLabel: '思考内容',
    toolArgumentsJson: '',
    toolResultJson: '',
    toolExitCode: 0,
    deferred: false,
    waitingPlaceholder: false,
    ...message
  }
  normalized.renderKey ||= normalized.id
  return normalized
}

function sortMessagesBySeq() {
  state.messages.sort((a, b) => {
    const aSeq = Number.isFinite(a.seq) && a.seq >= 0 ? a.seq : Number.MAX_SAFE_INTEGER
    const bSeq = Number.isFinite(b.seq) && b.seq >= 0 ? b.seq : Number.MAX_SAFE_INTEGER
    if (aSeq !== bSeq) return aSeq - bSeq
    return 0
  })
}

function dedupeMessagesById(messages) {
  const seen = new Set()
  const result = []
  for (const msg of messages) {
    const id = msg?.id
    if (id != null && id !== '' && id !== WAITING_SKELETON_ID) {
      if (seen.has(id)) continue
      seen.add(id)
    }
    result.push(msg)
  }
  return result
}

let waitingOverlayFadeTimer = 0
const WAITING_OVERLAY_FADE_MS = 1000
const WAITING_SKELETON_ID = '__waiting_skeleton__'

function findWaitingPlaceholder() {
  return state.messages.find(m => m.waitingPlaceholder && m.id === WAITING_SKELETON_ID)
}

function ensureWaitingPlaceholder() {
  if (findWaitingPlaceholder()) return
  state.messages.push(normalizeMessage({
    id: WAITING_SKELETON_ID,
    renderKey: WAITING_SKELETON_ID,
    role: 'assistant',
    waitingPlaceholder: true,
    seq: Number.MAX_SAFE_INTEGER
  }))
  sortMessagesBySeq()
}

function removeWaitingPlaceholder() {
  const idx = state.messages.findIndex(m => m.waitingPlaceholder && m.id === WAITING_SKELETON_ID)
  if (idx >= 0)
    state.messages.splice(idx, 1)
}

function showWaitingOverlay() {
  clearTimeout(waitingOverlayFadeTimer)
  state.waitingOverlayFading = false
  state.skeletonFadeOnMessageId = null
  state.waitingOverlayVisible = true
  ensureWaitingPlaceholder()
}

function hideWaitingOverlayImmediate() {
  clearTimeout(waitingOverlayFadeTimer)
  state.waitingOverlayFading = false
  state.waitingOverlayVisible = false
  state.skeletonFadeOnMessageId = null
  removeWaitingPlaceholder()
}

/** SSE 消息已写入 DOM 后，在原占位处上方 1s 淡出。 */
function scheduleWaitingOverlayDismiss() {
  if (!state.skeletonFadeOnMessageId) return
  clearTimeout(waitingOverlayFadeTimer)
  state.waitingOverlayFading = false
  void nextTick(() => {
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        if (!state.skeletonFadeOnMessageId) return
        state.waitingOverlayFading = true
        waitingOverlayFadeTimer = setTimeout(() => {
          state.waitingOverlayFading = false
          state.skeletonFadeOnMessageId = null
        }, WAITING_OVERLAY_FADE_MS)
      })
    })
  })
}

function isResponseMessage(message) {
  return !!message
    && message.role !== 'user'
    && !message.waitingPlaceholder
}

/** 骨架淡出已开始或无需等待时，跳过后续 dismiss 检查（避免 appendStreamTail 热路径开销）。 */
function isWaitingSkeletonPending() {
  return !!findWaitingPlaceholder() || state.waitingReply || state.waitingOverlayVisible
}

/** 下一帧 SSE 消息壳或正文已 upsert/append 到 messages 后，在原位上方淡出骨架。 */
function dismissWaitingWhenSseMessagePlaced(message, { shellOnly = false } = {}) {
  if (state.skeletonFadeOnMessageId)
    return
  if (!isWaitingSkeletonPending())
    return
  if (!isResponseMessage(message)) return

  const hasPayload =
    (message.blocks?.length ?? 0) > 0
    || !!(message.tailMarkdown?.trim())
    || (message.reasoningBlocks?.length ?? 0) > 0
    || !!(message.reasoningTailMarkdown?.trim())
    || !!message.toolArgumentsJson
    || !!message.toolResultJson
    || !!message.title
    || !!message.command
    || message.isStreaming
    || message.isThinking
    || message.isToolRunning

  if (!shellOnly && !hasPayload) return

  removeWaitingPlaceholder()
  state.waitingReply = false
  state.waitingOverlayVisible = false
  state.skeletonFadeOnMessageId = message.id
  scheduleWaitingOverlayDismiss()
}

function lastConversationMessage() {
  for (let i = state.messages.length - 1; i >= 0; i--) {
    const message = state.messages[i]
    if (message && !message.waitingPlaceholder) return message
  }
  return null
}

/** 用户切换思考/工具面板折叠态时重置，展开后恢复 SSE 跟滚；折叠后下次仅首帧跟滚一次。 */
export function resetCollapsedScrollLatch(message, panel) {
  if (!message) return
  if (panel === 'reasoning') delete message._scrollLatchReasoningCollapsed
  if (panel === 'tool') delete message._scrollLatchToolCollapsed
}

function shouldSkipCollapsedPanelAutoScroll(message, updateKind) {
  if (!message || updateKind === 'main' || updateKind === 'general') return false

  if (updateKind === 'reasoning') {
    if (message.reasoningExpanded) {
      delete message._scrollLatchReasoningCollapsed
      return false
    }
    if (message._scrollLatchReasoningCollapsed) return true
    message._scrollLatchReasoningCollapsed = true
    return false
  }

  if (updateKind === 'tool') {
    if (message.toolExpanded) {
      delete message._scrollLatchToolCollapsed
      return false
    }
    if (message._scrollLatchToolCollapsed) return true
    message._scrollLatchToolCollapsed = true
    return false
  }

  return false
}

function inferPatchUpdateKind(command) {
  if (command.reasoningBlocks?.length || command.reasoningTailMarkdown !== undefined
    || command.isThinking !== undefined || command.hasReasoning !== undefined
    || command.reasoningExpanded !== undefined || command.thinkingLabel !== undefined)
    return 'reasoning'
  if (command.isToolRunning !== undefined || command.toolResultJson !== undefined
    || command.toolArgumentsJson !== undefined || command.command !== undefined
    || command.title !== undefined || command.toolExitCode !== undefined
    || command.toolExpanded !== undefined)
    return 'tool'
  return 'main'
}

function inferUpsertUpdateKind(message) {
  if (message.role === 'tool') return 'tool'
  if (message.hasReasoning && (message.isThinking || message.reasoningTailMarkdown))
    return 'reasoning'
  return 'main'
}

function shouldShowWaitingOverlay() {
  const last = lastConversationMessage()
  if (!last) return false
  if (last.role === 'user') return true
  return last.role === 'tool'
    && !last.isToolRunning
    && (
      !isEmptyToolPayload(last.toolResultJson)
      || Number.isFinite(Number(last.toolExitCode))
    )
}

function syncWaitingOverlayVisibility() {
  if (state.waitingReply && shouldShowWaitingOverlay())
    showWaitingOverlay()
  else if (!state.waitingOverlayFading && !state.skeletonFadeOnMessageId)
    hideWaitingOverlayImmediate()
}

function applyTheme(theme) {
  const dark = theme === 'dark'
  state.theme = dark ? 'dark' : 'light'
  document.documentElement.classList.toggle('dark', dark)
}

function mergeMessage(existing, incoming) {
  const merged = normalizeMessage({ ...existing, ...incoming })
  if (!incoming.blocks?.length)
    merged.blocks = existing.blocks ?? []
  else
    merged.tailMarkdown = incoming.tailMarkdown ?? ''
  if (!incoming.reasoningBlocks?.length)
    merged.reasoningBlocks = existing.reasoningBlocks ?? []
  else
    merged.reasoningTailMarkdown = incoming.reasoningTailMarkdown ?? ''
  if (!incoming.blocks?.length && !incoming.tailMarkdown)
    merged.tailMarkdown = existing.tailMarkdown ?? ''
  if (!incoming.reasoningBlocks?.length && !incoming.reasoningTailMarkdown)
    merged.reasoningTailMarkdown = existing.reasoningTailMarkdown ?? ''
  if (isEmptyToolPayload(incoming.toolResultJson) && !isEmptyToolPayload(existing.toolResultJson))
    merged.toolResultJson = existing.toolResultJson
  if (isEmptyToolPayload(incoming.toolArgumentsJson) && !isEmptyToolPayload(existing.toolArgumentsJson))
    merged.toolArgumentsJson = existing.toolArgumentsJson
  if (incoming.toolExpanded === false && existing.toolExpanded)
    merged.toolExpanded = true
  if (incoming.reasoningExpanded === false && existing.reasoningExpanded)
    merged.reasoningExpanded = true
  return merged
}

function isEmptyToolPayload(payload) {
  if (payload == null || payload === '') return true
  if (typeof payload === 'string') return payload.trim() === ''
  return false
}

let suppressAutoFollowReset = false
let scrollIntentAttached = false
let userPinnedAwayFromBottom = false

const NEAR_BOTTOM_THRESHOLD_PX = 160
const BOTTOM_NO_SCROLL_THRESHOLD_PX = 8

function getChatRoot() {
  return document.querySelector('.chat-root')
}

function maxScrollTop(root) {
  return Math.max(0, root.scrollHeight - root.clientHeight)
}

function bottomDistance(root) {
  if (!root) return 0
  return Math.max(0, maxScrollTop(root) - root.scrollTop)
}

function isAtBottom(root = getChatRoot()) {
  if (!root) return true
  return bottomDistance(root) <= BOTTOM_NO_SCROLL_THRESHOLD_PX
}

function isNearBottom(root, threshold = 80) {
  if (!root) return true
  return bottomDistance(root) <= threshold
}

function isActivelyStreaming() {
  if (state.waitingReply || state.waitingOverlayVisible)
    return true

  const messages = state.messages
  for (let i = messages.length - 1; i >= 0; i--) {
    const m = messages[i]
    if (m.waitingPlaceholder)
      continue
    if (m.isStreaming || m.isToolRunning || m.isThinking)
      return true
    if (m.role === 'user')
      break
  }

  return false
}

export function scrollToBottom() {
  const root = getChatRoot()
  if (!root || isAtBottom(root)) return
  userPinnedAwayFromBottom = false
  state.autoFollow = true
  markProgrammaticScroll()
  root.scrollTop = maxScrollTop(root)
}

function markProgrammaticScroll() {
  suppressAutoFollowReset = true
  requestAnimationFrame(() => {
    suppressAutoFollowReset = false
  })
}

let scrollBottomRafId = 0

/** 仅在 UI 变更命令后调用：已贴底 8px 内或无跟随意愿时不滚动；rAF 合并高频 appendStreamTail。 */
function maybeScrollToBottom(options = {}) {
  if (options.forceFollow) {
    userPinnedAwayFromBottom = false
    state.autoFollow = true
  }
  if (!state.autoFollow || userPinnedAwayFromBottom) return
  if (!options.forceFollow && shouldSkipCollapsedPanelAutoScroll(options.message, options.updateKind ?? 'general'))
    return

  if (scrollBottomRafId)
    return

  scrollBottomRafId = requestAnimationFrame(() => {
    scrollBottomRafId = 0
    const root = getChatRoot()
    if (!root || !state.autoFollow || userPinnedAwayFromBottom) return
    if (isAtBottom(root)) return
    markProgrammaticScroll()
    root.scrollTop = maxScrollTop(root)
  })
}

export function attachChatScrollFollow() {
  const root = getChatRoot()
  if (!scrollIntentAttached && root) {
    root.addEventListener('wheel', onTailScrollUserIntent, { passive: true })
    root.addEventListener('touchstart', onTailScrollUserIntent, { passive: true })
    root.addEventListener('keydown', onTailScrollUserIntent)
    scrollIntentAttached = true
  }
}

/** 会话打开/历史恢复：用 C# 下发的完整 JSON 直接覆盖 messages，不做 merge。 */
export function initSessionWindowFromJson(messages, options = {}) {
  state.sessionEpoch += 1
  state.messages = dedupeMessagesById(
    (messages || [])
      .map(normalizeMessage)
      .filter(m => !m.waitingPlaceholder)
  )
  state.initialScrollToEnd = options.scrollToEnd !== false
  if (options.waiting !== undefined) state.waitingReply = !!options.waiting
  if (options.readOnly !== undefined) state.readOnly = !!options.readOnly
  if (options.theme) applyTheme(options.theme)
  hideWaitingOverlayImmediate()
  if (state.waitingReply) syncWaitingOverlayVisibility()

  attachChatScrollFollow()

  const root = getChatRoot()
  if (root && options.scrollToEnd === false)
    root.scrollTop = 0

  if (options.scrollToEnd !== false)
    maybeScrollToBottom({ forceFollow: true })
}

function applyHydratedPayload(existing, item) {
  if (item.content !== undefined && item.content !== '')
    existing.content = item.content
  if (item.title !== undefined && item.title !== '')
    existing.title = item.title
  if (item.command !== undefined && item.command !== '')
    existing.command = item.command

  if (item.blocks?.length) {
    existing.blocks = item.blocks
    existing.tailMarkdown = item.tailMarkdown ?? ''
  } else if (item.tailMarkdown !== undefined) {
    existing.tailMarkdown = item.tailMarkdown
  }

  if (item.reasoningBlocks?.length) {
    existing.reasoningBlocks = item.reasoningBlocks
    existing.reasoningTailMarkdown = item.reasoningTailMarkdown ?? ''
  } else if (item.reasoningTailMarkdown !== undefined) {
    existing.reasoningTailMarkdown = item.reasoningTailMarkdown
  }

  if (item.toolArgumentsJson !== undefined)
    existing.toolArgumentsJson = item.toolArgumentsJson
  if (item.toolResultJson !== undefined)
    existing.toolResultJson = item.toolResultJson
  if (item.toolExitCode !== undefined)
    existing.toolExitCode = item.toolExitCode
}

function hydrateMessagesFromJson(messages) {
  const incoming = (messages || []).map(normalizeMessage)
  if (incoming.length === 0) return

  for (const item of incoming) {
    const existing = findMessage(item.id)
    if (existing) {
      Object.assign(existing, mergeMessage(existing, item))
      existing.deferred = false
      applyHydratedPayload(existing, item)
    } else {
      item.deferred = false
      state.messages.push(item)
    }
  }
}

let hydrateRequestTimer = 0
export function requestReasoningPayload(id) {
  if (!id || typeof invokeCSharpAction !== 'function') return
  invokeCSharpAction(JSON.stringify({ type: 'requestReasoningPayload', id }))
}

export function requestMessageWindow(from, to) {
  if (typeof invokeCSharpAction !== 'function') return
  clearTimeout(hydrateRequestTimer)
  hydrateRequestTimer = setTimeout(() => {
    invokeCSharpAction(JSON.stringify({ type: 'requestMessageWindow', from, to }))
  }, 48)
}

function appendMessagesFromJson(messages, options = {}) {
  const incoming = (messages || []).map(normalizeMessage)
  if (incoming.length === 0) return
  state.messages.push(...incoming)
  sortMessagesBySeq()
  attachChatScrollFollow()
  if (options.scrollToEnd)
    maybeScrollToBottom({ forceFollow: true })
}

function truncateMessagesFrom(id, options = {}) {
  if (!id) return
  const index = state.messages.findIndex(m => m.id === id)
  if (index < 0) return

  hideWaitingOverlayImmediate()
  state.messages.splice(index)
  state.waitingReply = false
  attachChatScrollFollow()
  if (options.scrollToEnd) {
    userPinnedAwayFromBottom = false
    state.autoFollow = true
    maybeScrollToBottom()
  }
}

/** 用户消息撤销：非阻塞确认框，确认后通知 C# 删除此条及之后的消息。 */
export async function revertMessage(message) {
  const id = message?.id
  if (!id || state.readOnly) return

  const ok = await openConfirm({
    title: '撤销对话',
    message:
      '确定撤销此条消息及之后的全部对话？\n此操作不可恢复；若 AI 正在输出将先停止。',
    confirmText: '撤销',
    cancelText: '取消',
    danger: true
  })
  if (!ok) return
  if (typeof invokeCSharpAction !== 'function') return
  invokeCSharpAction(JSON.stringify({ type: 'revertMessage', id }))
}

/** 供 App.vue 绑定：用户手动滚动时更新跟随状态。 */
export function onChatRootScroll(event) {
  if (suppressAutoFollowReset) return
  const el = event.target
  const nearBottom = isNearBottom(el, NEAR_BOTTOM_THRESHOLD_PX)
  if (nearBottom) {
    userPinnedAwayFromBottom = false
    state.autoFollow = true
  } else if (!isActivelyStreaming()) {
    state.autoFollow = false
  }
}

function onTailScrollUserIntent(event) {
  const root = getChatRoot()
  if (!root) return

  if (event.type === 'wheel') {
    if (event.deltaY >= 0) return
  } else if (event.type === 'keydown') {
    if (!['ArrowUp', 'PageUp', 'Home'].includes(event.key) && !(event.key === ' ' && event.shiftKey)) return
  } else if (isNearBottom(root, NEAR_BOTTOM_THRESHOLD_PX)) {
    return
  }

  state.autoFollow = false
  userPinnedAwayFromBottom = true
}

export function applyCommand(command) {
  if (!command || !command.type) return

  if (command.type === 'batch') {
    for (const item of command.commands || []) applyCommand(item)
    return
  }

  switch (command.type) {
    case 'initSessionWindow':
      initSessionWindowFromJson(command.messages, {
        waiting: command.waiting,
        readOnly: command.readOnly,
        scrollToEnd: command.scrollToEnd !== false
      })
      break
    case 'hydrateMessages':
      hydrateMessagesFromJson(command.messages)
      break
    case 'appendMessages':
      appendMessagesFromJson(command.messages, {
        scrollToEnd: !!command.scrollToEnd
      })
      break
    case 'truncateMessagesFrom':
      truncateMessagesFrom(command.id, {
        scrollToEnd: command.scrollToEnd !== false
      })
      break
    case 'remapMessageId': {
      const fromId = command.fromId
      const toId = command.toId
      if (!fromId || !toId || fromId === toId) break
      const existing = findMessage(fromId)
      if (!existing) break
      if (findMessage(toId)) break
      existing.id = toId
      existing.renderKey = toId
      break
    }
    case 'setMessages': {
      const incoming = (command.messages || []).map(normalizeMessage)
      const existingById = new Map(state.messages.map(m => [m.id, m]))
      state.messages = incoming.map(msg => {
        const existing = existingById.get(msg.id)
        return existing ? mergeMessage(existing, msg) : msg
      })
      maybeScrollToBottom()
      break
    }
    case 'upsertMessage': {
      let target
      const existing = findMessage(command.message.id)
      if (existing) {
        const prevToolExpanded = existing.toolExpanded
        const prevReasoningExpanded = existing.reasoningExpanded
        const incoming = normalizeMessage(command.message)
        Object.assign(existing, mergeMessage(existing, command.message))
        existing.deferred = false
        applyHydratedPayload(existing, incoming)
        if (incoming.toolArgumentsJson !== undefined)
          existing.toolArgumentsJson = incoming.toolArgumentsJson
        if (incoming.toolResultJson !== undefined)
          existing.toolResultJson = incoming.toolResultJson
        if (command.message?.toolExpanded === undefined)
          existing.toolExpanded = prevToolExpanded
        if (command.message?.reasoningExpanded === undefined)
          existing.reasoningExpanded = prevReasoningExpanded
        dismissWaitingWhenSseMessagePlaced(existing, { shellOnly: true })
        target = existing
      }
      else {
        const created = normalizeMessage(command.message)
        created.deferred = false
        state.messages.push(created)
        dismissWaitingWhenSseMessagePlaced(created, { shellOnly: true })
        sortMessagesBySeq()
        target = created
      }
      maybeScrollToBottom({ message: target, updateKind: inferUpsertUpdateKind(target) })
      break
    }
    case 'replaceBlocks': {
      const msg = findMessage(command.messageId)
      if (!msg) return
      msg.deferred = false
      const blocks = command.blocks ?? []
      if (command.reasoning) {
        msg.reasoningBlocks = blocks
        if (blocks.length > 0)
          msg.reasoningTailMarkdown = ''
      }
      else {
        msg.blocks = blocks
        if (blocks.length > 0)
          msg.tailMarkdown = ''
      }
      dismissWaitingWhenSseMessagePlaced(msg)
      maybeScrollToBottom({
        message: msg,
        updateKind: command.reasoning ? 'reasoning' : 'main'
      })
      break
    }
    case 'setStreamTail': {
      const msg = findMessage(command.messageId)
      if (!msg) return
      msg.deferred = false
      const markdown = command.markdown ?? ''
      if (command.reasoning) msg.reasoningTailMarkdown = markdown
      else msg.tailMarkdown = markdown
      maybeScrollToBottom({
        message: msg,
        updateKind: command.reasoning ? 'reasoning' : 'main'
      })
      break
    }
    case 'appendStreamTail': {
      const msg = findMessage(command.messageId)
      if (!msg) return
      msg.deferred = false
      const delta = command.delta ?? ''
      if (command.reasoning) msg.reasoningTailMarkdown = (msg.reasoningTailMarkdown ?? '') + delta
      else msg.tailMarkdown = (msg.tailMarkdown ?? '') + delta
      maybeScrollToBottom({
        message: msg,
        updateKind: command.reasoning ? 'reasoning' : 'main'
      })
      break
    }
    case 'patchMessage': {
      const msg = findMessage(command.id)
      if (!msg) return
      const prevToolExpanded = msg.toolExpanded
      const prevReasoningExpanded = msg.reasoningExpanded
      if (command.hasReasoning !== undefined) msg.hasReasoning = command.hasReasoning
      if (command.isThinking !== undefined) msg.isThinking = command.isThinking
      if (command.isStreaming !== undefined) msg.isStreaming = command.isStreaming
      if (command.reasoningExpanded !== undefined) msg.reasoningExpanded = command.reasoningExpanded
      if (command.title !== undefined) msg.title = command.title
      if (command.command !== undefined) msg.command = command.command
      if (command.isToolRunning !== undefined) msg.isToolRunning = command.isToolRunning
      if (command.thinkingLabel !== undefined) msg.thinkingLabel = command.thinkingLabel
      if (command.toolArgumentsJson !== undefined)
        msg.toolArgumentsJson = command.toolArgumentsJson
      if (command.toolResultJson !== undefined)
        msg.toolResultJson = command.toolResultJson
      if (command.toolExitCode !== undefined)
        msg.toolExitCode = command.toolExitCode
      if (command.toolExpanded !== undefined)
        msg.toolExpanded = command.toolExpanded
      else if (
        command.toolArgumentsJson !== undefined
        || command.toolResultJson !== undefined
        || command.isToolRunning !== undefined
        || command.command !== undefined
        || command.title !== undefined
      )
        msg.toolExpanded = prevToolExpanded
      else if (
        command.hasReasoning !== undefined
        || command.isThinking !== undefined
        || command.thinkingLabel !== undefined
        || command.reasoningTailMarkdown !== undefined
        || command.reasoningBlocks?.length
      )
        msg.reasoningExpanded = prevReasoningExpanded
      if (command.blocks?.length) {
        msg.blocks = command.blocks
        msg.tailMarkdown = command.tailMarkdown ?? ''
      } else if (command.tailMarkdown !== undefined) {
        msg.tailMarkdown = command.tailMarkdown
      }
      if (command.reasoningBlocks?.length) {
        msg.reasoningBlocks = command.reasoningBlocks
        msg.reasoningTailMarkdown = command.reasoningTailMarkdown ?? ''
      } else if (command.reasoningTailMarkdown !== undefined) {
        msg.reasoningTailMarkdown = command.reasoningTailMarkdown
      }
      if (command.blocks?.length || command.tailMarkdown || command.reasoningBlocks?.length
        || command.reasoningTailMarkdown) {
        dismissWaitingWhenSseMessagePlaced(msg)
      }
      if (command.isStreaming || command.isThinking || command.isToolRunning) {
        maybeScrollToBottom({
          message: msg,
          updateKind: inferPatchUpdateKind(command)
        })
      }
      break
    }
    case 'appendBlocks': {
      const msg = findMessage(command.messageId)
      if (!msg) return
      msg.blocks.push(...command.blocks)
      maybeScrollToBottom({ message: msg, updateKind: 'main' })
      break
    }
    case 'setRawTail': {
      const msg = findMessage(command.messageId)
      if (!msg) return
      msg.tailMarkdown = command.markdown ?? ''
      maybeScrollToBottom({ message: msg, updateKind: 'main' })
      break
    }
    case 'appendReasoningBlocks': {
      const msg = findMessage(command.messageId)
      if (!msg) return
      msg.reasoningBlocks.push(...command.blocks)
      maybeScrollToBottom({ message: msg, updateKind: 'reasoning' })
      break
    }
    case 'setReasoningRawTail': {
      const msg = findMessage(command.messageId)
      if (!msg) return
      msg.reasoningTailMarkdown = command.markdown ?? ''
      maybeScrollToBottom({ message: msg, updateKind: 'reasoning' })
      break
    }
    case 'setWaiting':
      state.waitingReply = !!command.waiting
      if (state.waitingReply) {
        syncWaitingOverlayVisibility()
        maybeScrollToBottom()
      }
      break
    case 'setReadOnly':
      state.readOnly = !!command.readOnly
      break
    case 'setTheme':
      applyTheme(command.theme)
      break
    case 'clear':
      state.sessionEpoch += 1
      state.messages = []
      state.waitingReply = false
      hideWaitingOverlayImmediate()
      break
  }
}
