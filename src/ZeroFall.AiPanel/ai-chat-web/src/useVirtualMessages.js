import { computed, ref } from 'vue'
import { state } from './chat-bridge.js'

const DEFAULT_HEIGHT = { user: 52, tool: 44, assistant: 54, deferred: 36, waiting: 44 }
const VIEWPORT_BUFFER_PX = 720
const HYDRATE_BUFFER_ITEMS = 8

function heightKey(message) {
  return message?.renderKey || message?.id
}

export function useVirtualMessages(getScrollRoot, onHydrateRange) {
  const heightById = ref(new Map())
  const visibleRange = ref({ start: 0, end: -1 })
  const lastHydrateKey = ref('')

  function estimateHeight(message) {
    if (!message) return DEFAULT_HEIGHT.assistant
    const cached = heightById.value.get(heightKey(message))
    if (cached) return cached
    if (message.waitingPlaceholder) return DEFAULT_HEIGHT.waiting
    if (message.deferred) return DEFAULT_HEIGHT.deferred
    return DEFAULT_HEIGHT[message.role] ?? DEFAULT_HEIGHT.assistant
  }

  function findIndexAtOffset(messages, offset) {
    if (messages.length === 0) return 0
    let y = 0
    for (let i = 0; i < messages.length; i++) {
      const h = estimateHeight(messages[i])
      if (y + h > offset) return i
      y += h
    }
    return messages.length - 1
  }

  function estimatedTotalHeight(messages) {
    let height = 0
    for (let i = 0; i < messages.length; i++)
      height += estimateHeight(messages[i])
    return height
  }

  function computeTailRange() {
    const root = getScrollRoot()
    const messages = state.messages
    if (!messages.length) {
      visibleRange.value = { start: 0, end: -1 }
      return
    }

    const viewportHeight = root?.clientHeight || 640
    const totalHeight = estimatedTotalHeight(messages)
    const startOffset = Math.max(0, totalHeight - viewportHeight - VIEWPORT_BUFFER_PX)
    const start = findIndexAtOffset(messages, startOffset)
    visibleRange.value = { start, end: messages.length - 1 }
    scheduleHydration()
  }

  function computeRange() {
    const root = getScrollRoot()
    const messages = state.messages
    if (!root || messages.length === 0) {
      visibleRange.value = { start: 0, end: -1 }
      return
    }

    const scrollTop = Math.max(0, root.scrollTop)
    const viewportBottom = scrollTop + root.clientHeight
    const start = findIndexAtOffset(messages, Math.max(0, scrollTop - VIEWPORT_BUFFER_PX))
    const end = findIndexAtOffset(messages, viewportBottom + VIEWPORT_BUFFER_PX)
    visibleRange.value = { start, end: Math.max(start, end) }
    scheduleHydration()
  }

    function scheduleHydration() {
    const messages = state.messages
    if (messages.length === 0) return

    const { start, end } = visibleRange.value
    if (end < start) return

    const hydrateStart = Math.max(0, start - HYDRATE_BUFFER_ITEMS)
    const hydrateEnd = Math.min(messages.length - 1, end + HYDRATE_BUFFER_ITEMS)

    let needs = false
    for (let i = hydrateStart; i <= hydrateEnd; i++) {
      if (messages[i]?.deferred) {
        needs = true
        break
      }
    }
    if (needs) {
      const key = `${hydrateStart}:${hydrateEnd}`
      if (lastHydrateKey.value !== key) {
        lastHydrateKey.value = key
        onHydrateRange?.(hydrateStart, hydrateEnd)
      }
    }

    trimHydratedPayloadsOutsideRange(hydrateStart, hydrateEnd)
  }

  function trimHydratedPayloadsOutsideRange(hydrateStart, hydrateEnd) {
    // 不在前端驱逐已 hydrate 的正文：驱逐会清空 blocks/content，且删除高度缓存会导致
    // 虚拟列表总高度塌陷，scrollTop 映射错位，视口内整页退化成 deferred 标签。
    void hydrateStart
    void hydrateEnd
  }

  function isPinnedIndex(index, message) {
    if (!message) return false
    if (message.waitingPlaceholder) return true
    if (message.isStreaming || message.isToolRunning || message.isThinking) return true
    if (state.autoFollow && index === state.messages.length - 1) return true
    return false
  }

  const visibleIndices = computed(() => {
    const messages = state.messages
    if (messages.length === 0) return []

    const { start, end } = visibleRange.value
    const indices = new Set()
    const lo = Math.max(0, start)
    const hi = end < 0 ? messages.length - 1 : Math.min(end, messages.length - 1)
    for (let i = lo; i <= hi; i++) indices.add(i)

    const tailScanStart = Math.max(0, lo - 2)
    for (let i = tailScanStart; i < messages.length; i++) {
      if (i >= lo && i <= hi)
        continue
      if (isPinnedIndex(i, messages[i]))
        indices.add(i)
    }

    return [...indices].sort((a, b) => a - b)
  })

  const topSpacerHeight = computed(() => {
    const messages = state.messages
    const { start } = visibleRange.value
    if (start <= 0) return 0
    let height = 0
    for (let i = 0; i < start && i < messages.length; i++)
      height += estimateHeight(messages[i])
    return height
  })

  const bottomSpacerHeight = computed(() => {
    const messages = state.messages
    const { end } = visibleRange.value
    if (end < 0 || end >= messages.length - 1) return 0
    let height = 0
    for (let i = end + 1; i < messages.length; i++)
      height += estimateHeight(messages[i])
    return height
  })

  function measureMessage(message, height) {
    const key = heightKey(message)
    if (!key || !Number.isFinite(height) || height <= 0) return
    const rounded = Math.round(height)
    const prev = heightById.value.get(key)
    if (prev === rounded) return
    heightById.value.set(key, rounded)

    if (message.isStreaming || message.isToolRunning || message.isThinking) {
      scheduleRangeUpdate()
      return
    }

    computeRange()
  }

  let rafId = 0
  function scheduleRangeUpdate() {
    if (rafId) return
    rafId = requestAnimationFrame(() => {
      rafId = 0
      computeRange()
    })
  }

  function resetVirtualState() {
    heightById.value = new Map()
    lastHydrateKey.value = ''
    if (state.initialScrollToEnd)
      computeTailRange()
    else
      visibleRange.value = { start: 0, end: -1 }
  }

  return {
    visibleIndices,
    topSpacerHeight,
    bottomSpacerHeight,
    scheduleRangeUpdate,
    resetVirtualState,
    measureMessage
  }
}
